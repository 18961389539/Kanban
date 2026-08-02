using System;
using MainAPP.Models;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// 班次配置（跨天）纯逻辑单元测试。
/// 重点覆盖跨天班次的 Contains / ResolveRange / GetCurrentStart —— 这是历史 OEE 重建、
/// 本班次查询的边界计算，魔法数密集、易错，且现有测试未覆盖。
/// </summary>
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class ShiftConfigTests
{
    // 固定参考日，避免依赖 DateTime.Now
    private static readonly DateTime Day = new(2026, 7, 23, 0, 0, 0); // 周四

    private static DateTime At(int h, int m = 0) => Day.AddHours(h).AddMinutes(m);

    // ──────────── Contains（不跨天） ────────────

    [Fact]
    public void Contains_DayShift_Inside()
    {
        var s = new ShiftConfig { Name = "白班", StartTime = new(8, 0, 0), EndTime = new(20, 0, 0) };
        Assert.True(s.Contains(new TimeSpan(10, 0, 0)));
        Assert.True(s.Contains(new TimeSpan(8, 0, 0)));   // 起始点包含
    }

    [Fact]
    public void Contains_DayShift_BeforeStart_Excluded()
    {
        var s = new ShiftConfig { Name = "白班", StartTime = new(8, 0, 0), EndTime = new(20, 0, 0) };
        Assert.False(s.Contains(new TimeSpan(7, 59, 0)));
    }

    [Fact]
    public void Contains_DayShift_EndExclusive()
    {
        var s = new ShiftConfig { Name = "白班", StartTime = new(8, 0, 0), EndTime = new(20, 0, 0) };
        Assert.False(s.Contains(new TimeSpan(20, 0, 0))); // 结束点不含
    }

    // ──────────── Contains（跨天） ────────────

    [Fact]
    public void Contains_NightShift_Inside()
    {
        // 夜班 20:00-次日08:00
        var s = new ShiftConfig { Name = "夜班", StartTime = new(20, 0, 0), EndTime = new(8, 0, 0) };
        Assert.True(s.Contains(new TimeSpan(23, 0, 0)));
        Assert.True(s.Contains(new TimeSpan(2, 0, 0)));
        Assert.True(s.Contains(new TimeSpan(20, 0, 0))); // 起始点包含
    }

    [Fact]
    public void Contains_NightShift_Excluded()
    {
        var s = new ShiftConfig { Name = "夜班", StartTime = new(20, 0, 0), EndTime = new(8, 0, 0) };
        Assert.False(s.Contains(new TimeSpan(8, 0, 0)));  // 结束点不含
        Assert.False(s.Contains(new TimeSpan(19, 0, 0))); // 间隙
    }

    // ──────────── ResolveRange（不跨天） ────────────

    [Fact]
    public void ResolveRange_DayShift_SameDay()
    {
        var s = new ShiftConfig { Name = "白班", StartTime = new(8, 0, 0), EndTime = new(20, 0, 0) };
        var (start, end) = s.ResolveRange(At(10, 0));
        Assert.Equal(At(8, 0), start);
        Assert.Equal(At(20, 0), end);
    }

    [Fact]
    public void ResolveRange_DayShift_BeforeStart_ReturnsPreviousDay()
    {
        var s = new ShiftConfig { Name = "白班", StartTime = new(8, 0, 0), EndTime = new(20, 0, 0) };
        var (start, end) = s.ResolveRange(At(7, 0)); // 早于今日起始
        Assert.Equal(Day.AddDays(-1).AddHours(8), start);
        Assert.Equal(Day.AddDays(-1).AddHours(20), end);
    }

    [Fact]
    public void ResolveRange_DayShift_AfterEnd_ReturnsTodayInstance()
    {
        // 约定：返回包含 reference 的最晚 start ≤ reference 实例；
        // 21:00 的最晚 start 是今日 08:00
        var s = new ShiftConfig { Name = "白班", StartTime = new(8, 0, 0), EndTime = new(20, 0, 0) };
        var (start, end) = s.ResolveRange(At(21, 0));
        Assert.Equal(At(8, 0), start);
        Assert.Equal(At(20, 0), end);
    }

    // ──────────── ResolveRange（跨天） ────────────

    [Fact]
    public void ResolveRange_NightShift_EveningSegment()
    {
        // 23:00 → 今日 20:00 至 次日 08:00
        var s = new ShiftConfig { Name = "夜班", StartTime = new(20, 0, 0), EndTime = new(8, 0, 0) };
        var (start, end) = s.ResolveRange(At(23, 0));
        Assert.Equal(At(20, 0), start);
        Assert.Equal(Day.AddDays(1).AddHours(8), end);
    }

    [Theory]
    [InlineData(2, 0)]   // 凌晨段：昨日 20:00 → 今日 08:00
    [InlineData(12, 0)]  // 间隙段：取最晚 start ≤ reference（昨日 20:00）
    public void ResolveRange_NightShift_EarlyMorningOrGap_ReturnsPreviousInstance(int h, int m)
    {
        var s = new ShiftConfig { Name = "夜班", StartTime = new(20, 0, 0), EndTime = new(8, 0, 0) };
        var (start, end) = s.ResolveRange(At(h, m));
        Assert.Equal(Day.AddDays(-1).AddHours(20), start);
        Assert.Equal(At(8, 0), end);
    }

    // ──────────── GetCurrentStart ────────────

    [Fact]
    public void GetCurrentStart_DayShift_ReturnsTodayStart()
    {
        var s = new ShiftConfig { Name = "白班", StartTime = new(8, 0, 0), EndTime = new(20, 0, 0) };
        Assert.Equal(At(8, 0), s.GetCurrentStart(At(10, 0)));
        Assert.Equal(At(8, 0), s.GetCurrentStart(At(23, 0))); // 不跨天恒为今日起始
    }

    [Fact]
    public void GetCurrentStart_NightShift_Evening_ReturnsTodayStart()
    {
        var s = new ShiftConfig { Name = "夜班", StartTime = new(20, 0, 0), EndTime = new(8, 0, 0) };
        Assert.Equal(At(20, 0), s.GetCurrentStart(At(23, 0)));
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(12, 0)]
    public void GetCurrentStart_NightShift_EarlyMorningOrGap_ReturnsYesterdayStart(int h, int m)
    {
        var s = new ShiftConfig { Name = "夜班", StartTime = new(20, 0, 0), EndTime = new(8, 0, 0) };
        Assert.Equal(Day.AddDays(-1).AddHours(20), s.GetCurrentStart(At(h, m)));
    }

    // ──────────── DurationHours ────────────

    [Fact]
    public void DurationHours_CrossDay_Adds24()
    {
        var s = new ShiftConfig { Name = "夜班", StartTime = new(20, 0, 0), EndTime = new(8, 0, 0) };
        Assert.Equal(12.0, s.DurationHours);
    }

    [Fact]
    public void DurationHours_DayShift_Normal()
    {
        var s = new ShiftConfig { Name = "白班", StartTime = new(8, 0, 0), EndTime = new(20, 0, 0) };
        Assert.Equal(12.0, s.DurationHours);
    }

    // ──────────── 跨午夜边界精确测试 ────────────
    // 既有测试覆盖了"夜班内任意时刻"，但未精确验证 23:59:59 / 00:00:00 等边界分钟。
    // 跨夜班次 22:00-06:00 的逻辑：
    //   - 22:00:00 包含（StartTime 含）
    //   - 06:00:00 不含（EndTime 不含，左闭右开）
    //   - 23:59:59 包含（夜班内）
    //   - 00:00:00 包含（夜班凌晨段起点）
    //   - 05:59:59 包含（夜班凌晨段内）

    [Fact]
    public void Contains_CrossMidnight_AtExactStart_Included()
    {
        // 22:00-06:00 跨夜班次，22:00:00 应包含（左闭）
        var s = new ShiftConfig { Name = "夜班", StartTime = new(22, 0, 0), EndTime = new(6, 0, 0) };
        Assert.True(s.Contains(new TimeSpan(22, 0, 0)));
    }

    [Fact]
    public void Contains_CrossMidnight_AtExactEnd_Excluded()
    {
        // 06:00:00 应不含（右开）
        var s = new ShiftConfig { Name = "夜班", StartTime = new(22, 0, 0), EndTime = new(6, 0, 0) };
        Assert.False(s.Contains(new TimeSpan(6, 0, 0)));
    }

    [Fact]
    public void Contains_CrossMidnight_JustBeforeMidnight_Included()
    {
        var s = new ShiftConfig { Name = "夜班", StartTime = new(22, 0, 0), EndTime = new(6, 0, 0) };
        Assert.True(s.Contains(new TimeSpan(23, 59, 59)));
    }

    [Fact]
    public void Contains_CrossMidnight_AtMidnight_Included()
    {
        // 00:00:00 应包含（凌晨段起点）
        var s = new ShiftConfig { Name = "夜班", StartTime = new(22, 0, 0), EndTime = new(6, 0, 0) };
        Assert.True(s.Contains(new TimeSpan(0, 0, 0)));
    }

    [Fact]
    public void Contains_CrossMidnight_OneSecondBeforeEnd_Included()
    {
        var s = new ShiftConfig { Name = "夜班", StartTime = new(22, 0, 0), EndTime = new(6, 0, 0) };
        Assert.True(s.Contains(new TimeSpan(5, 59, 59)));
    }

    [Fact]
    public void Contains_CrossMidnight_GapBeforeStart_Excluded()
    {
        // 21:59:59 在白班结束后、夜班开始前的间隙
        var s = new ShiftConfig { Name = "夜班", StartTime = new(22, 0, 0), EndTime = new(6, 0, 0) };
        Assert.False(s.Contains(new TimeSpan(21, 59, 59)));
    }

    [Fact]
    public void Contains_CrossMidnight_GapAfterEnd_Excluded()
    {
        // 06:00:01 已结束，下一班次未开始的间隙
        var s = new ShiftConfig { Name = "夜班", StartTime = new(22, 0, 0), EndTime = new(6, 0, 0) };
        Assert.False(s.Contains(new TimeSpan(6, 0, 1)));
    }

    // ──────────── GetCurrentStart 跨午夜 ────────────
    // 跨夜班次的起始 DateTime 在凌晨段应返回昨日（前一天的 22:00），
    // 这是 OEE 重建范围下界计算的关键魔法数。

    [Fact]
    public void GetCurrentStart_CrossMidnight_Evening_ReturnsTodayStart()
    {
        // 参考时刻：23:00（夜班晚段）→ 起始 = 今日 22:00
        var s = new ShiftConfig { Name = "夜班", StartTime = new(22, 0, 0), EndTime = new(6, 0, 0) };
        var result = s.GetCurrentStart(At(23, 0));
        Assert.Equal(At(22, 0), result);
    }

    [Fact]
    public void GetCurrentStart_CrossMidnight_EarlyMorning_ReturnsYesterdayStart()
    {
        // 参考时刻：凌晨 02:00（夜班凌晨段）→ 起始 = 昨日 22:00
        var s = new ShiftConfig { Name = "夜班", StartTime = new(22, 0, 0), EndTime = new(6, 0, 0) };
        var result = s.GetCurrentStart(At(2, 0));
        Assert.Equal(At(-2, 0), result); // Day - 2h = 前一天 22:00
    }

    [Fact]
    public void GetCurrentStart_CrossMidnight_AtMidnight_ReturnsYesterdayStart()
    {
        // 参考时刻：00:00:00（夜班凌晨段最起点）→ 起始 = 昨日 22:00
        var s = new ShiftConfig { Name = "夜班", StartTime = new(22, 0, 0), EndTime = new(6, 0, 0) };
        var result = s.GetCurrentStart(At(0, 0));
        Assert.Equal(At(-2, 0), result);
    }

    [Fact]
    public void GetCurrentStart_CrossMidnight_AtExactStart_ReturnsTodayStart()
    {
        // 参考时刻：22:00:00（夜班起点）→ 起始 = 今日 22:00
        var s = new ShiftConfig { Name = "夜班", StartTime = new(22, 0, 0), EndTime = new(6, 0, 0) };
        var result = s.GetCurrentStart(At(22, 0));
        Assert.Equal(At(22, 0), result);
    }

    // ──────────── ResolveRange 跨午夜完整范围 ────────────

    [Fact]
    public void ResolveRange_CrossMidnight_FullNightShiftRange()
    {
        // 夜班 22:00-06:00，参考 23:00 → [今日 22:00, 明日 06:00]
        var s = new ShiftConfig { Name = "夜班", StartTime = new(22, 0, 0), EndTime = new(6, 0, 0) };
        var (start, end) = s.ResolveRange(At(23, 0));
        Assert.Equal(At(22, 0), start);                       // 今日 22:00
        Assert.Equal(start.AddHours(8), end);                 // +8h = 次日 06:00
        Assert.Equal(new TimeSpan(6, 0, 0), end.TimeOfDay);   // 06:00
    }

    [Fact]
    public void ResolveRange_CrossMidnight_EarlyMorning_ReturnsPreviousNightRange()
    {
        // 参考时刻：凌晨 02:00 → 起始 = 昨日 22:00，结束 = 今日 06:00
        var s = new ShiftConfig { Name = "夜班", StartTime = new(22, 0, 0), EndTime = new(6, 0, 0) };
        var (start, end) = s.ResolveRange(At(2, 0));
        Assert.Equal(At(-2, 0), start); // 昨日 22:00
        Assert.Equal(At(6, 0), end);    // 今日 06:00
        Assert.Equal(start.AddHours(8), end);
    }

    [Fact]
    public void ResolveRange_CrossMidnight_AtEnd_ReturnsPreviousNightRange()
    {
        // 参考时刻：06:00（结束点不含，落在间隙）→ 应返回昨夜班次的范围
        var s = new ShiftConfig { Name = "夜班", StartTime = new(22, 0, 0), EndTime = new(6, 0, 0) };
        var (start, end) = s.ResolveRange(At(6, 0));
        // 06:00 落在间隙，应返回最近的前一个夜班实例：昨日 22:00 ~ 今日 06:00
        Assert.Equal(At(-2, 0), start);
        Assert.Equal(At(6, 0), end);
    }
}
