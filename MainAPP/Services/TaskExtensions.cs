using Microsoft.Extensions.Logging;

namespace MainAPP.Services;

/// <summary>
/// Task 扩展方法：统一处理 fire-and-forget 场景的未观察异常。
/// 参照 PlcDataAcquisitionService 中已采用的 ContinueWith(OnlyOnFaulted) 模式。
/// </summary>
public static class TaskExtensions
{
    /// <summary>
    /// 观察 fire-and-forget Task 的异常：若 Task 失败，记录日志避免未观察异常。
    /// 用于 `_ = Task.Run(...)` 场景。
    /// </summary>
    public static void Forget(this Task task, ILogger? logger = null)
    {
        if (task.IsCompleted)
        {
            if (task.IsFaulted)
                logger?.LogError(task.Exception, "Fire-and-forget Task 发生未观察异常");
            return;
        }

        task.ContinueWith(t =>
        {
            if (t.IsFaulted)
                logger?.LogError(t.Exception, "Fire-and-forget Task 发生未观察异常");
        }, TaskContinuationOptions.OnlyOnFaulted);
    }
}
