using Kanban.Contracts.Display;
using Xunit;

namespace MainAPP.Tests.Unit;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public class DisplayCarouselClockTests
{
    private static readonly DateTime T0 = new(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NextScene_SkipsAlarmCenter_WhenNoActiveAlarms()
    {
        Assert.Equal(DisplayCarousel.ProductionLine, DisplayCarousel.NextScene(DisplayCarousel.Home, false));
        Assert.Equal(DisplayCarousel.Home, DisplayCarousel.NextScene(DisplayCarousel.ProductionLine, false));
        Assert.Equal(DisplayCarousel.Home, DisplayCarousel.NextScene(DisplayCarousel.AlarmCenter, false));
    }

    [Fact]
    public void NextScene_IncludesAlarmCenter_WhenAlarmsActive()
    {
        Assert.Equal(DisplayCarousel.AlarmCenter, DisplayCarousel.NextScene(DisplayCarousel.ProductionLine, true));
        Assert.Equal(DisplayCarousel.Home, DisplayCarousel.NextScene(DisplayCarousel.AlarmCenter, true));
    }

    [Fact]
    public void HomeDwell_IsTwoHundredSeconds()
    {
        var clock = new DisplayCarouselClock();
        var status = clock.Step(EnabledOn(DisplayCarousel.Home, T0, 0));
        Assert.Equal(200, status.RemainingSeconds);
        Assert.Null(status.NavigateTo);

        status = clock.Step(EnabledOn(DisplayCarousel.Home, T0.AddSeconds(199), DisplayCarousel.HomeDwellMs - 1000));
        Assert.Equal(DisplayCarousel.Home, status.Scene);
        Assert.Null(status.NavigateTo);

        status = clock.Step(EnabledOn(DisplayCarousel.Home, T0.AddSeconds(200), 1000));
        Assert.Equal(DisplayCarousel.ProductionLine, status.NavigateTo);
    }

    [Fact]
    public void HighAlarm_NavigatesToAlarmCenter_AndFreezes()
    {
        var clock = new DisplayCarouselClock();
        clock.Step(EnabledOn(DisplayCarousel.Home, T0, 0));
        var status = clock.Step(new DisplayCarouselInput(T0.AddSeconds(1), 1000, true, true, true, DisplayCarousel.Home));
        Assert.True(status.Frozen);
        Assert.Equal(DisplayCarousel.AlarmCenter, status.NavigateTo);
    }

    [Fact]
    public void HighAlarm_AfterClick_StaysOnHome()
    {
        var clock = new DisplayCarouselClock();
        clock.Step(EnabledOn(DisplayCarousel.Home, T0, 0));
        clock.NoteInteraction(T0);
        var status = clock.Step(new DisplayCarouselInput(T0.AddSeconds(1), 1000, true, true, true, DisplayCarousel.Home));
        Assert.True(status.Paused);
        Assert.False(status.Frozen);
        Assert.Null(status.NavigateTo);
        Assert.Equal(DisplayCarousel.Home, status.Scene);
    }

    [Fact]
    public void HighAlarm_DoesNotStealSettingsPage()
    {
        var clock = new DisplayCarouselClock();
        var status = clock.Step(new DisplayCarouselInput(T0, 1000, true, true, true, "Settings"));
        Assert.False(status.OverlayVisible);
        Assert.Null(status.NavigateTo);
    }

    [Fact]
    public void HighAlarm_AfterPauseExpires_ReturnsToAlarmCenter()
    {
        var clock = new DisplayCarouselClock();
        clock.Step(EnabledOn(DisplayCarousel.Home, T0, 0));
        clock.NoteInteraction(T0);
        clock.Step(new DisplayCarouselInput(T0.AddSeconds(1), 1000, true, true, true, DisplayCarousel.Home));
        var status = clock.Step(new DisplayCarouselInput(
            T0.AddMilliseconds(DisplayCarousel.ResumeAfterInteractionMs), 1000, true, true, true, DisplayCarousel.Home));
        Assert.True(status.Frozen);
        Assert.Equal(DisplayCarousel.AlarmCenter, status.NavigateTo);
    }

    [Fact]
    public void Interaction_PausesForEightySeconds()
    {
        var clock = new DisplayCarouselClock();
        clock.Step(EnabledOn(DisplayCarousel.Home, T0, 0));
        clock.NoteInteraction(T0);
        var paused = clock.Step(EnabledOn(DisplayCarousel.Home, T0.AddSeconds(10), 10_000));
        Assert.True(paused.Paused);
        Assert.Equal(70, paused.RemainingSeconds);

        var resumed = clock.Step(EnabledOn(DisplayCarousel.Home, T0.AddSeconds(80), 1000));
        Assert.False(resumed.Paused);
        Assert.Equal(199, resumed.RemainingSeconds);
    }

    [Fact]
    public void NoteInteraction_ShorterHold_DoesNotCutLongerPause()
    {
        var clock = new DisplayCarouselClock();
        clock.Step(EnabledOn(DisplayCarousel.Home, T0, 0));
        clock.NoteInteraction(T0, DisplayCarousel.ResumeAfterNavMs);
        clock.NoteInteraction(T0.AddSeconds(1), DisplayCarousel.ResumeAfterInteractionMs);
        var status = clock.Step(new DisplayCarouselInput(
            T0.AddSeconds(30), 1000, true, true, true, DisplayCarousel.Home));
        Assert.True(status.Paused);
        Assert.Null(status.NavigateTo);
        Assert.Equal(DisplayCarousel.Home, status.Scene);
    }

    [Fact]
    public void HighAlarm_AfterNavHold_StaysOnLine()
    {
        var clock = new DisplayCarouselClock();
        clock.Step(EnabledOn(DisplayCarousel.ProductionLine, T0, 0));
        clock.NoteInteraction(T0, DisplayCarousel.ResumeAfterNavMs);
        var status = clock.Step(new DisplayCarouselInput(
            T0.AddSeconds(30), 1000, true, true, true, DisplayCarousel.ProductionLine));
        Assert.True(status.Paused);
        Assert.False(status.Frozen);
        Assert.Null(status.NavigateTo);
        Assert.Equal(DisplayCarousel.ProductionLine, status.Scene);
    }

    [Fact]
    public void SettingsPage_HidesOverlay_AndDoesNotNavigate()
    {
        var clock = new DisplayCarouselClock();
        var status = clock.Step(EnabledOn("Settings", T0, 1000));
        Assert.False(status.OverlayVisible);
        Assert.Null(status.NavigateTo);
    }

    [Fact]
    public void EmptyAlarmPage_SkipsImmediately()
    {
        var clock = new DisplayCarouselClock();
        var status = clock.Step(EnabledOn(DisplayCarousel.AlarmCenter, T0, 0, hasActive: false));
        Assert.Equal(DisplayCarousel.Home, status.NavigateTo);
    }

    private static DisplayCarouselInput EnabledOn(string page, DateTime utc, int deltaMs, bool hasActive = false) =>
        new(utc, deltaMs, Enabled: true, HasHighAlarm: false, HasActiveAlarms: hasActive, CurrentPageKey: page);
}
