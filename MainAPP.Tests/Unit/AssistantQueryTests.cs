using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Models;
using Xunit;
using MainAPP.Services;

namespace MainAPP.Tests.Unit;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public class AssistantQueryTests
{
    private static readonly DateTime Now = new(2026, 10, 2, 12, 0, 0);

    [Fact]
    public void Parse_UsesOneCalendarWindowForYesterdayAndTheDayBefore()
    {
        Assert.True(AssistantQuery.TryParse("前天的合格率", Now, [], null, out var before));
        Assert.True(AssistantQuery.TryParse("昨天的合格率", Now, [], null, out var yesterday));

        Assert.Equal(AssistantTimeKind.Calendar, before.Windows[0].Kind);
        Assert.Equal(AssistantTimeKind.Calendar, yesterday.Windows[0].Kind);
        Assert.Equal(Now.Date.AddDays(-2), before.Windows[0].From);
        Assert.Equal(before.Windows[0].From.AddDays(1), yesterday.Windows[0].From);
        Assert.True(before.Pieces);
        Assert.True(yesterday.Pieces);
        Assert.True(AssistantQuery.TryParse("工单昨天的产量", Now, [], null, out var withOrder));
        Assert.True(withOrder.Pieces);
    }

    [Theory]
    [InlineData("合格率")]
    [InlineData("昨天怎么样")]
    [InlineData("去年的快照")]
    public void Parse_StopsWhenTimeOrMetricIsMissing(string question)
    {
        Assert.False(AssistantQuery.TryParse(question, Now, [], null, out _));
    }

    [Fact]
    public void ReadWindows_KeepsTheDayWhenTheQuestionHasNoMetric()
    {
        var windows = AssistantQuery.ReadWindows("昨天有哪些报警", Now);
        Assert.Single(windows);
        Assert.Equal(Now.Date.AddDays(-1), windows[0].From);
        Assert.Equal(Now.Date.AddTicks(-1), windows[0].To);
    }

    [Fact]
    public void Parse_TurnsLastYearADayAndARollingWeekIntoCalendarWindows()
    {
        Assert.True(AssistantQuery.TryParse("去年的产量", Now, [], null, out var year));
        Assert.Equal(new DateTime(2025, 1, 1), year.Windows[0].From);

        Assert.True(AssistantQuery.TryParse("3天前的合格率", Now, [], null, out var daysAgo));
        Assert.Equal(Now.Date.AddDays(-3), daysAgo.Windows[0].From);

        Assert.True(AssistantQuery.TryParse("近7天的产量", Now, [], null, out var week));
        Assert.Equal(Now.Date.AddDays(-6), week.Windows[0].From);
        Assert.Equal(Now, week.Windows[0].To);

        Assert.True(AssistantQuery.TryParse("2026-09-01的产量", Now, [], null, out var dated));
        Assert.Equal(new DateTime(2026, 9, 1), dated.Windows[0].From);
    }

    [Fact]
    public void Parse_ReadsTwoWindowsAndANamedDeviceFromOneQuestion()
    {
        Assert.True(AssistantQuery.TryParse("今天和昨天的产量", Now, [], null, out var both));
        Assert.Equal(2, both.Windows.Count);
        Assert.True(both.Plant);

        Assert.True(AssistantQuery.TryParse("注塑机A1昨天的合格率", Now, ["注塑机A1", "焊接机B1"], null, out var one));
        Assert.False(one.Plant);
        Assert.Equal(["注塑机A1"], one.DeviceNames);
        Assert.False(AssistantQuery.TryParse("这台昨天的产量", Now, [], null, out _));
        Assert.True(AssistantQuery.TryParse("这台昨天的产量", Now, [], "注塑机A1", out var selected));
        Assert.True(selected.UseSelectedDevice);
        Assert.False(selected.Plant);
    }

    [Fact]
    public void Render_FillsTheSameSentenceForAnyDay()
    {
        var before = Ask("前天的合格率");
        var yesterday = Ask("昨天的合格率");
        var devices = new List<Device> { new() { Id = "a", Name = "注塑机A1" } };
        var logs = new Dictionary<string, List<ProductionLog>>
        {
            ["a"] =
            [
                Log("a", new DateTime(2026, 9, 30, 10, 0, 0), 10, 2),
                Log("a", new DateTime(2026, 10, 1, 10, 0, 0), 4, 1),
            ],
        };

        var beforeText = Show(before, devices, logs);
        var yesterdayText = Show(yesterday, devices, logs);

        Assert.Contains("2026-09-30 00:00 至 2026-09-30 23:59，全厂：合格 10 件，不良 2 件，合计 12 件，良品率 83.3%。这是窗口差分。", beforeText, StringComparison.Ordinal);
        Assert.Contains("2026-10-01 00:00 至 2026-10-01 23:59，全厂：合格 4 件，不良 1 件，合计 5 件，良品率 80.0%。这是窗口差分。", yesterdayText, StringComparison.Ordinal);
        Assert.DoesNotContain("前天", beforeText, StringComparison.Ordinal);
        Assert.DoesNotContain("昨天", yesterdayText, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_SubtractsTwoWindowsAndNamesTheLowestQuality()
    {
        var ask = Ask("今天和昨天合格率最低的是哪台");
        var devices = new List<Device>
        {
            new() { Id = "a", Name = "注塑机A1" },
            new() { Id = "b", Name = "焊接机B1" },
        };
        var logs = new Dictionary<string, List<ProductionLog>>
        {
            ["a"] =
            [
                Log("a", new DateTime(2026, 10, 1, 9, 0, 0), 1, 1),
                Log("a", new DateTime(2026, 10, 2, 9, 0, 0), 9, 1),
            ],
            ["b"] =
            [
                Log("b", new DateTime(2026, 10, 1, 9, 0, 0), 3, 1),
                Log("b", new DateTime(2026, 10, 2, 9, 0, 0), 1, 1),
            ],
        };

        var text = Show(ask, devices, logs);

        Assert.Contains("2026-10-02 00:00 至 2026-10-02 12:00 比 2026-10-01 00:00 至 2026-10-01 23:59 多 6 件。两段都是窗口差分。", text, StringComparison.Ordinal);
        Assert.Contains("合格率最低的是 注塑机A1，50.0%。", text, StringComparison.Ordinal);
        Assert.Contains("合格率最低的是 焊接机B1，50.0%。", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_SaysWhenTheWindowIsOutsideRetentionOrHasNoPieces()
    {
        var year = Ask("去年的产量");
        var text = Show(year, [new Device { Id = "a", Name = "注塑机A1" }], new Dictionary<string, List<ProductionLog>>());
        Assert.Contains("超出保留的 365 天，没有查到。", text, StringComparison.Ordinal);
        Assert.DoesNotContain("良品率", text, StringComparison.Ordinal);

        var empty = Ask("昨天的产量");
        var emptyText = Show(empty, [new Device { Id = "a", Name = "注塑机A1" }], new Dictionary<string, List<ProductionLog>>());
        Assert.Contains("合格 0 件，不良 0 件，合计 0 件。这段没有产量，没有良品率。", emptyText, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_LabelsAShiftWindowAndSumsAlarmHours()
    {
        Assert.True(AssistantQuery.TryParse("当前班次的报警时长", Now, [], null, out var ask));
        Assert.Equal(AssistantTimeKind.CurrentShift, ask.Windows[0].Kind);
        var devices = new List<Device> { new() { Id = "a", Name = "注塑机A1" } };
        var shifts = new List<ShiftConfig>
        {
            new() { Name = "早班", StartTime = TimeSpan.FromHours(8), EndTime = TimeSpan.FromHours(20) },
        };
        var transitions = new Dictionary<string, List<StatusTransitionRecord>>
        {
            ["a"] =
            [
                new()
                {
                    DeviceId = "a",
                    DeviceName = "注塑机A1",
                    EventTime = Now.Date.AddHours(8),
                    PreviousState = (int)DeviceStatus.Running,
                    CurrentState = (int)DeviceStatus.Alarm,
                },
            ],
        };
        var windows = AssistantQuery.ResolveWindows(ask, Now, shifts, 365);
        var text = AssistantQuery.Render(ask, devices, null, windows, new Dictionary<string, List<ProductionLog>>(), false, transitions, false);

        Assert.Contains("班次窗 早班（2026-10-02 08:00 至 2026-10-02 12:00）", text, StringComparison.Ordinal);
        Assert.Contains("报警时长合计 4.0 小时", text, StringComparison.Ordinal);
        Assert.Contains("不是报警次数", text, StringComparison.Ordinal);
        Assert.DoesNotContain("首页累计", text, StringComparison.Ordinal);
    }

    private static AssistantAsk Ask(string question)
    {
        Assert.True(AssistantQuery.TryParse(question, Now, ["注塑机A1", "焊接机B1"], null, out var ask));
        return ask;
    }

    private static string Show(AssistantAsk ask, IReadOnlyList<Device> devices, IReadOnlyDictionary<string, List<ProductionLog>> logs)
    {
        var windows = AssistantQuery.ResolveWindows(ask, Now, [], 365);
        return AssistantQuery.Render(ask, devices, null, windows, logs, false, null, false);
    }

    private static ProductionLog Log(string deviceId, DateTime timestamp, int ok, int ng) => new()
    {
        DeviceId = deviceId,
        ShiftName = timestamp.ToString("yyyyMMdd"),
        Timestamp = timestamp,
        OkProduction = ok,
        NgProduction = ng,
    };
}
