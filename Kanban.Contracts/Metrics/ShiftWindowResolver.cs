namespace Kanban.Contracts.Metrics;

/// <summary>
/// 班次墙钟区间解析（不依赖 NodaTime / Collector.Core）。
/// 口径对齐 <c>Kanban.Collector.Core.Models.ShiftConfig.ResolveRange</c>：
/// 返回包含 reference 的最近一次实例 [Start, End)；落在班次间隙时取最晚开始 ≤ reference 的实例。
/// EndTime 允许 24:00（<see cref="TimeSpan.Ticks"/> 满一天），按当天结束归一化为 00:00 并走跨天分支。
/// </summary>
public static class ShiftWindowResolver
{
    public static (DateTime Start, DateTime End) ResolveRange(TimeSpan startTime, TimeSpan endTime, DateTime reference)
    {
        var startTod = TimeSpan.FromTicks(startTime.Ticks % TimeSpan.TicksPerDay);
        var endTod = TimeSpan.FromTicks(endTime.Ticks % TimeSpan.TicksPerDay);
        var date = reference.Date;

        if (startTod <= endTod)
        {
            var start = date + startTod;
            var end = date + endTod;
            if (reference < start)
            {
                start = start.AddDays(-1);
                end = end.AddDays(-1);
            }
            return (start, end);
        }

        if (reference.TimeOfDay >= startTod)
            return (date + startTod, date.AddDays(1) + endTod);
        return (date.AddDays(-1) + startTod, date + endTod);
    }
}
