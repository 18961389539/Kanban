using Microsoft.Extensions.Logging;

namespace Kanban.Collector.Core.Services;

/// <summary>
/// 后台常驻任务（各 Store 的 flush 循环、发布循环等）的统一启动入口。
/// </summary>
/// <remarks>
/// 审查修复 2026-09-05（P2）：此前各 Store 一律 <c>Task.Run(() =&gt; FlushLoopAsync(...))</c>
/// 火后不管，既无故障观察也无告警。循环体对单次写入的 try/catch 只在循环内部兜底，
/// 一旦有异常从循环体之外逃逸（序列化失败、构造期失败、CancellationTokenSource 已释放等），
/// 后台任务静默故障、写入永久停滞且无人知晓，只能靠"数据不涨了"被动发现。
/// 与 <see cref="PlcDataAcquisitionService"/> 的轮询任务（ContinueWith + OnlyOnFaulted）保持一致。
/// </remarks>
internal static class BackgroundTaskRunner
{
    /// <summary>
    /// 启动后台循环并挂接故障观察。
    /// </summary>
    /// <param name="loop">循环体；取消语义由 <paramref name="ct"/> 自行在内部承担。</param>
    /// <param name="ct">传给循环体的取消令牌。</param>
    /// <param name="logger">故障日志输出。</param>
    /// <param name="name">任务名，用于日志定位。</param>
    public static Task StartLoop(Func<CancellationToken, Task> loop, CancellationToken ct, ILogger logger, string name)
    {
        // 刻意不把 ct 作为 Task.Run 的第二参数：那只是"启动前取消则不调度"的语义，
        // 一旦命中，委托根本不执行而调用方已认为后台循环在运行（静默假启动）。
        var task = Task.Run(() => loop(ct), CancellationToken.None);
        Observe(task, logger, name);
        return task;
    }

    /// <summary>为已启动的后台任务挂接故障观察（不阻塞、不影响原任务生命周期）。</summary>
    public static void Observe(Task task, ILogger logger, string name)
    {
        _ = task.ContinueWith(t =>
            {
                if (t.IsFaulted)
                    logger.LogError(t.Exception, "{Name} 后台任务未观察异常，该后台循环已终止", name);
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }
}
