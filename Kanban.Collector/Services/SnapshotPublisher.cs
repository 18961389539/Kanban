using Kanban.Contracts.Dtos;
using Kanban.Contracts.Enums;
using MainAPP.Data;
using MainAPP.Models;
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
    /// 发布全部设备的当前快照。设备无运行时状态（未采集到）时按 Unknown/待机发布。
    /// </summary>
    public void PublishAll()
    {
        var devices = _deviceRepository.GetDevicesSnapshot();
        var runtimes = _deviceRepository.GetRuntimesSnapshot();
        var runtimeById = runtimes.ToDictionary(r => r.DeviceId);

        foreach (var device in devices)
        {
            try
            {
                var snapshot = ToSnapshot(device, runtimeById.TryGetValue(device.Id, out var rt) ? rt : null);
                _aggregator.Publish(snapshot);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "发布设备 {DeviceId} 快照失败", device.Id);
            }
        }
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
