using MainAPP.Models;

namespace MainAPP.Services;

/// <summary>
/// 本班小时计划桶：按班次墙钟切整点格，计划用额定产能（件/时）按重叠分钟折算，
/// 当前未结束小时按已过分钟折算应产。产量差分由调用方按与小时柱图同一套口径提供。
/// </summary>
public static class HourByHourBoardBuilder
{
    /// <summary>
    /// 生成 [shiftStart, shiftEnd) 内每一整点小时格。
    /// <paramref name="okByHourStart"/> 的键为该格墙钟整点（与 <see cref="Kanban.Contracts.Metrics.HourlyProductionDiff.BuildHourStarts"/> 对齐）。
    /// </summary>
    public static IReadOnlyList<HourBucketItem> Build(
        DateTime shiftStart,
        DateTime shiftEnd,
        DateTime now,
        int targetPcsPerHour,
        IReadOnlyDictionary<DateTime, int> okByHourStart)
    {
        if (shiftEnd <= shiftStart)
            return [];

        okByHourStart ??= new Dictionary<DateTime, int>();
        var items = new List<HourBucketItem>();
        var hour = new DateTime(shiftStart.Year, shiftStart.Month, shiftStart.Day, shiftStart.Hour, 0, 0);

        while (hour < shiftEnd)
        {
            var hourEnd = hour.AddHours(1);
            var overlapStart = hour > shiftStart ? hour : shiftStart;
            var overlapEnd = hourEnd < shiftEnd ? hourEnd : shiftEnd;
            if (overlapEnd <= overlapStart)
            {
                hour = hourEnd;
                continue;
            }

            var overlapMinutes = (overlapEnd - overlapStart).TotalMinutes;
            HourBucketState state;
            int plan;
            int? actual;

            if (now < overlapStart)
            {
                state = HourBucketState.Future;
                plan = RoundPlan(targetPcsPerHour, overlapMinutes);
                actual = null;
            }
            else if (now < overlapEnd)
            {
                state = HourBucketState.Current;
                plan = RoundPlan(targetPcsPerHour, (now - overlapStart).TotalMinutes);
                actual = okByHourStart.TryGetValue(hour, out var currentOk) ? currentOk : 0;
            }
            else
            {
                plan = RoundPlan(targetPcsPerHour, overlapMinutes);
                actual = okByHourStart.TryGetValue(hour, out var pastOk) ? pastOk : 0;
                state = targetPcsPerHour <= 0 || actual.Value >= plan
                    ? HourBucketState.Hit
                    : HourBucketState.Miss;
            }

            items.Add(new HourBucketItem(
                hour,
                hourEnd,
                $"{hour:HH}–{hourEnd:HH}",
                plan,
                actual,
                state));
            hour = hourEnd;
        }

        return items;
    }

    internal static int RoundPlan(int targetPcsPerHour, double minutes)
    {
        if (targetPcsPerHour <= 0 || minutes <= 0)
            return 0;
        return (int)Math.Round(targetPcsPerHour * minutes / 60.0, MidpointRounding.AwayFromZero);
    }
}
