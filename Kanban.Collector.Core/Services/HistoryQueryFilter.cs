using System.Linq.Expressions;

namespace Kanban.Collector.Core.Services;

internal static class HistoryQueryFilter
{
    public static IQueryable<T> ApplyRange<T>(
        IQueryable<T> query,
        DateTime from,
        DateTime to,
        string? deviceId,
        string? shiftName,
        string timeProperty,
        string deviceProperty,
        string shiftProperty)
    {
        var parameter = Expression.Parameter(typeof(T), "x");
        Expression body = Expression.AndAlso(
            Expression.GreaterThanOrEqual(Expression.Property(parameter, timeProperty), Expression.Constant(from)),
            Expression.LessThanOrEqual(Expression.Property(parameter, timeProperty), Expression.Constant(to)));
        if (deviceId != null)
            body = Expression.AndAlso(body, Expression.Equal(Expression.Property(parameter, deviceProperty), Expression.Constant(deviceId)));
        if (shiftName != null)
            body = Expression.AndAlso(body, Expression.Equal(Expression.Property(parameter, shiftProperty), Expression.Constant(shiftName)));
        return query.Where(Expression.Lambda<Func<T, bool>>(body, parameter));
    }
}
