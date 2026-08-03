using System.Reflection;
using System.Text;

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
        return sb.ToString();
    }
}
