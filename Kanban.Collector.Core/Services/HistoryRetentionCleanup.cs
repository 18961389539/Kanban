using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Linq.Expressions;

namespace Kanban.Core.Services;

internal static class HistoryRetentionCleanup
{
    public static int DeleteBefore<T>(
        Func<DbContext> createContext,
        Func<DbContext, DbSet<T>> selectSet,
        Expression<Func<T, DateTime>> timeSelector,
        string label,
        int retentionDays,
        ILogger logger) where T : class
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-retentionDays);
            using var context = createContext();
            var oldRecords = selectSet(context).Where(BuildCutoffPredicate(timeSelector, cutoff));
            var count = oldRecords.Count();
            if (count > 0)
            {
                selectSet(context).RemoveRange(oldRecords);
                context.SaveChanges();
                logger.LogInformation("已清理 {Count} 条过期{Label}（早于 {Cutoff}）", count, label, cutoff);
            }
            return count;
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
