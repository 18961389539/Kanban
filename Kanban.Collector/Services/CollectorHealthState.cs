using Kanban.Core.Services;

namespace Kanban.Collector.Services;

/// <summary>
/// Collector 健康状态（进程级单例）：由 <see cref="CollectorWorker"/> 在初始化各阶段更新，
/// 供 /health/live 与 /health/ready 探针读取。核心目标：**消除"进程存活但采集已停止"的假健康**——
/// 初始化失败时进程直接退出（Worker 重新抛出），退出前记录失败原因；
/// readiness 探针据此在采集循环停止/连续失败/数据库不可写时返回不健康。
/// </summary>
public sealed class CollectorHealthState
{
    private readonly object _gate = new();

    /// <summary>初始化阶段状态。</summary>
    public enum InitState
    {
        Pending,
        Ready,
        Failed,
    }

    private InitState _state = InitState.Pending;
    private string? _initErrorMessage;
    private DateTime? _readyAtUtc;
    private DateTime? _failedAtUtc;
    private AcquisitionDiagnosticsSnapshot? _lastAcquisition;

    public InitState State
    {
        get { lock (_gate) return _state; }
    }

    public string? InitErrorMessage
    {
        get { lock (_gate) return _initErrorMessage; }
    }

    public DateTime? ReadyAtUtc
    {
        get { lock (_gate) return _readyAtUtc; }
    }

    public DateTime? FailedAtUtc
    {
        get { lock (_gate) return _failedAtUtc; }
    }

    public AcquisitionDiagnosticsSnapshot? LastAcquisition
    {
        get { lock (_gate) return _lastAcquisition; }
    }

    /// <summary>标记初始化完成（采集已启动）。</summary>
    public void MarkReady()
    {
        lock (_gate)
        {
            _state = InitState.Ready;
            _readyAtUtc = DateTime.UtcNow;
        }
    }

    /// <summary>标记初始化失败（进程随后退出；若被宿主吞掉，readiness 仍判不健康）。</summary>
    public void MarkFailed(string message)
    {
        lock (_gate)
        {
            _state = InitState.Failed;
            _initErrorMessage = message;
            _failedAtUtc = DateTime.UtcNow;
        }
    }

    /// <summary>更新最近一次采集诊断快照（采集循环每轮写入，供 readiness 判活）。</summary>
    public void UpdateAcquisition(AcquisitionDiagnosticsSnapshot snapshot)
    {
        lock (_gate) _lastAcquisition = snapshot;
    }
}
