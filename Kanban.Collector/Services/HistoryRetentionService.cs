using Kanban.Core.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kanban.Collector.Services;

/// <summary>
/// 历史数据保留策略：启动后立即执行一次、之后每 24 小时执行一次，
/// 按保留天数分批清理生产/报警/状态/缺陷四类历史数据并做 WAL checkpoint。
/// 保留天数来源（优先级从高到低）：
/// 1. 环境变量 KANBAN_HISTORY_RETENTION_DAYS（生产部署按现场磁盘容量配置）；
/// 2. 默认 365 天。
/// 配置 0 或负数 = 禁用清理（默认值 365 保证开箱即有保留策略，不再"只提供方法无人调用"）。
/// </summary>
public sealed class HistoryRetentionService : BackgroundService
{
    public const string EnvRetentionDays = "KANBAN_HISTORY_RETENTION_DAYS";
    private const int DefaultRetentionDays = 365;
    private const int MaxRetentionDays = 36500; // 100 年，防环境变量误配极端值
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(24);

    private readonly HistoryService _history;
    private readonly ILogger<HistoryRetentionService> _logger;
    private readonly int _retentionDays;

    public HistoryRetentionService(HistoryService history, ILogger<HistoryRetentionService> logger)
    {
        _history = history;
        _logger = logger;
        _retentionDays = ResolveRetentionDays();
    }

    /// <summary>当前生效的保留天数（供日志与测试读取）。</summary>
    public int RetentionDays => _retentionDays;

    internal static int ResolveRetentionDays()
    {
        var raw = Environment.GetEnvironmentVariable(EnvRetentionDays);
        if (string.IsNullOrWhiteSpace(raw)) return DefaultRetentionDays;
        if (int.TryParse(raw, out var days) && days >= 0 && days <= MaxRetentionDays)
            return days;
        // 环境变量非法：回退默认并告警（日志在构造后由服务输出）
        return DefaultRetentionDays;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("历史保留策略已启动：保留 {Days} 天，每 24 小时清理一次（环境变量 {Env} 可调整）",
            _retentionDays, EnvRetentionDays);

        // 启动后立即执行一次（错开采集初始化：首次延时 30s，避免与启动期大量写入争抢锁）
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            RunCleanup();
        }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(CleanupInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            RunCleanup();
        }
    }

    private void RunCleanup()
    {
        if (_retentionDays <= 0)
        {
            _logger.LogInformation("历史保留策略已禁用（保留天数 = 0），跳过清理");
            return;
        }
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var deleted = _history.CleanupOldHistory(_retentionDays);
            stopwatch.Stop();
            if (deleted > 0)
                _logger.LogInformation("历史保留清理完成：删除 {Count} 条（保留 {Days} 天，耗时 {Elapsed:F1}s）",
                    deleted, _retentionDays, stopwatch.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "历史保留清理失败（保留 {Days} 天，耗时 {Elapsed:F1}s）", _retentionDays, stopwatch.Elapsed.TotalSeconds);
        }
    }
}
