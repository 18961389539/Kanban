using Kanban.Contracts.Dtos;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Services;
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
    private readonly IPlcRuntimeSessionManager? _runtimeSessions;
    private readonly IDeviceRepository _deviceRepository;
    private readonly ILogger<CollectorDiagnosticsProvider> _logger;

    public CollectorDiagnosticsProvider(
        PlcDataAcquisitionService acquisition,
        HistoryService history,
        PlcConnectionManager connectionManager,
        IDeviceRepository deviceRepository,
        ILogger<CollectorDiagnosticsProvider> logger,
        IPlcRuntimeSessionManager? runtimeSessions = null)
    {
        _acquisition = acquisition;
        _history = history;
        _connectionManager = connectionManager;
        _runtimeSessions = runtimeSessions;
        _deviceRepository = deviceRepository;
        _logger = logger;
    }

    public CollectorDiagnosticsDto GetSnapshot()
    {
        try
        {
            var acq = _acquisition.GetDiagnosticsSnapshot();
            var hist = _history.GetDiagnosticsSnapshot();
            CollectorMetrics.UpdateDiagnostics(acq, hist);
            CollectorMetrics.UpdateReaderDiagnostics(acq.DataSourceReaderDiagnostics);
            var profileDiagnostics = _runtimeSessions?.GetDiagnosticsSnapshot() ?? [];
            CollectorMetrics.UpdateRuntimeSessionDiagnostics(profileDiagnostics);

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
                CycleP95Milliseconds = acq.CycleP95Milliseconds,
                CycleP99Milliseconds = acq.CycleP99Milliseconds,
                LastSuccessfulDevices = acq.LastSuccessfulDevices,
                ConfiguredDevices = acq.ConfiguredDevices,
                LastSuccessfulAt = acq.LastSuccessfulAt,
                SuccessfulCycles = acq.SuccessfulCycles,
                EstimatedReadOperations = acq.EstimatedReadOperations,
                BatchPlanRebuilds = acq.BatchPlanRebuilds,
                BatchPlanBuildMilliseconds = acq.BatchPlanBuildMilliseconds,
                DataSourceReaderDiagnostics = acq.DataSourceReaderDiagnostics
                    .Select(reader => new CollectorReaderDiagnosticsDto
                    {
                        ProtocolKey = reader.ProtocolKey,
                        ReaderType = reader.ReaderType,
                        Priority = reader.Priority,
                        ResolveCount = reader.ResolveCount,
                        ValidationCount = reader.ValidationCount,
                        ValidationSuccessCount = reader.ValidationSuccessCount,
                        ValidationFailureCount = reader.ValidationFailureCount,
                        ReadCount = reader.ReadCount,
                        ReadSuccessCount = reader.ReadSuccessCount,
                        ReadFailureCount = reader.ReadFailureCount,
                        ReadP95Milliseconds = reader.ReadP95Milliseconds,
                        ReadP99Milliseconds = reader.ReadP99Milliseconds,
                        AcknowledgementCount = reader.AcknowledgementCount,
                        AcknowledgementSuccessCount = reader.AcknowledgementSuccessCount,
                        AcknowledgementFailureCount = reader.AcknowledgementFailureCount,
                        AcknowledgementP95Milliseconds = reader.AcknowledgementP95Milliseconds,
                        AcknowledgementP99Milliseconds = reader.AcknowledgementP99Milliseconds,
                    })
                    .ToArray(),
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
                ProductionQueuePeakCount = hist.ProductionQueuePeakCount,
                ProductionOverflowCount = hist.ProductionOverflowCount,
                ProductionFlushP95Milliseconds = hist.ProductionFlushP95Milliseconds,
                ProductionFlushP99Milliseconds = hist.ProductionFlushP99Milliseconds,
                ProductionDatabaseBytes = hist.ProductionDatabaseBytes,
                ProductionWalBytes = hist.ProductionWalBytes,
                PendingDataSourceCount = hist.PendingDataSourceCount,
                DataSourceQueuePeakCount = hist.DataSourceQueuePeakCount,
                DataSourceOverflowCount = hist.DataSourceOverflowCount,
                DataSourceRecoveryFileExists = hist.DataSourceRecoveryFileExists,
                DataSourceRecoveryFileBytes = hist.DataSourceRecoveryFileBytes,
                DataSourceRecoveryFileLines = hist.DataSourceRecoveryFileLines,
                LastDataSourceFlushAt = hist.LastDataSourceFlushAt,
                DataSourceFlushFailureCount = hist.DataSourceFlushFailureCount,
                DataSourceTotalFlushedCount = hist.DataSourceTotalFlushedCount,
                DataSourceFlushP95Milliseconds = hist.DataSourceFlushP95Milliseconds,
                DataSourceFlushP99Milliseconds = hist.DataSourceFlushP99Milliseconds,
                DataSourceDatabaseBytes = hist.DataSourceDatabaseBytes,
                DataSourceWalBytes = hist.DataSourceWalBytes,
                TotalDatabaseBytes = hist.TotalDatabaseBytes,
                TotalWalBytes = hist.TotalWalBytes,
                IsConnected = _connectionManager.IsConnected,
                IsRunning = _acquisition.IsRunning,
                ConnectionStatus = _connectionManager.ConnectionStatus,
                TotalDisconnectCount = _connectionManager.TotalDisconnectCount,
                ConsecutiveFailures = _connectionManager.ConsecutiveFailures,
                DisconnectedAt = _connectionManager.DisconnectedAt,
                ConfiguredReadAddressCount = _deviceRepository.GetDevicesSnapshot().Sum(CountConfiguredAddresses),
                DeviceStatuses = BuildDeviceStatuses(acq.LastSuccessfulDeviceIds),
                Profiles = profileDiagnostics
                    .Select(profile => new CollectorProfileDiagnosticsDto
                    {
                        ProfileId = profile.ProfileId,
                        IpAddress = profile.IpAddress,
                        Port = profile.Port,
                        ProtocolKey = profile.ProtocolKey,
                        Brand = profile.Brand.ToString(),
                        IsConnected = profile.IsConnected,
                        ConnectionStatus = profile.ConnectionStatus,
                        ConsecutiveConnectionFailures = profile.ConsecutiveFailures,
                        TotalDisconnectCount = profile.TotalDisconnectCount,
                        DisconnectedAt = profile.DisconnectedAt,
                        LastDisconnectDuration = profile.LastDisconnectDuration,
                        LastSuccessfulAcquisitionAt = profile.LastSuccessfulAcquisitionAt,
                        LastAcquisitionFailureAt = profile.LastAcquisitionFailureAt,
                        ConsecutiveAcquisitionFailures = profile.ConsecutiveAcquisitionFailures,
                        AcquisitionFailureCount = profile.AcquisitionFailureCount,
                        LastAcquisitionFailureMessage = profile.LastAcquisitionFailureMessage,
                    })
                    .ToArray(),
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "生成 Collector 诊断快照失败");
            return new CollectorDiagnosticsDto();
        }
    }

    private static int CountConfiguredAddresses(Kanban.Collector.Core.Models.Device device)
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
