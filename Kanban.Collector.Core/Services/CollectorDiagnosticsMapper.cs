using Kanban.Collector.Core.Models;
using Kanban.Contracts.Dtos;
using Kanban.Contracts.Enums;

namespace Kanban.Collector.Core.Services;

/// <summary>
/// 把采集循环、历史写入和连接状态收成同一份 <see cref="CollectorDiagnosticsDto"/>。
/// 本地模式的监控页和 Collector 的诊断接口都走这里，避免两套字段赋值各写一遍。
/// </summary>
public static class CollectorDiagnosticsMapper
{
    public static CollectorDiagnosticsDto Create(
        AcquisitionDiagnosticsSnapshot acquisition,
        HistoryDiagnosticsSnapshot history,
        bool isConnected,
        bool isRunning,
        string connectionStatus,
        int totalDisconnectCount,
        int consecutiveConnectionFailures,
        DateTime? disconnectedAt,
        IReadOnlyList<Device> devices,
        Func<string, DeviceRuntime?>? runtimeOf,
        SystemResourceSnapshot? resources,
        IReadOnlyList<PlcRuntimeSessionDiagnosticsSnapshot>? profiles = null,
        DateTime? clock = null)
    {
        var now = clock ?? DateTime.Now;
        var deviceStatuses = new CollectorDeviceStatusDto[devices.Count];
        var configuredAddresses = 0;
        for (var index = 0; index < devices.Count; index++)
        {
            var device = devices[index];
            var addressCount = CountConfiguredAddresses(device);
            configuredAddresses += addressCount;
            var runtimeValue = runtimeOf?.Invoke(device.Id);
            deviceStatuses[index] = new CollectorDeviceStatusDto
            {
                DeviceId = device.Id,
                DeviceName = device.Name,
                StatusWord = runtimeValue?.StatusWord ?? 0,
                OfflineCause = runtimeValue?.OfflineCause ?? OfflineCause.None,
                OkProduction = runtimeValue?.OkProduction ?? 0,
                NgProduction = runtimeValue?.NgProduction ?? 0,
                ConfiguredAddressCount = addressCount,
                LastCycleSucceeded = acquisition.LastSuccessfulDeviceIds.Contains(device.Id),
            };
        }

        return new CollectorDiagnosticsDto
        {
            CompletedCycles = acquisition.CompletedCycles,
            FailedCycles = acquisition.FailedCycles,
            LastCycleSucceeded = acquisition.LastCycleSucceeded,
            ConsecutiveFailureCycles = acquisition.ConsecutiveFailureCycles,
            LastFailureAt = acquisition.LastFailureAt,
            LastFailureMessage = acquisition.LastFailureMessage,
            LastCycleMilliseconds = acquisition.LastCycleMilliseconds,
            AverageCycleMilliseconds = acquisition.AverageCycleMilliseconds,
            MaxCycleMilliseconds = acquisition.MaxCycleMilliseconds,
            CycleP95Milliseconds = acquisition.CycleP95Milliseconds,
            CycleP99Milliseconds = acquisition.CycleP99Milliseconds,
            LastSuccessfulDevices = acquisition.LastSuccessfulDevices,
            ConfiguredDevices = acquisition.ConfiguredDevices,
            LastSuccessfulAt = acquisition.LastSuccessfulAt,
            SuccessfulCycles = acquisition.SuccessfulCycles,
            EstimatedReadOperations = acquisition.EstimatedReadOperations,
            BatchPlanRebuilds = acquisition.BatchPlanRebuilds,
            BatchPlanBuildMilliseconds = acquisition.BatchPlanBuildMilliseconds,
            DataSourceReaderDiagnostics = MapReaders(acquisition.DataSourceReaderDiagnostics),
            DWordReadMilliseconds = acquisition.DWordReadMilliseconds,
            AlarmReadMilliseconds = acquisition.AlarmReadMilliseconds,
            DefectReadMilliseconds = acquisition.DefectReadMilliseconds,
            CounterAlarmReadMilliseconds = acquisition.CounterAlarmReadMilliseconds,
            HistoryWriteMilliseconds = acquisition.HistoryWriteMilliseconds,
            RecentCycleSamples = acquisition.RecentCycleSamples
                .Select(sample => new CollectorCycleSampleDto
                {
                    Timestamp = sample.Timestamp,
                    Milliseconds = sample.Milliseconds,
                })
                .ToArray(),
            PendingHistoryCount = history.PendingProductionCount,
            RecoveryFileExists = history.RecoveryFileExists,
            RecoveryFileBytes = history.RecoveryFileBytes,
            LastHistoryFlushAt = history.LastFlushAt,
            HistoryFlushFailureCount = history.FlushFailureCount,
            ProductionQueuePeakCount = history.ProductionQueuePeakCount,
            ProductionOverflowCount = history.ProductionOverflowCount,
            ProductionFlushP95Milliseconds = history.ProductionFlushP95Milliseconds,
            ProductionFlushP99Milliseconds = history.ProductionFlushP99Milliseconds,
            ProductionDatabaseBytes = history.ProductionDatabaseBytes,
            ProductionWalBytes = history.ProductionWalBytes,
            PendingDataSourceCount = history.PendingDataSourceCount,
            DataSourceQueuePeakCount = history.DataSourceQueuePeakCount,
            DataSourceOverflowCount = history.DataSourceOverflowCount,
            DataSourceRecoveryFileExists = history.DataSourceRecoveryFileExists,
            DataSourceRecoveryFileBytes = history.DataSourceRecoveryFileBytes,
            DataSourceRecoveryFileLines = history.DataSourceRecoveryFileLines,
            LastDataSourceFlushAt = history.LastDataSourceFlushAt,
            DataSourceFlushFailureCount = history.DataSourceFlushFailureCount,
            DataSourceTotalFlushedCount = history.DataSourceTotalFlushedCount,
            DataSourceFlushP95Milliseconds = history.DataSourceFlushP95Milliseconds,
            DataSourceFlushP99Milliseconds = history.DataSourceFlushP99Milliseconds,
            DataSourceDatabaseBytes = history.DataSourceDatabaseBytes,
            DataSourceWalBytes = history.DataSourceWalBytes,
            TotalDatabaseBytes = history.TotalDatabaseBytes,
            TotalWalBytes = history.TotalWalBytes,
            IsConnected = isConnected,
            IsRunning = isRunning,
            ConnectionStatus = connectionStatus,
            TotalDisconnectCount = totalDisconnectCount,
            ConsecutiveFailures = consecutiveConnectionFailures,
            DisconnectedAt = disconnectedAt,
            DisconnectDurationSeconds = disconnectedAt is { } at
                ? Math.Max(0, (now - at).TotalSeconds)
                : null,
            ProcessResourcesAvailable = resources is not null,
            CpuUsagePercent = resources?.CpuUsagePercent ?? 0,
            GpuUsagePercent = resources?.GpuUsagePercent ?? 0,
            GpuAvailable = resources?.GpuAvailable ?? false,
            ProcessMemoryMb = resources?.ProcessMemoryMb ?? 0,
            AvailableMemoryMb = resources?.AvailableMemoryMb ?? 0,
            ProcessUptimeSeconds = resources is null ? 0 : (long)resources.ProcessUptime.TotalSeconds,
            ProcessThreadCount = resources?.ThreadCount ?? 0,
            ProcessHandleCount = resources?.HandleCount ?? 0,
            FreeDiskGb = resources?.FreeDiskGb ?? 0,
            ConfiguredReadAddressCount = configuredAddresses,
            DeviceStatuses = deviceStatuses,
            Profiles = MapProfiles(profiles),
        };
    }

    private static CollectorReaderDiagnosticsDto[] MapReaders(IReadOnlyList<DataSourceReaderDiagnosticsSnapshot> readers)
        => readers.Select(reader => new CollectorReaderDiagnosticsDto
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
        }).ToArray();

    private static CollectorProfileDiagnosticsDto[] MapProfiles(IReadOnlyList<PlcRuntimeSessionDiagnosticsSnapshot>? profiles)
        => (profiles ?? [])
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
            .ToArray();

    /// <summary>与设备管理页 <c>DeviceConfigValidator.GetDeviceAddresses</c> 计数口径一致，含数据源地址。</summary>
    private static int CountConfiguredAddresses(Device device)
    {
        var count = 0;
        if (!string.IsNullOrWhiteSpace(device.OkCountAddress)) count++;
        if (!string.IsNullOrWhiteSpace(device.NgCountAddress)) count++;
        if (!string.IsNullOrWhiteSpace(device.StatusCountAddress)) count++;
        if (!string.IsNullOrWhiteSpace(device.ProductionResetAddress)) count++;
        if (!string.IsNullOrWhiteSpace(device.RecipeAddress)) count++;
        count += device.Alarms.Count(alarm => !string.IsNullOrWhiteSpace(alarm.PlcAddress));
        count += device.Defects.Count(defect => !string.IsNullOrWhiteSpace(defect.PlcAddress));
        count += device.CounterAlarms.Count(counter => !string.IsNullOrWhiteSpace(counter.PlcAddress));
        foreach (var source in device.Sources)
        {
            if (!string.IsNullOrWhiteSpace(source.TriggerAddress)) count++;
            count += source.Values.Count(value => !string.IsNullOrWhiteSpace(value.PlcAddress));
        }

        return count;
    }
}
