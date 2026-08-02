using MainAPP.Helpers;
using Xunit;

namespace MainAPP.Tests.Unit;


[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class FormatHelperTests
{
    [Theory]
    [InlineData(5, "▲5")]
    [InlineData(1, "▲1")]
    public void FormatDiff_Positive_PrefixUpArrow(int diff, string expected)
        => Assert.Equal(expected, FormatHelper.FormatDiff(diff));

    [Theory]
    [InlineData(-3, "▼3")]
    [InlineData(-1, "▼1")]
    public void FormatDiff_Negative_PrefixDownArrow(int diff, string expected)
        => Assert.Equal(expected, FormatHelper.FormatDiff(diff));

    [Fact]
    public void FormatDiff_Zero_ReturnsEmptyString()
        => Assert.Equal("", FormatHelper.FormatDiff(0));

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(45, "45s")]
    [InlineData(59, "59s")]
    public void FormatDuration_UnderOneMinute_SecondsOnly(double secs, string expected)
        => Assert.Equal(expected, FormatHelper.FormatDuration(secs));

    [Theory]
    [InlineData(60, "1m 0s")]
    [InlineData(90, "1m 30s")]
    [InlineData(125, "2m 5s")]
    public void FormatDuration_OneMinuteToUnderOneHour_MinutesSeconds(double secs, string expected)
        => Assert.Equal(expected, FormatHelper.FormatDuration(secs));

    [Theory]
    [InlineData(3600, "1h 0m")]
    [InlineData(3661, "1h 1m")]
    [InlineData(7325, "2h 2m")]
    public void FormatDuration_OneHourOrMore_HoursMinutes(double secs, string expected)
        => Assert.Equal(expected, FormatHelper.FormatDuration(secs));
}
