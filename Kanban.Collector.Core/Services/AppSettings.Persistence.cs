using Kanban.Localization;
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
    /// 获取配置目录下指定文件的完整路径，基于 DataRoot（%APPDATA%/Kanban）。
    /// </summary>
    public string GetFilePath(string fileName)
    {
        return Path.Combine(DataRoot, ConfigDirectory, fileName);
    }

    /// <summary>
    /// 确保配置目录存在
    /// </summary>
    public void EnsureDirectory()
    {
        var fullPath = Path.Combine(DataRoot, ConfigDirectory);
        if (!Directory.Exists(fullPath))
        {
            Directory.CreateDirectory(fullPath);
        }
    }

    /// <summary>
    /// 保存本类全部设置属性（序列化整个 AppSettings 实例）。
    /// 采用原子写入（写临时文件 → 重命名），避免断电/强制关机时产生半截 JSON 导致配置损坏。
    /// </summary>
    public void Save()
    {
        SynchronizeDefaultConnectionProfile();
        WriteSettingsFile(this);
        // 连接档案/PLC 品牌等配置变更可能改变地址解析规则（codec），失效解析缓存
        PlcAddressParser.ClearCache();
    }
    /// <summary>
    /// 把指定实例的完整设置序列化到 settings.json（原子写入）。
    /// 供 Remote 设置同步等"先落盘、后生效"流程使用：写入候选配置成功后，
    /// 调用方才把候选值应用到运行实例，避免磁盘写入失败时内存/驱动已变、磁盘未变的分裂状态。
    /// </summary>
    internal static void WriteSettingsFile(AppSettings candidate)
    {
        candidate.SynchronizeDefaultConnectionProfile();
        candidate.EnsureDirectory();
        candidate.SchemaVersion = CurrentSchemaVersion;
        var json = JsonSerializer.Serialize(candidate, JsonOptions);
        AtomicFileWriter.Write(candidate.SettingsFilePath, json);
    }

    /// <summary>
    /// 加载本类全部设置属性
    /// </summary>
    public void Load()
    {
        if (!File.Exists(SettingsFilePath)) return;

        try
        {
            var json = new SettingsMigrationRunner().Migrate(File.ReadAllText(SettingsFilePath));
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (settings != null)
            {
                settings.EnsureConnectionProfiles();
                SchemaVersion = CurrentSchemaVersion;
                ConnectionProfiles = settings.ConnectionProfiles ?? new ObservableCollection<ConnectionProfile>();
                PlcConfig = settings.PlcConfig ?? new PlcConfig();
                EnsureConnectionProfiles();
                // ConfigDirectory 固定为程序运行目录下的 "Config"，不从 settings.json 读回，
                // 避免旧配置把数据目录指向其他位置导致数据分散
                SettingsFileName = settings.SettingsFileName;
                PollingIntervalMs = settings.PollingIntervalMs;
                HistoryWriteIntervalScans = settings.HistoryWriteIntervalScans;
                PlcBatchReadMaxLength = settings.PlcBatchReadMaxLength;
                PlcBatchReadMaxGapSlots = settings.PlcBatchReadMaxGapSlots;
                DashboardRefreshIntervalMs = settings.DashboardRefreshIntervalMs;
                AppTitle = string.IsNullOrWhiteSpace(settings.AppTitle) ? "生产看板" : settings.AppTitle;
                var legacyLanguage = Enum.IsDefined(settings.Language) ? settings.Language : AppLanguage.Zh;
                var loadedLanguageCode = LocalizationCatalog.IsSupported(settings.LanguageCode)
                    ? LocalizationCatalog.Normalize(settings.LanguageCode)
                    : LegacyLanguageCode(legacyLanguage);
                Language = LegacyLanguage(loadedLanguageCode);
                LanguageCode = loadedLanguageCode;
                IsDarkTheme = settings.IsDarkTheme;
                UiScale = settings.UiScale;
                EnableAlarmSound = settings.EnableAlarmSound;
                EnableAutomaticDailyReport = settings.EnableAutomaticDailyReport;
                AutomaticDailyReportTime = settings.AutomaticDailyReportTime;
                DataMode = Enum.IsDefined(settings.DataMode) ? settings.DataMode : KanbanDataMode.Local;
                if (!string.IsNullOrWhiteSpace(settings.CollectorHubUrl))
                    CollectorHubUrl = settings.CollectorHubUrl;
                RunMode = Enum.IsDefined(settings.RunMode) ? settings.RunMode : KanbanRunMode.Full;
                // 旧配置没有该字段时保持默认开启，避免升级后主页静默不转。
                DisplayCarouselEnabled = JsonCarouselEnabledOrDefault(json);
                HasCompletedFirstRunGuide = settings.HasCompletedFirstRunGuide;
                // 保证至少一个班次：若加载到空集合或 null，回退到默认两个班次
                Shifts = (settings.Shifts is { Count: > 0 } shifts)
                    ? shifts
                    : GetDefaultShifts();
            }
        }
        catch (UnsupportedSettingsVersionException ex)
        {
            Log.Error(ex, "settings.json 版本过高，保留原文件并停止加载");
            LoadErrorMessage = $"settings.json 版本 {ex.Version} 高于当前程序支持的版本 {ex.CurrentVersion}，" +
                               "请升级程序后再使用此配置文件。原文件已保留。";
        }
        catch (Exception ex)
        {
            // 文件损坏：备份原文件供用户恢复（参照 DeviceRepository.LoadAll 的 .corrupt 机制）
            Log.Warning(ex, "settings.json 解析失败，将备份原文件并回退默认值");
            var corruptPath = SettingsFilePath + ".corrupt";
            try
            {
                if (File.Exists(corruptPath))
                    File.Delete(corruptPath);
                File.Move(SettingsFilePath, corruptPath);
            }
            catch (Exception backupEx)
            {
                Log.Warning(backupEx, "settings.json 备份为 .corrupt 失败，原文件保留原位");
            }

            LoadErrorMessage = $"配置文件 settings.json 损坏（{ex.Message}），已备份为 settings.json.corrupt。" +
                               "已恢复为默认设置，请重新保存配置。";
            // 使用默认值继续运行，不崩溃
            Shifts = GetDefaultShifts();
        }
    }

    /// <summary>旧 settings.json 没有该字段时默认开，避免升级后主页静默不转。</summary>
    internal static bool JsonCarouselEnabledOrDefault(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("DisplayCarouselEnabled", out var flag)
                && (flag.ValueKind is JsonValueKind.True or JsonValueKind.False))
                return flag.GetBoolean();
        }
        catch (JsonException)
        {
        }

        return true;
    }

    /// <summary>
    /// 默认班次配置：白班 08:00-20:00 + 夜班 20:00-次日 08:00（跨天）
    /// </summary>
    internal static ObservableCollection<ShiftConfig> GetDefaultShifts() => new()
    {
        new ShiftConfig { Name = "白班", StartTime = new TimeSpan(8, 0, 0), EndTime = new TimeSpan(20, 0, 0) },
        new ShiftConfig { Name = "夜班", StartTime = new TimeSpan(20, 0, 0), EndTime = new TimeSpan(8, 0, 0) }
    };
}
