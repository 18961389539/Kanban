using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Kanban.Collector.Services;

/// <summary>
/// Collector readiness 探针（/health/ready）：
/// - 初始化未完成/失败 → Unhealthy；
/// - 采集循环已停止（进程存活但采集挂掉）→ Unhealthy；
/// - 连续采集失败或长时间无成功采集（且已配置设备）→ Unhealthy；
/// - 生产历史库不可写（磁盘满/库损坏）→ Unhealthy；
/// - 恢复文件超过上限（回放被挂起，数据积压）→ Degraded；
/// - 审计丢弃（队列满 DropWrite 或落库失败）→ Degraded。
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
    private readonly IAuditService? _auditService;
    private readonly Func<int> _configuredDeviceCount;
    private readonly Func<IReadOnlyCollection<string>> _configuredProfileIds;
    private readonly Func<HistoryDiagnosticsSnapshot> _historyDiagnostics;
    private readonly IPlcRuntimeSessionManager? _runtimeSessions;
    private readonly ILogger<CollectorReadinessCheck> _logger;

    public CollectorReadinessCheck(
        CollectorHealthState healthState,
        IPlcDataAcquisitionService acquisition,
        DatabaseProvider db,
        ILogger<CollectorReadinessCheck> logger,
        IAuditService? auditService = null,
        Func<int>? configuredDeviceCount = null,
        Func<HistoryDiagnosticsSnapshot>? historyDiagnostics = null,
        IPlcRuntimeSessionManager? runtimeSessions = null,
        Func<IReadOnlyCollection<string>>? configuredProfileIds = null)
    {
        _healthState = healthState;
        _acquisition = acquisition;
        _db = db;
        _auditService = auditService;
        _logger = logger;
        _configuredDeviceCount = configuredDeviceCount ?? (() => 0);
        _configuredProfileIds = configuredProfileIds ?? (() => Array.Empty<string>());
        _historyDiagnostics = historyDiagnostics ?? (() => new HistoryDiagnosticsSnapshot());
        _runtimeSessions = runtimeSessions;
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

                var profileResult = CheckProfileReadiness(diag);
                if (profileResult is { } profileHealth)
                    return Task.FromResult(profileHealth);

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
                if (hist.RecoveryFileBytes > MaxRecoveryFileBytes
                    || hist.DataSourceRecoveryFileBytes > MaxRecoveryFileBytes)
                {
                    var details = new List<string>();
                    if (hist.RecoveryFileBytes > MaxRecoveryFileBytes)
                        details.Add($"生产历史 {hist.RecoveryFileBytes / (1024 * 1024)}MB");
                    if (hist.DataSourceRecoveryFileBytes > MaxRecoveryFileBytes)
                        details.Add($"数据源快照 {hist.DataSourceRecoveryFileBytes / (1024 * 1024)}MB");
                    return Task.FromResult(HealthCheckResult.Degraded(
                        $"恢复文件 {string.Join("、", details)} 超过回放上限，历史数据积压中"));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "读取历史诊断失败（跳过恢复文件积压检查）");
            }

            // 6. 审计丢弃告警（队列满 DropWrite 或最终落库失败 → 审计数据丢失，审计链完整性受损）
            var auditDropped = _auditService?.DroppedCount ?? 0;
            if (auditDropped > 0)
                return Task.FromResult(HealthCheckResult.Degraded(
                    $"审计已丢弃 {auditDropped} 条记录（队列满或落库失败，审计链完整性受损）"));

            return Task.FromResult(HealthCheckResult.Healthy());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "readiness 探针执行异常");
            return Task.FromResult(HealthCheckResult.Unhealthy($"readiness 探针异常：{ex.Message}"));
        }
    }

    private HealthCheckResult? CheckProfileReadiness(AcquisitionDiagnosticsSnapshot diagnostics)
    {
        if (_runtimeSessions is null || diagnostics.CompletedCycles == 0)
            return null;

        IReadOnlyCollection<string> configuredProfiles;
        try
        {
            configuredProfiles = _configuredProfileIds()
                .Where(profileId => !string.IsNullOrWhiteSpace(profileId))
                .Select(profileId => profileId.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取采集 profile 配置失败（跳过 profile readiness 检查）");
            return null;
        }

        if (configuredProfiles.Count == 0)
            return null;

        IReadOnlyList<PlcRuntimeSessionDiagnosticsSnapshot> snapshots;
        try
        {
            snapshots = _runtimeSessions.GetDiagnosticsSnapshot();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取 PLC profile 运行状态失败");
            return HealthCheckResult.Unhealthy("无法读取 PLC profile 运行状态");
        }

        var byProfile = snapshots.ToDictionary(snapshot => snapshot.ProfileId, StringComparer.OrdinalIgnoreCase);
        var unhealthy = new List<string>();
        foreach (var profileId in configuredProfiles)
        {
            if (!byProfile.TryGetValue(profileId, out var snapshot))
            {
                unhealthy.Add($"{profileId}: 未注册");
                continue;
            }

            if (!snapshot.LastSuccessfulAcquisitionAt.HasValue)
            {
                unhealthy.Add($"{profileId}: 尚无成功采集");
                continue;
            }

            if (DateTime.Now - snapshot.LastSuccessfulAcquisitionAt.Value > StaleAcquisitionWindow)
            {
                unhealthy.Add($"{profileId}: 成功采集已过期");
                continue;
            }

            if (snapshot.ConsecutiveAcquisitionFailures >= 10)
                unhealthy.Add($"{profileId}: 连续 {snapshot.ConsecutiveAcquisitionFailures} 轮失败");
        }

        if (unhealthy.Count == 0)
            return null;

        var description = string.Join("；", unhealthy);
        return unhealthy.Count == configuredProfiles.Count
            ? HealthCheckResult.Unhealthy($"所有 {configuredProfiles.Count} 个 PLC profile 均不健康：{description}")
            : HealthCheckResult.Degraded($"部分 PLC profile 不健康（{unhealthy.Count}/{configuredProfiles.Count}）：{description}");
    }
}
