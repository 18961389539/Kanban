using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using Xunit;

namespace MainAPP.Tests.Unit;

public sealed class CalculateStateDurationsSnapshotTests
{
    private static StatusTransitionRecord Transition(int previous, int current, DateTime time) => new()
    {
        PreviousState = previous,
        CurrentState = current,
        EventTime = time,
    };

    [Fact]
    public void FullShiftWithMultipleStateChanges()
    {
        var start = new DateTime(2026, 7, 23, 8, 0, 0);
        var transitions = new List<StatusTransitionRecord>
        {
            Transition(1, 2, start.AddMinutes(10)),
            Transition(2, 3, start.AddMinutes(25)),
            Transition(3, 1, start.AddMinutes(40)),
            Transition(1, 2, start.AddMinutes(70)),
            Transition(2, 1, start.AddMinutes(85)),
        };

        var (run, alarm, paused, offline) = OeeCalculator.CalculateStateDurations(
            transitions, start, start.AddMinutes(120), initialState: 1);

        Assert.Equal(4500, run);
        Assert.Equal(1800, alarm);
        Assert.Equal(900, paused);
        Assert.Equal(0, offline);
    }

    [Fact]
    public void ConsecutiveAlarmTransitions()
    {
        var start = new DateTime(2026, 7, 23, 8, 0, 0);
        var transitions = new List<StatusTransitionRecord>
        {
            Transition(1, 2, start.AddMinutes(5)),
            Transition(2, 2, start.AddMinutes(10)),
            Transition(2, 1, start.AddMinutes(20)),
            Transition(1, 2, start.AddMinutes(30)),
        };

        var (run, alarm, paused, offline) = OeeCalculator.CalculateStateDurations(
            transitions, start, start.AddMinutes(40), initialState: 1);

        Assert.Equal(900, run);
        Assert.Equal(1500, alarm);
        Assert.Equal(0, paused);
        Assert.Equal(0, offline);
    }
}
