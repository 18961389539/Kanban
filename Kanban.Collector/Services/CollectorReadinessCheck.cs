using Kanban.Core.Data;
using Kanban.Core.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Kanban.Collector.Services;

/// <summary>
/// Collector readiness 探针（/health/ready）：
/// - 初始化未完成/失败 → Unhealthy；
/// - 采集循环已停止（进程存活但采集挂掉）→ Unhealthy；
/// - 连续采集失败或长时间无成功采集（且已配置设备）→ Unhealthy；
/// - 生产历史库不可写（磁盘满/库损坏）→ Unhealthy；
/// - 恢复文件超过上限（回放被挂起，数据积压）→ Degraded。
/// /health/live 只反映进程存活（Kestrel 能响应即 Healthy），不反映业务状态。
/// </summary>
public sealed class CollectorReadinessCheck : IHealthCheck
{
    /// <summary>最近成功采集判活窗口：PLC 断线重连（1~30s 退避）期间不应误判，取 3 分钟。</summary>
    private static readonly TimeSpan StaleAcquisitionWindow = TimeSpan.FromMinutes(3);

    /// <summary>恢复文件回放上限（与 ProductionHistoryWriter.MaxRecoveryFileBytes 对齐的对外口径）。</summary>
    private const long MaxRecoveryFileBytes = 200 * 1024 * 1024;

    private readonly CollectorHealthState _healthState;
    private readonly IPlcDataAcquisitionService _acquisition;
    private readonly DatabaseProvider _db;
    private readonly Func<int> _configuredDeviceCount;
    private readonly Func<HistoryDiagnosticsSnapshot> _historyDiagnostics;
    private readonly ILogger<CollectorReadinessCheck> _logger;

    public CollectorReadinessCheck(
        CollectorHealthState healthState,
        IPlcDataAcquisitionService acquisition,
        DatabaseProvider db,
        ILogger<CollectorReadinessCheck> logger,
        Func<int>? configuredDeviceCount = null,
        Func<HistoryDiagnosticsSnapshot>? historyDiagnostics = null)
    {
        _healthState = healthState;
        _acquisition = acquisition;
        _db = db;
        _logger = logger;
        _configuredDeviceCount = configuredDeviceCount ?? (() => 0);
        _historyDiagnostics = historyDiagnostics ?? (() => new HistoryDiagnosticsSnapshot());
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            // 1. 初始化状态
            if (_healthState.State == CollectorHealthState.InitState.Failed)
                return Task.FromResult(HealthCheckResult.Unhealthy($"采集初始化失败：{_healthState.InitErrorMessage}"));
            if (_healthState.State == CollectorHealthState.InitState.Pending)
                return Task.FromResult(HealthCheckResult.Unhealthy("采集初始化尚未完成"));

            // 2. 采集循环是否在跑（Worker 循环内部每轮更新诊断；StopAsync 后 IsRunning=false）
            if (!_acquisition.IsRunning)
                return Task.FromResult(HealthCheckResult.Unhealthy("采集循环已停止（进程存活但采集未运行）"));

            // 3. 采集活性：有已配置设备时，最近成功采集不能超过窗口；连续失败过多也判不健康
            var configured = _configuredDeviceCount();
            var diag = _acquisition.GetDiagnosticsSnapshot();
            _healthState.UpdateAcquisition(diag);
            if (configured > 0)
            {
                if (diag.LastSuccessfulAt.HasValue && DateTime.Now - diag.LastSuccessfulAt.Value > StaleAcquisitionWindow)
                    return Task.FromResult(HealthCheckResult.Unhealthy(
                        $"最近成功采集时间超过 {StaleAcquisitionWindow.TotalMinutes:0} 分钟（LastSuccessfulAt={diag.LastSuccessfulAt:o}）"));
                if (!diag.LastSuccessfulAt.HasValue && diag.CompletedCycles > 0)
                    return Task.FromResult(HealthCheckResult.Unhealthy("采集从未成功（已配置设备但无成功采集记录）"));
                if (diag.ConsecutiveFailureCycles >= 10)
                    return Task.FromResult(HealthCheckResult.Degraded(
                        $"连续 {diag.ConsecutiveFailureCycles} 轮采集失败（PLC 断线或全部设备读取失败）"));
            }

            // 4. 历史库可写性（生产库 quick_check + BEGIN IMMEDIATE 写锁探测）
            var writeProbe = _db.ProbeWriteAccess();
            if (writeProbe is not null)
                return Task.FromResult(HealthCheckResult.Unhealthy($"历史库不可写：{writeProbe}"));

            // 5. 恢复文件积压（超过上限 → 回放挂起，历史写入可能继续转存积压）
            try
            {
                var hist = _historyDiagnostics();
                if (hist.RecoveryFileBytes > MaxRecoveryFileBytes)
                    return Task.FromResult(HealthCheckResult.Degraded(
                        $"恢复文件 {hist.RecoveryFileBytes / (1024 * 1024)}MB 超过回放上限，历史数据积压中"));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "读取历史诊断失败（跳过恢复文件积压检查）");
            }

            return Task.FromResult(HealthCheckResult.Healthy());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "readiness 探针执行异常");
            return Task.FromResult(HealthCheckResult.Unhealthy($"readiness 探针异常：{ex.Message}"));
        }
    }
}
