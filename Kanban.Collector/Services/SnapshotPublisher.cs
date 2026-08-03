using Kanban.Contracts.Dtos;
using Kanban.Contracts.Enums;
using Kanban.Core.Data;
using Kanban.Core.Models;
using Microsoft.Extensions.Logging;
using DeviceStatus = Kanban.Contracts.Enums.DeviceStatus;
using AlarmLevel = Kanban.Contracts.Enums.AlarmLevel;

namespace Kanban.Collector.Services;

/// <summary>
/// 快照发布器：把采集端内存状态（DeviceRepository.Devices/Runtimes）聚合为
/// <see cref="DeviceSnapshotDto"/> 并发布到 <see cref="SnapshotAggregator"/>。
/// 由 CollectorWorker 按采集节奏周期调用 <see cref="PublishAll"/>。
/// </summary>
public sealed class SnapshotPublisher
{
    private readonly DeviceRepository _deviceRepository;
    private readonly SnapshotAggregator _aggregator;
    private readonly ILogger<SnapshotPublisher> _logger;
    private long _seq;

    /// <summary>每设备上次已发布快照（增量比较基准；业务字段一致则跳过发布，静止设备不再每 500ms 推全量）。</summary>
    private readonly Dictionary<string, DeviceSnapshotDto> _lastPublished = new(StringComparer.Ordinal);

    public SnapshotPublisher(
        DeviceRepository deviceRepository,
        SnapshotAggregator aggregator,
        ILogger<SnapshotPublisher> logger)
    {
        _deviceRepository = deviceRepository;
        _aggregator = aggregator;
        _logger = logger;
    }

    /// <summary>
    /// 发布全部设备的当前快照（**增量**：业务字段与上次一致则跳过，静止设备不推；
    /// 首次/设备变化/新订阅者补发全量兜底）。设备无运行时状态（未采集到）时按 Unknown/待机发布。
    /// 客户端以 upsert 语义消费（OnSnapshot 按 DeviceId 覆盖），增量不改变 DTO 契约。
    /// </summary>
    public void PublishAll()
    {
        var devices = _deviceRepository.GetDevicesSnapshot();
        var runtimes = _deviceRepository.GetRuntimesSnapshot();
        var runtimeById = runtimes.ToDictionary(r => r.DeviceId);

        // 清理已删除设备的增量基准（设备被裁剪后残留条目无意义且占内存）
        if (_lastPublished.Count != devices.Count)
        {
            var liveIds = devices.Select(d => d.Id).ToHashSet();
            foreach (var staleId in _lastPublished.Keys.Where(id => !liveIds.Contains(id)).ToList())
                _lastPublished.Remove(staleId);
        }

        foreach (var device in devices)
        {
            try
            {
                var snapshot = ToSnapshot(device, runtimeById.TryGetValue(device.Id, out var rt) ? rt : null);
                if (_lastPublished.TryGetValue(device.Id, out var last) && SameSnapshot(last, snapshot))
                    continue; // 静止设备：业务字段无变化，跳过（省序列化与带宽）
                _lastPublished[device.Id] = snapshot;
                _aggregator.Publish(snapshot);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref CollectorMetrics.PublishErrorCount);
                _logger.LogError(ex, "发布设备 {DeviceId} 快照失败", device.Id);
            }
        }
        Interlocked.Increment(ref CollectorMetrics.SnapshotPublishCount);
    }

    /// <summary>
    /// 业务字段等价比较（排除 Timestamp/Seq——两者每帧都变，不代表业务变化）。
    /// ActiveAlarms 是引用类型属性，record 默认按引用比较，这里按内容逐一比较。
    /// internal：供 MainAPP.Tests 直测（增量发布的核心判定，服务端零测试盲区补位）。
    /// </summary>
    internal static bool SameSnapshot(DeviceSnapshotDto a, DeviceSnapshotDto b)
    {
        if (a.DeviceId != b.DeviceId || a.DeviceName != b.DeviceName || a.Status != b.Status
            || a.StatusWord != b.StatusWord || a.OkProduction != b.OkProduction || a.NgProduction != b.NgProduction
            || a.TotalOkProduction != b.TotalOkProduction || a.TotalNgProduction != b.TotalNgProduction
            || a.RunTime != b.RunTime || a.AlarmTime != b.AlarmTime || a.PausedTime != b.PausedTime
            || a.QualityRate != b.QualityRate || a.PerformanceRate != b.PerformanceRate
            || a.AvailabilityRate != b.AvailabilityRate || a.Oee != b.Oee || a.TargetCycle != b.TargetCycle
            || a.Removed != b.Removed)
            return false;
        if (a.ActiveAlarms.Count != b.ActiveAlarms.Count) return false;
        for (int i = 0; i < a.ActiveAlarms.Count; i++)
            if (a.ActiveAlarms[i] != b.ActiveAlarms[i]) return false;
        return true;
    }

    private DeviceSnapshotDto ToSnapshot(Device device, DeviceRuntime? runtime)
    {
        var status = runtime is null ? DeviceStatus.Unknown : (DeviceStatus)runtime.StatusWord;

        var activeAlarms = device.Alarms
            .Where(a => a.StartTime != default && a.EndTime == default)
            .Select(a => new ActiveAlarmDto
            {
                AlarmId = a.Id,
                Name = a.Name,
                PlcAddress = a.PlcAddress,
                Description = a.Description,
                Level = (AlarmLevel)a.Level,
                StartTime = a.StartTime,
            })
            .ToArray();

        return new DeviceSnapshotDto
        {
            DeviceId = device.Id,
            DeviceName = device.Name,
            Status = status,
            StatusWord = runtime?.StatusWord ?? 0,
            OkProduction = runtime?.OkProduction ?? 0,
            NgProduction = runtime?.NgProduction ?? 0,
            TotalOkProduction = runtime?.TotalOkProduction ?? 0,
            TotalNgProduction = runtime?.TotalNgProduction ?? 0,
            RunTime = runtime?.RunTime ?? 0,
            AlarmTime = runtime?.AlarmTime ?? 0,
            PausedTime = runtime?.PausedTime ?? 0,
            QualityRate = runtime?.QualityRate ?? 0,
            PerformanceRate = runtime?.PerformanceRate ?? 0,
            AvailabilityRate = runtime?.AvailabilityRate ?? 0,
            Oee = runtime?.Oee ?? 0,
            TargetCycle = device.TargetCycle,
            ActiveAlarms = activeAlarms,
            Timestamp = DateTime.Now,
            Seq = Interlocked.Increment(ref _seq),
        };
    }
}
