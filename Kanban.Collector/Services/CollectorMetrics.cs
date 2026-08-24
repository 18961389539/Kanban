using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;

namespace Kanban.Collector.Services;

/// <summary>
/// Collector 进程级运行指标（可观测性）：采集/推送/事件计数 + 订阅者峰值 + 错误计数。
/// 通过 GET /metrics 以 text/plain 输出，供运维快速判断"服务是否在推/是否有积压/是否在报错"。
/// 全部为 Interlocked 原子计数，无锁、无分配，可在高频路径（500ms 快照/边沿事件）安全调用。
/// </summary>
public static class CollectorMetrics
{
    // ──────────── 采集与推送 ────────────
    public static long SnapshotPublishCount;      // 快照全量发布次数（500ms/次）
    public static long MetaPublishCount;          // 元数据发布次数（5s/次）
    public static long AlarmEventPublishCount;    // 报警边沿事件数
    public static long StatusEventPublishCount;   // 状态转换事件数
    public static long AcquisitionCycleCount;     // 采集轮询周期数

    // ──────────── 连接规模 ────────────
    public static long SnapshotSubscriberPeak;    // 快照订阅者峰值
    public static long EventSubscriberPeak;       // 事件订阅者峰值
    public static long MetaSubscriberPeak;        // 元数据订阅者峰值

    // ──────────── 错误 ────────────
    public static long PublishErrorCount;         // 发布/采集异常次数（TryScan 捕获级）

    // ──────────── 当前容量状态（gauge） ────────────
    private static long _acquisitionCycleP95Milliseconds;
    private static long _acquisitionCycleP99Milliseconds;
    private static long _productionPendingCount;
    private static long _productionQueuePeakCount;
    private static long _productionOverflowCount;
    private static long _productionRecoveryFileBytes;
    private static long _productionFlushP95Milliseconds;
    private static long _productionFlushP99Milliseconds;
    private static long _dataSourcePendingCount;
    private static long _dataSourceQueuePeakCount;
    private static long _dataSourceOverflowCount;
    private static long _dataSourceRecoveryFileBytes;
    private static long _dataSourceFlushP95Milliseconds;
    private static long _dataSourceFlushP99Milliseconds;
    private static long _productionDatabaseBytes;
    private static long _productionWalBytes;
    private static long _dataSourceDatabaseBytes;
    private static long _dataSourceWalBytes;
    private static long _totalDatabaseBytes;
    private static long _totalWalBytes;
    private static readonly ConcurrentDictionary<string, ReaderMetricSnapshot> ReaderDiagnostics = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, PlcProfileMetricSnapshot> PlcProfileDiagnostics = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>订阅者规模登记：记录当前订阅者数到峰值（峰值单调不减，用于容量观察）。</summary>
    public static void TrackSubscriberCount(ref long peakField, int current)
    {
        long observed = Interlocked.Read(ref peakField);
        while (current > observed)
        {
            long prev = Interlocked.CompareExchange(ref peakField, current, observed);
            if (prev == observed) break; // 成功
            observed = prev;
        }
    }

    /// <summary>更新容量相关 gauge；调用方负责提供同一时刻的诊断快照。</summary>
    public static void UpdateDiagnostics(
        Kanban.Collector.Core.Services.AcquisitionDiagnosticsSnapshot acquisition,
        Kanban.Collector.Core.Services.HistoryDiagnosticsSnapshot history)
    {
        Interlocked.Exchange(ref _acquisitionCycleP95Milliseconds, acquisition.CycleP95Milliseconds);
        Interlocked.Exchange(ref _acquisitionCycleP99Milliseconds, acquisition.CycleP99Milliseconds);
        Interlocked.Exchange(ref _productionPendingCount, history.PendingProductionCount);
        Interlocked.Exchange(ref _productionQueuePeakCount, history.ProductionQueuePeakCount);
        Interlocked.Exchange(ref _productionOverflowCount, history.ProductionOverflowCount);
        Interlocked.Exchange(ref _productionRecoveryFileBytes, history.RecoveryFileBytes);
        Interlocked.Exchange(ref _productionFlushP95Milliseconds, history.ProductionFlushP95Milliseconds);
        Interlocked.Exchange(ref _productionFlushP99Milliseconds, history.ProductionFlushP99Milliseconds);
        Interlocked.Exchange(ref _dataSourcePendingCount, history.PendingDataSourceCount);
        Interlocked.Exchange(ref _dataSourceQueuePeakCount, history.DataSourceQueuePeakCount);
        Interlocked.Exchange(ref _dataSourceOverflowCount, history.DataSourceOverflowCount);
        Interlocked.Exchange(ref _dataSourceRecoveryFileBytes, history.DataSourceRecoveryFileBytes);
        Interlocked.Exchange(ref _dataSourceFlushP95Milliseconds, history.DataSourceFlushP95Milliseconds);
        Interlocked.Exchange(ref _dataSourceFlushP99Milliseconds, history.DataSourceFlushP99Milliseconds);
        Interlocked.Exchange(ref _productionDatabaseBytes, history.ProductionDatabaseBytes);
        Interlocked.Exchange(ref _productionWalBytes, history.ProductionWalBytes);
        Interlocked.Exchange(ref _dataSourceDatabaseBytes, history.DataSourceDatabaseBytes);
        Interlocked.Exchange(ref _dataSourceWalBytes, history.DataSourceWalBytes);
        Interlocked.Exchange(ref _totalDatabaseBytes, history.TotalDatabaseBytes);
        Interlocked.Exchange(ref _totalWalBytes, history.TotalWalBytes);
    }

    public static void UpdateReaderDiagnostics(IReadOnlyList<DataSourceReaderDiagnosticsSnapshot> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        var currentProtocols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in diagnostics.GroupBy(item => item.ProtocolKey, StringComparer.OrdinalIgnoreCase))
        {
            var snapshot = AggregateReaderDiagnostics(group.Key, group.ToArray());
            currentProtocols.Add(snapshot.ProtocolKey);
            ReaderDiagnostics[snapshot.ProtocolKey] = snapshot;
        }

        foreach (var protocolKey in ReaderDiagnostics.Keys)
        {
            if (!currentProtocols.Contains(protocolKey))
                ReaderDiagnostics.TryRemove(protocolKey, out _);
        }
    }

    public static void UpdateRuntimeSessionDiagnostics(
        IReadOnlyList<PlcRuntimeSessionDiagnosticsSnapshot> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        var currentProfiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var diagnostic in diagnostics)
        {
            var profileId = string.IsNullOrWhiteSpace(diagnostic.ProfileId)
                ? ConnectionProfile.DefaultId
                : diagnostic.ProfileId.Trim();
            currentProfiles.Add(profileId);
            PlcProfileDiagnostics[profileId] = new PlcProfileMetricSnapshot
            {
                ProfileId = profileId,
                ProtocolKey = string.IsNullOrWhiteSpace(diagnostic.ProtocolKey)
                    ? DataSourceProtocolKeys.Plc
                    : diagnostic.ProtocolKey.Trim().ToLowerInvariant(),
                Brand = diagnostic.Brand.ToString(),
                IsConnected = diagnostic.IsConnected,
                ConsecutiveFailures = diagnostic.ConsecutiveFailures,
                TotalDisconnectCount = diagnostic.TotalDisconnectCount,
                DisconnectedAt = diagnostic.DisconnectedAt,
                LastDisconnectDuration = diagnostic.LastDisconnectDuration,
                LastSuccessfulAcquisitionAt = diagnostic.LastSuccessfulAcquisitionAt,
                ConsecutiveAcquisitionFailures = diagnostic.ConsecutiveAcquisitionFailures,
                AcquisitionFailureCount = diagnostic.AcquisitionFailureCount,
            };
        }

        foreach (var profileId in PlcProfileDiagnostics.Keys)
        {
            if (!currentProfiles.Contains(profileId))
                PlcProfileDiagnostics.TryRemove(profileId, out _);
        }
    }

    /// <summary>输出 Prometheus 风格文本（text/plain；版本来自程序集，供升级追溯）。</summary>
    public static string Render()
    {
        var version = System.Reflection.Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";
        var sb = new StringBuilder(512);
        sb.AppendLine($"# kanban_collector_version {version}");
        sb.AppendLine($"kanban_snapshot_publish_total {Interlocked.Read(ref SnapshotPublishCount)}");
        sb.AppendLine($"kanban_meta_publish_total {Interlocked.Read(ref MetaPublishCount)}");
        sb.AppendLine($"kanban_alarm_event_publish_total {Interlocked.Read(ref AlarmEventPublishCount)}");
        sb.AppendLine($"kanban_status_event_publish_total {Interlocked.Read(ref StatusEventPublishCount)}");
        sb.AppendLine($"kanban_acquisition_cycle_total {Interlocked.Read(ref AcquisitionCycleCount)}");
        sb.AppendLine($"kanban_snapshot_subscriber_peak {Interlocked.Read(ref SnapshotSubscriberPeak)}");
        sb.AppendLine($"kanban_event_subscriber_peak {Interlocked.Read(ref EventSubscriberPeak)}");
        sb.AppendLine($"kanban_meta_subscriber_peak {Interlocked.Read(ref MetaSubscriberPeak)}");
        sb.AppendLine($"kanban_publish_error_total {Interlocked.Read(ref PublishErrorCount)}");
        sb.AppendLine($"kanban_acquisition_cycle_p95_milliseconds {Interlocked.Read(ref _acquisitionCycleP95Milliseconds)}");
        sb.AppendLine($"kanban_acquisition_cycle_p99_milliseconds {Interlocked.Read(ref _acquisitionCycleP99Milliseconds)}");
        sb.AppendLine($"kanban_production_pending_count {Interlocked.Read(ref _productionPendingCount)}");
        sb.AppendLine($"kanban_production_queue_peak_count {Interlocked.Read(ref _productionQueuePeakCount)}");
        sb.AppendLine($"kanban_production_overflow_total {Interlocked.Read(ref _productionOverflowCount)}");
        sb.AppendLine($"kanban_production_recovery_file_bytes {Interlocked.Read(ref _productionRecoveryFileBytes)}");
        sb.AppendLine($"kanban_production_flush_p95_milliseconds {Interlocked.Read(ref _productionFlushP95Milliseconds)}");
        sb.AppendLine($"kanban_production_flush_p99_milliseconds {Interlocked.Read(ref _productionFlushP99Milliseconds)}");
        sb.AppendLine($"kanban_datasource_pending_count {Interlocked.Read(ref _dataSourcePendingCount)}");
        sb.AppendLine($"kanban_datasource_queue_peak_count {Interlocked.Read(ref _dataSourceQueuePeakCount)}");
        sb.AppendLine($"kanban_datasource_overflow_total {Interlocked.Read(ref _dataSourceOverflowCount)}");
        sb.AppendLine($"kanban_datasource_recovery_file_bytes {Interlocked.Read(ref _dataSourceRecoveryFileBytes)}");
        sb.AppendLine($"kanban_datasource_flush_p95_milliseconds {Interlocked.Read(ref _dataSourceFlushP95Milliseconds)}");
        sb.AppendLine($"kanban_datasource_flush_p99_milliseconds {Interlocked.Read(ref _dataSourceFlushP99Milliseconds)}");
        sb.AppendLine($"kanban_production_database_bytes {Interlocked.Read(ref _productionDatabaseBytes)}");
        sb.AppendLine($"kanban_production_wal_bytes {Interlocked.Read(ref _productionWalBytes)}");
        sb.AppendLine($"kanban_datasource_database_bytes {Interlocked.Read(ref _dataSourceDatabaseBytes)}");
        sb.AppendLine($"kanban_datasource_wal_bytes {Interlocked.Read(ref _dataSourceWalBytes)}");
        sb.AppendLine($"kanban_history_database_bytes {Interlocked.Read(ref _totalDatabaseBytes)}");
        sb.AppendLine($"kanban_history_wal_bytes {Interlocked.Read(ref _totalWalBytes)}");
        sb.AppendLine("# TYPE kanban_plc_profile_connected gauge");
        sb.AppendLine("# TYPE kanban_plc_profile_consecutive_failures gauge");
        sb.AppendLine("# TYPE kanban_plc_profile_disconnect_total counter");
        sb.AppendLine("# TYPE kanban_plc_profile_disconnected_seconds gauge");
        sb.AppendLine("# TYPE kanban_plc_profile_last_disconnect_seconds gauge");
        sb.AppendLine("# TYPE kanban_plc_profile_acquisition_consecutive_failures gauge");
        sb.AppendLine("# TYPE kanban_plc_profile_acquisition_failure_total counter");
        sb.AppendLine("# TYPE kanban_plc_profile_last_successful_acquisition_timestamp_seconds gauge");
        foreach (var profile in PlcProfileDiagnostics.Values.OrderBy(item => item.ProfileId, StringComparer.OrdinalIgnoreCase))
            AppendPlcProfileMetrics(sb, profile);
        sb.AppendLine("# TYPE kanban_reader_resolve_total counter");
        sb.AppendLine("# TYPE kanban_reader_validation_total counter");
        sb.AppendLine("# TYPE kanban_reader_read_total counter");
        sb.AppendLine("# TYPE kanban_reader_ack_total counter");
        sb.AppendLine("# TYPE kanban_reader_read_duration_milliseconds histogram");
        sb.AppendLine("# TYPE kanban_reader_ack_duration_milliseconds histogram");
        sb.AppendLine("# TYPE kanban_reader_read_p95_milliseconds gauge");
        sb.AppendLine("# TYPE kanban_reader_read_p99_milliseconds gauge");
        sb.AppendLine("# TYPE kanban_reader_ack_p95_milliseconds gauge");
        sb.AppendLine("# TYPE kanban_reader_ack_p99_milliseconds gauge");
        foreach (var reader in ReaderDiagnostics.Values.OrderBy(item => item.ProtocolKey, StringComparer.OrdinalIgnoreCase))
            AppendReaderMetrics(sb, reader);
        return sb.ToString();
    }

    private static void AppendPlcProfileMetrics(StringBuilder sb, PlcProfileMetricSnapshot profile)
    {
        var profileId = EscapeLabelValue(profile.ProfileId);
        var protocol = EscapeLabelValue(profile.ProtocolKey);
        var brand = EscapeLabelValue(profile.Brand);
        var labels = $"profile=\"{profileId}\",protocol=\"{protocol}\",brand=\"{brand}\"";
        sb.AppendLine($"kanban_plc_profile_connected{{{labels}}} {(profile.IsConnected ? 1 : 0)}");
        sb.AppendLine($"kanban_plc_profile_consecutive_failures{{{labels}}} {profile.ConsecutiveFailures}");
        sb.AppendLine($"kanban_plc_profile_disconnect_total{{{labels}}} {profile.TotalDisconnectCount}");
        var disconnectedSeconds = profile.DisconnectedAt.HasValue
            ? Math.Max(0, (long)(DateTime.Now - profile.DisconnectedAt.Value).TotalSeconds)
            : 0;
        sb.AppendLine($"kanban_plc_profile_disconnected_seconds{{{labels}}} {disconnectedSeconds}");
        sb.AppendLine($"kanban_plc_profile_last_disconnect_seconds{{{labels}}} {Math.Max(0, (long)(profile.LastDisconnectDuration?.TotalSeconds ?? 0))}");
        sb.AppendLine($"kanban_plc_profile_acquisition_consecutive_failures{{{labels}}} {profile.ConsecutiveAcquisitionFailures}");
        sb.AppendLine($"kanban_plc_profile_acquisition_failure_total{{{labels}}} {profile.AcquisitionFailureCount}");
        sb.AppendLine($"kanban_plc_profile_last_successful_acquisition_timestamp_seconds{{{labels}}} {ToUnixSeconds(profile.LastSuccessfulAcquisitionAt)}");
    }

    private static long ToUnixSeconds(DateTime? value)
        => value.HasValue ? new DateTimeOffset(value.Value).ToUnixTimeSeconds() : 0;

    private static ReaderMetricSnapshot AggregateReaderDiagnostics(
        string protocolKey,
        IReadOnlyList<DataSourceReaderDiagnosticsSnapshot> readers)
    {
        return new ReaderMetricSnapshot
        {
            ProtocolKey = protocolKey,
            ResolveCount = readers.Sum(reader => reader.ResolveCount),
            ValidationCount = readers.Sum(reader => reader.ValidationCount),
            ValidationSuccessCount = readers.Sum(reader => reader.ValidationSuccessCount),
            ValidationFailureCount = readers.Sum(reader => reader.ValidationFailureCount),
            ReadCount = readers.Sum(reader => reader.ReadCount),
            ReadSuccessCount = readers.Sum(reader => reader.ReadSuccessCount),
            ReadFailureCount = readers.Sum(reader => reader.ReadFailureCount),
            ReadDurationTotalMilliseconds = readers.Sum(reader => reader.ReadDurationTotalMilliseconds),
            ReadDurationBucketCounts = SumBuckets(readers.Select(reader => reader.ReadDurationBucketCounts)),
            ReadP95Milliseconds = readers.Count == 0 ? 0 : readers.Max(reader => reader.ReadP95Milliseconds),
            ReadP99Milliseconds = readers.Count == 0 ? 0 : readers.Max(reader => reader.ReadP99Milliseconds),
            AcknowledgementCount = readers.Sum(reader => reader.AcknowledgementCount),
            AcknowledgementSuccessCount = readers.Sum(reader => reader.AcknowledgementSuccessCount),
            AcknowledgementFailureCount = readers.Sum(reader => reader.AcknowledgementFailureCount),
            AcknowledgementDurationTotalMilliseconds = readers.Sum(reader => reader.AcknowledgementDurationTotalMilliseconds),
            AcknowledgementDurationBucketCounts = SumBuckets(readers.Select(reader => reader.AcknowledgementDurationBucketCounts)),
            AcknowledgementP95Milliseconds = readers.Count == 0 ? 0 : readers.Max(reader => reader.AcknowledgementP95Milliseconds),
            AcknowledgementP99Milliseconds = readers.Count == 0 ? 0 : readers.Max(reader => reader.AcknowledgementP99Milliseconds),
        };
    }

    private static IReadOnlyList<long> SumBuckets(IEnumerable<IReadOnlyList<long>> buckets)
    {
        var result = new long[DataSourceReaderMetricBuckets.DurationUpperBoundsMilliseconds.Count];
        foreach (var bucket in buckets)
        {
            for (var index = 0; index < result.Length && index < bucket.Count; index++)
                result[index] += bucket[index];
        }
        return result;
    }

    private static void AppendReaderMetrics(StringBuilder sb, ReaderMetricSnapshot reader)
    {
        var protocol = EscapeLabelValue(reader.ProtocolKey);
        sb.AppendLine($"kanban_reader_resolve_total{{protocol=\"{protocol}\"}} {reader.ResolveCount}");
        sb.AppendLine($"kanban_reader_validation_total{{protocol=\"{protocol}\",result=\"success\"}} {reader.ValidationSuccessCount}");
        sb.AppendLine($"kanban_reader_validation_total{{protocol=\"{protocol}\",result=\"failure\"}} {reader.ValidationFailureCount}");
        sb.AppendLine($"kanban_reader_read_total{{protocol=\"{protocol}\",result=\"success\"}} {reader.ReadSuccessCount}");
        sb.AppendLine($"kanban_reader_read_total{{protocol=\"{protocol}\",result=\"failure\"}} {reader.ReadFailureCount}");
        sb.AppendLine($"kanban_reader_ack_total{{protocol=\"{protocol}\",result=\"success\"}} {reader.AcknowledgementSuccessCount}");
        sb.AppendLine($"kanban_reader_ack_total{{protocol=\"{protocol}\",result=\"failure\"}} {reader.AcknowledgementFailureCount}");
        sb.AppendLine($"kanban_reader_read_p95_milliseconds{{protocol=\"{protocol}\"}} {reader.ReadP95Milliseconds}");
        sb.AppendLine($"kanban_reader_read_p99_milliseconds{{protocol=\"{protocol}\"}} {reader.ReadP99Milliseconds}");
        sb.AppendLine($"kanban_reader_ack_p95_milliseconds{{protocol=\"{protocol}\"}} {reader.AcknowledgementP95Milliseconds}");
        sb.AppendLine($"kanban_reader_ack_p99_milliseconds{{protocol=\"{protocol}\"}} {reader.AcknowledgementP99Milliseconds}");
        AppendHistogram(sb, "kanban_reader_read_duration_milliseconds", protocol, reader.ReadDurationBucketCounts, reader.ReadCount, reader.ReadDurationTotalMilliseconds);
        AppendHistogram(sb, "kanban_reader_ack_duration_milliseconds", protocol, reader.AcknowledgementDurationBucketCounts, reader.AcknowledgementCount, reader.AcknowledgementDurationTotalMilliseconds);
    }

    private static void AppendHistogram(
        StringBuilder sb,
        string metricName,
        string protocol,
        IReadOnlyList<long> bucketCounts,
        long count,
        long sum)
    {
        long cumulative = 0;
        for (var index = 0; index < DataSourceReaderMetricBuckets.DurationUpperBoundsMilliseconds.Count; index++)
        {
            if (index < bucketCounts.Count)
                cumulative += bucketCounts[index];
            var upperBound = DataSourceReaderMetricBuckets.DurationUpperBoundsMilliseconds[index];
            sb.AppendLine($"{metricName}_bucket{{protocol=\"{protocol}\",le=\"{upperBound}\"}} {cumulative}");
        }
        sb.AppendLine($"{metricName}_bucket{{protocol=\"{protocol}\",le=\"+Inf\"}} {count}");
        sb.AppendLine($"{metricName}_sum{{protocol=\"{protocol}\"}} {sum}");
        sb.AppendLine($"{metricName}_count{{protocol=\"{protocol}\"}} {count}");
    }

    private static string EscapeLabelValue(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    private sealed record ReaderMetricSnapshot
    {
        public string ProtocolKey { get; init; } = string.Empty;
        public long ResolveCount { get; init; }
        public long ValidationCount { get; init; }
        public long ValidationSuccessCount { get; init; }
        public long ValidationFailureCount { get; init; }
        public long ReadCount { get; init; }
        public long ReadSuccessCount { get; init; }
        public long ReadFailureCount { get; init; }
        public long ReadDurationTotalMilliseconds { get; init; }
        public IReadOnlyList<long> ReadDurationBucketCounts { get; init; } = [];
        public long ReadP95Milliseconds { get; init; }
        public long ReadP99Milliseconds { get; init; }
        public long AcknowledgementCount { get; init; }
        public long AcknowledgementSuccessCount { get; init; }
        public long AcknowledgementFailureCount { get; init; }
        public long AcknowledgementDurationTotalMilliseconds { get; init; }
        public IReadOnlyList<long> AcknowledgementDurationBucketCounts { get; init; } = [];
        public long AcknowledgementP95Milliseconds { get; init; }
        public long AcknowledgementP99Milliseconds { get; init; }
    }

    private sealed record PlcProfileMetricSnapshot
    {
        public string ProfileId { get; init; } = string.Empty;
        public string ProtocolKey { get; init; } = DataSourceProtocolKeys.Plc;
        public string Brand { get; init; } = string.Empty;
        public bool IsConnected { get; init; }
        public int ConsecutiveFailures { get; init; }
        public int TotalDisconnectCount { get; init; }
        public DateTime? DisconnectedAt { get; init; }
        public TimeSpan? LastDisconnectDuration { get; init; }
        public DateTime? LastSuccessfulAcquisitionAt { get; init; }
        public int ConsecutiveAcquisitionFailures { get; init; }
        public int AcquisitionFailureCount { get; init; }
    }
}
