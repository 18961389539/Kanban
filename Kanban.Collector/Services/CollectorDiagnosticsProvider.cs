using Kanban.Contracts.Dtos;
using Kanban.Core.Data;
using Kanban.Core.Services;
using Microsoft.Extensions.Logging;

namespace Kanban.Collector.Services;

/// <summary>
/// Collector 诊断快照：聚合采集循环 + 历史写入 + 连接状态，供运行监控页 Remote 模式展示。
/// </summary>
public sealed class CollectorDiagnosticsProvider
{
    private readonly PlcDataAcquisitionService _acquisition;
    private readonly HistoryService _history;
    private readonly PlcConnectionManager _connectionManager;
    private readonly IDeviceRepository _deviceRepository;
    private readonly ILogger<CollectorDiagnosticsProvider> _logger;

    public CollectorDiagnosticsProvider(
        PlcDataAcquisitionService acquisition,
        HistoryService history,
        PlcConnectionManager connectionManager,
        IDeviceRepository deviceRepository,
        ILogger<CollectorDiagnosticsProvider> logger)
    {
        _acquisition = acquisition;
        _history = history;
        _connectionManager = connectionManager;
        _deviceRepository = deviceRepository;
        _logger = logger;
    }

    public CollectorDiagnosticsDto GetSnapshot()
    {
        try
        {
            var acq = _acquisition.GetDiagnosticsSnapshot();
            var hist = _history.GetDiagnosticsSnapshot();

            return new CollectorDiagnosticsDto
            {
                CompletedCycles = acq.CompletedCycles,
                FailedCycles = acq.FailedCycles,
                LastCycleSucceeded = acq.LastCycleSucceeded,
                ConsecutiveFailureCycles = acq.ConsecutiveFailureCycles,
                LastFailureAt = acq.LastFailureAt,
                LastFailureMessage = acq.LastFailureMessage,
                LastCycleMilliseconds = acq.LastCycleMilliseconds,
                AverageCycleMilliseconds = acq.AverageCycleMilliseconds,
                MaxCycleMilliseconds = acq.MaxCycleMilliseconds,
                LastSuccessfulDevices = acq.LastSuccessfulDevices,
                ConfiguredDevices = acq.ConfiguredDevices,
                LastSuccessfulAt = acq.LastSuccessfulAt,
                SuccessfulCycles = acq.SuccessfulCycles,
                EstimatedReadOperations = acq.EstimatedReadOperations,
                BatchPlanRebuilds = acq.BatchPlanRebuilds,
                BatchPlanBuildMilliseconds = acq.BatchPlanBuildMilliseconds,
                DWordReadMilliseconds = acq.DWordReadMilliseconds,
                AlarmReadMilliseconds = acq.AlarmReadMilliseconds,
                DefectReadMilliseconds = acq.DefectReadMilliseconds,
                CounterAlarmReadMilliseconds = acq.CounterAlarmReadMilliseconds,
                HistoryWriteMilliseconds = acq.HistoryWriteMilliseconds,
                PendingHistoryCount = hist.PendingProductionCount,
                RecoveryFileExists = hist.RecoveryFileExists,
                RecoveryFileBytes = hist.RecoveryFileBytes,
                LastHistoryFlushAt = hist.LastFlushAt,
                HistoryFlushFailureCount = hist.FlushFailureCount,
                ProductionDatabaseBytes = hist.ProductionDatabaseBytes,
                ProductionWalBytes = hist.ProductionWalBytes,
                IsConnected = _connectionManager.IsConnected,
                IsRunning = _acquisition.IsRunning,
                ConnectionStatus = _connectionManager.ConnectionStatus,
                TotalDisconnectCount = _connectionManager.TotalDisconnectCount,
                ConsecutiveFailures = _connectionManager.ConsecutiveFailures,
                DisconnectedAt = _connectionManager.DisconnectedAt,
                ConfiguredReadAddressCount = _deviceRepository.GetDevicesSnapshot().Sum(CountConfiguredAddresses),
                DeviceStatuses = BuildDeviceStatuses(acq.LastSuccessfulDeviceIds),
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "生成 Collector 诊断快照失败");
            return new CollectorDiagnosticsDto();
        }
    }

    private static int CountConfiguredAddresses(Kanban.Core.Models.Device device)
    {
        var primary = new[]
        {
            device.OkCountAddress,
            device.NgCountAddress,
            device.StatusCountAddress,
            device.ProductionResetAddress,
            device.RecipeAddress,
        }.Count(address => !string.IsNullOrWhiteSpace(address));

        return primary
               + device.Alarms.Count(a => !string.IsNullOrWhiteSpace(a.PlcAddress))
               + device.Defects.Count(d => !string.IsNullOrWhiteSpace(d.PlcAddress))
               + device.CounterAlarms.Count(c => !string.IsNullOrWhiteSpace(c.PlcAddress));
    }

    private IReadOnlyList<CollectorDeviceStatusDto> BuildDeviceStatuses(IReadOnlySet<string> lastSuccessfulDeviceIds)
    {
        var result = new List<CollectorDeviceStatusDto>();
        foreach (var device in _deviceRepository.GetDevicesSnapshot())
        {
            _deviceRepository.RuntimeMap.TryGetValue(device.Id, out var runtime);
            result.Add(new CollectorDeviceStatusDto
            {
                DeviceId = device.Id,
                DeviceName = device.Name,
                StatusWord = runtime?.StatusWord ?? 0,
                OkProduction = runtime?.OkProduction ?? 0,
                NgProduction = runtime?.NgProduction ?? 0,
                ConfiguredAddressCount = CountConfiguredAddresses(device),
                LastCycleSucceeded = lastSuccessfulDeviceIds.Contains(device.Id),
            });
        }
        return result;
    }
}
