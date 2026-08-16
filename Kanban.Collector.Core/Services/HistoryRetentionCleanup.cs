using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Linq.Expressions;

namespace Kanban.Collector.Core.Services;

internal static class HistoryRetentionCleanup
{
    /// <summary>单批删除上限：避免超长事务/锁占用与内存放大。</summary>
    private const int DefaultBatchSize = 5000;

    /// <summary>
    /// 分批删除早于 cutoff 的历史记录（SQL 层 ExecuteDelete，按批 <see cref="DefaultBatchSize"/> 循环，
    /// 返回删除总数）。相比旧实现 RemoveRange（全量加载内存再逐条 DELETE）：
    /// - 不把整表过期记录载入内存（百万级记录时内存峰值差异巨大）；
    /// - 单批事务短，WAL 模式下对其他读写的事务干扰小；
    /// - 中途失败时已完成批次不丢（下次启动继续清理剩余）。
    /// </summary>
    public static int DeleteBefore<T>(
        Func<DbContext> createContext,
        Func<DbContext, DbSet<T>> selectSet,
        Expression<Func<T, DateTime>> timeSelector,
        string label,
        int retentionDays,
        ILogger logger,
        int batchSize = DefaultBatchSize) where T : class
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-retentionDays);
            var totalDeleted = 0;
            while (true)
            {
                using var context = createContext();
                // ExecuteDelete 不支持 OrderBy（关系数据库）；分批只按条件 + Take
                var candidates = selectSet(context)
                    .Where(BuildCutoffPredicate(timeSelector, cutoff))
                    .Take(batchSize);
                var deleted = candidates.ExecuteDelete();
                if (deleted <= 0) break;
                totalDeleted += deleted;
                if (deleted < batchSize) break;
            }
            if (totalDeleted > 0)
                logger.LogInformation("已清理 {Count} 条过期{Label}（早于 {Cutoff}，分批 {BatchSize}）",
                    totalDeleted, label, cutoff, batchSize);
            return totalDeleted;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "清理过期{Label}失败", label);
            return 0;
        }
    }

    private static Expression<Func<T, bool>> BuildCutoffPredicate<T>(
        Expression<Func<T, DateTime>> timeSelector,
        DateTime cutoff) where T : class
    {
        var parameter = timeSelector.Parameters[0];
        var body = Expression.LessThan(
            timeSelector.Body,
            Expression.Constant(cutoff, typeof(DateTime)));
        return Expression.Lambda<Func<T, bool>>(body, parameter);
    }
}
