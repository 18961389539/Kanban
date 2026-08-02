using MainAPP.Entities;

namespace MainAPP.Services;

public interface IProductionReviewAlarmAnalysisService
{
    IReadOnlyList<ReviewAlarmAnalysisData> Analyze(
        IReadOnlyList<AlarmEventRecord> events,
        IReadOnlyList<ProductionLog> productionLogs);
}

public sealed class ProductionReviewAlarmAnalysisService : IProductionReviewAlarmAnalysisService
{
    public IReadOnlyList<ReviewAlarmAnalysisData> Analyze(
        IReadOnlyList<AlarmEventRecord> events,
        IReadOnlyList<ProductionLog> productionLogs)
    {
        return events
            .Where(e => e.EventType == AlarmEventType.Triggered)
            .GroupBy(e => new { e.AlarmName, e.DeviceName, e.PlcAddress })
            .Select(group =>
            {
                var triggers = group.OrderBy(e => e.EventTime).ToList();
                var intervals = triggers.Zip(triggers.Skip(1),
                    (first, second) => (second.EventTime - first.EventTime).TotalMinutes).ToList();
                var averageInterval = intervals.Count > 0 ? intervals.Average() : 0;
                return new ReviewAlarmAnalysisData(
                    group.Key.AlarmName,
                    group.Key.DeviceName,
                    group.Key.PlcAddress,
                    triggers.Count,
                    averageInterval,
                    triggers.Count >= 3 || (triggers.Count >= 2 && averageInterval <= 30),
                    triggers.Sum(trigger => ProductionReviewCalculations.CalculateProductionDelta(
                        productionLogs, trigger.EventTime.AddMinutes(-15), trigger.EventTime)),
                    triggers.Sum(trigger => ProductionReviewCalculations.CalculateProductionDelta(
                        productionLogs, trigger.EventTime, trigger.EventTime.AddMinutes(15))),
                    triggers[0].ShiftName,
                    CalculateAlarmDurationHours(events, group.Key.AlarmName, group.Key.DeviceName));
            })
            .OrderByDescending(item => item.TriggerCount)
            .ThenBy(item => item.AverageIntervalMinutes == 0 ? double.MaxValue : item.AverageIntervalMinutes)
            .Take(5)
            .ToList();
    }

    private static double CalculateAlarmDurationHours(
        IReadOnlyList<AlarmEventRecord> events,
        string alarmName,
        string deviceName)
    {
        var grouped = events.Where(e => e.AlarmName == alarmName && e.DeviceName == deviceName)
            .OrderBy(e => e.EventTime)
            .ToList();
        double seconds = 0;
        for (var index = 0; index < grouped.Count; index++)
        {
            if (grouped[index].EventType != AlarmEventType.Triggered) continue;
            var end = DateTime.Now;
            for (var next = index + 1; next < grouped.Count; next++)
            {
                if (grouped[next].EventType == AlarmEventType.Recovered)
                {
                    end = grouped[next].EventTime;
                    break;
                }
            }
            seconds += Math.Max(0, (end - grouped[index].EventTime).TotalSeconds);
        }
        return seconds / 3600.0;
    }
}
