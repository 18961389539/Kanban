using System.IO;
using System.Collections.ObjectModel;
using System.Text.Json;
using Kanban.Collector.Core.Localization;
using Kanban.Collector.Core.Models;
using Serilog;

namespace Kanban.Collector.Core.Services;

public partial class AppSettings
{
    /// <summary>
    /// 启动时验证配置完整性，返回所有验证错误。空列表表示通过。
    /// 启动阶段调用（App.OnStartup 中 Load 后立即调用）：
    /// - PLC IP 必须为合法 IPv4（启动 PLC 连接前拦截，避免无效 IP 触发连接超时再回退）
    /// - PLC 端口必须在 1-65535 范围内
    /// - 轮询间隔必须为正数（≥1ms），避免 0 触发 CPU 满载
    /// - 历史写入间隔必须为正数（≥1 次），避免 0 触发每轮写库
    /// - 主页刷新间隔必须为正数（≥1ms），避免 0 触发 UI 满刷新
    /// - 班次配置至少一个，且每个班次名称非空、起止时间合法
    /// </summary>
    public List<string> Validate()
    {
        List<string> errors = [];

        if (ConnectionProfiles is null || ConnectionProfiles.Count == 0)
            EnsureConnectionProfiles();

        ValidateConnectionProfiles(ConnectionProfiles!, errors);
        ValidatePlcConfig(PlcConfig, errors, profileId: null);

        // 间隔类配置：必须为正数；同时设上限防止 Remote 设置同步传入极端值（如 1ms 轮询造成 CPU 满载）
        if (PollingIntervalMs < 10 || PollingIntervalMs > 60000)
            errors.Add(string.Format(ValidationMessages.PollingIntervalInvalid, PollingIntervalMs));
        if (HistoryWriteIntervalScans < 1 || HistoryWriteIntervalScans > 3600)
            errors.Add(string.Format(ValidationMessages.HistoryWriteIntervalInvalid, HistoryWriteIntervalScans));
        if (DashboardRefreshIntervalMs < 1)
            errors.Add(string.Format(ValidationMessages.DashboardRefreshIntervalInvalid, DashboardRefreshIntervalMs));
        // 批量读取参数上限：超出后 PlcScanPipeline 的 Math.Clamp 上限会抛 ArgumentOutOfRangeException
        if (PlcBatchReadMaxLength < 1 || PlcBatchReadMaxLength > 1024)
            errors.Add(string.Format(ValidationMessages.PlcBatchReadMaxLengthInvalid, PlcBatchReadMaxLength));
        if (PlcBatchReadMaxGapSlots < 0 || PlcBatchReadMaxGapSlots > 256)
            errors.Add(string.Format(ValidationMessages.PlcBatchReadMaxGapSlotsInvalid, PlcBatchReadMaxGapSlots));

        // 班次配置验证
        if (Shifts is null || Shifts.Count == 0)
        {
            errors.Add(ValidationMessages.NoShiftsConfigured);
        }
        else
        {
            for (int i = 0; i < Shifts.Count; i++)
            {
                var s = Shifts[i];
                if (string.IsNullOrWhiteSpace(s.Name))
                    errors.Add(string.Format(ValidationMessages.ShiftNameEmpty, i + 1));
                // ShiftConfig.Contains 自身验证起止时间（跨天班次合法），这里只检查零时长
                if (s.StartTime == s.EndTime)
                    errors.Add(string.Format(ValidationMessages.ShiftStartEndEqual, s.Name, s.StartTime));
            }
        }

        return errors;
    }

    private static void ValidateConnectionProfiles(
        IReadOnlyList<ConnectionProfile> profiles,
        List<string> errors)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < profiles.Count; index++)
        {
            var profile = profiles[index];
            if (profile is null)
            {
                errors.Add(string.Format(ValidationMessages.ConnectionProfileEmpty, index + 1));
                continue;
            }

            var profileId = profile.Id?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(profileId))
            {
                errors.Add(string.Format(ValidationMessages.ConnectionProfileIdEmpty, index + 1));
            }
            else if (!ids.Add(profileId))
            {
                errors.Add(string.Format(ValidationMessages.ConnectionProfileIdDuplicate, profileId));
            }

            var profileLabel = string.IsNullOrWhiteSpace(profileId)
                ? (index + 1).ToString()
                : profileId;
            if (string.IsNullOrWhiteSpace(profile.Name))
                errors.Add(string.Format(ValidationMessages.ConnectionProfileNameEmpty, profileLabel));
            if (profile.Config is null)
            {
                errors.Add(string.Format(ValidationMessages.ConnectionProfileConfigMissing, profileLabel));
                continue;
            }

            if (!string.Equals(profileId, ConnectionProfile.DefaultId, StringComparison.OrdinalIgnoreCase))
                ValidatePlcConfig(profile.Config, errors, profileId);
        }
    }

    private static void ValidatePlcConfig(PlcConfig config, List<string> errors, string? profileId)
    {
        void Add(string message)
        {
            errors.Add(string.IsNullOrWhiteSpace(profileId)
                || string.Equals(profileId, ConnectionProfile.DefaultId, StringComparison.OrdinalIgnoreCase)
                ? message
                : $"连接档案 '{profileId}'：{message}");
        }

        // PLC IP 验证：System.Net.IPAddress.TryParse 接受 "1.2.3.4" 但也接受 "1"、"::1" 等，
        // 用 AddressFamily 限定 InterNetwork（IPv4），与三菱 MC 协议一致。
        var ip = config.IpAddress ?? string.Empty;
        if (string.IsNullOrWhiteSpace(ip))
        {
            Add(ValidationMessages.PlcIpEmpty);
        }
        else if (!System.Net.IPAddress.TryParse(ip, out var addr)
                 || addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            Add(string.Format(ValidationMessages.PlcIpInvalid, ip));
        }

        // PLC 端口验证：1-65535
        if (config.Port < 1 || config.Port > 65535)
            Add(string.Format(ValidationMessages.PlcPortOutOfRange, config.Port));
        if (!Enum.IsDefined(config.Brand))
            Add(string.Format(ValidationMessages.PlcBrandInvalid, config.Brand));
        else
        {
            var brandErrors = new List<string>();
            PlcBrandDescriptors.CreateDefault().Resolve(config.Brand).Validate(config, brandErrors);
            foreach (var brandError in brandErrors)
                Add(brandError);
        }
        if (config.TimeoutMs < 100 || config.TimeoutMs > 60000)
            Add(string.Format(ValidationMessages.PlcTimeoutOutOfRange, config.TimeoutMs));
    }
}
