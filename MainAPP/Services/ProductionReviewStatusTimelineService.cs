using Kanban.Core.Services;
using Kanban.Core.Models;
using Kanban.Core.Data;
using Kanban.Core.Entities;
using Kanban.Core.Entities;
using Kanban.Core.Models;
using MainAPP.Models;
using MainAPP.Resources;

namespace MainAPP.Services;

public interface IProductionReviewStatusTimelineService
{
    IReadOnlyList<ReviewStatusSegmentData> Build(
        string deviceId,
        IReadOnlyList<StatusTransitionRecord> transitions,
        IReadOnlyList<AlarmEventRecord> alarms,
        IReadOnlyList<ProductionLog> productionLogs,
        DateTime initialStatusTime,
        int initialState,
        DateTime from,
        DateTime to);
}

public sealed class ProductionReviewStatusTimelineService : IProductionReviewStatusTimelineService
{
    public IReadOnlyList<ReviewStatusSegmentData> Build(
        string deviceId,
        IReadOnlyList<StatusTransitionRecord> transitions,
        IReadOnlyList<AlarmEventRecord> alarms,
        IReadOnlyList<ProductionLog> productionLogs,
        DateTime initialStatusTime,
        int initialState,
        DateTime from,
        DateTime to)
    {
        var ordered = transitions
            .Where(t => t.DeviceId == deviceId && t.EventTime >= from && t.EventTime <= to)
            .OrderBy(t => t.EventTime)
            .ToList();
        var segments = new List<ReviewStatusSegmentData>();
        var cursor = from;
        var state = initialState;
        foreach (var transition in ordered)
        {
            if (transition.EventTime > cursor)
                segments.Add(CreateSegment(state, cursor, transition.EventTime, productionLogs, alarms));
            cursor = transition.EventTime;
            state = transition.CurrentState;
        }
        if (to > cursor)
            segments.Add(CreateSegment(state, cursor, to, productionLogs, alarms));
        return segments.Where(segment => (segment.End - segment.Start).TotalMinutes >= 0.1).ToList();
    }

    private static ReviewStatusSegmentData CreateSegment(
        int state,
        DateTime start,
        DateTime end,
        IReadOnlyList<ProductionLog> productionLogs,
        IReadOnlyList<AlarmEventRecord> alarms)
    {
        var output = ProductionReviewCalculations.CalculateProductionDelta(productionLogs, start, end);
        var alarmCount = alarms.Count(alarm => alarm.EventType == AlarmEventType.Triggered
            && alarm.EventTime >= start && alarm.EventTime < end);
        var statusText = state switch
        {
            (int)DeviceStatus.Running => Strings.Status_Running,
            (int)DeviceStatus.Paused => Strings.Status_Paused,
            (int)DeviceStatus.Alarm => Strings.Status_Alarm,
            0 => "断线",
            _ => Strings.Status_Unknown,
        };
        return new ReviewStatusSegmentData(
            start,
            end,
            statusText,
            state,
            output,
            alarmCount,
            state == (int)DeviceStatus.Running && output == 0);
    }
}
