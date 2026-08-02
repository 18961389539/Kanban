using System.Globalization;
using System.Windows;
using System.Windows.Media;
using MainAPP.Converters;
using Xunit;

namespace MainAPP.Tests.Unit;


[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public sealed class LayoutConverterCoverageTests
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    [Theory]
    [InlineData(0.0, 100.0, 0.0, 100.0)]
    [InlineData(0.5, 100.0, 50.0, 50.0)]
    [InlineData(1.0, 100.0, 100.0, 0.0)]
    public void ProgressToDashArray_UsesConfiguredCircumference(
        double progress, double circumference, double expectedSolid, double expectedGap)
    {
        var converter = new ProgressToStrokeDashArrayConverter();
        var result = (DoubleCollection)converter.Convert(
            progress, typeof(DoubleCollection), circumference.ToString(Culture), Culture);

        Assert.Equal(2, result.Count);
        Assert.Equal(expectedSolid, result[0], 6);
        Assert.Equal(expectedGap, result[1], 6);
    }

    [Theory]
    [InlineData(-1.0, 0.0, 1.0)]
    [InlineData(2.0, 1.0, 0.0)]
    public void ProgressToDashArray_ClampsProgress(double progress, double expectedSolidRatio, double expectedGapRatio)
    {
        var converter = new ProgressToStrokeDashArrayConverter();
        var result = (DoubleCollection)converter.Convert(progress, typeof(DoubleCollection), "1", Culture);

        Assert.Equal(expectedSolidRatio, result[0], 6);
        Assert.Equal(expectedGapRatio, result[1], 6);
    }

    [Fact]
    public void ProgressToDashArray_UsesDefaultForInvalidInput()
    {
        var converter = new ProgressToStrokeDashArrayConverter();
        var result = (DoubleCollection)converter.Convert("50%", typeof(DoubleCollection), "invalid", Culture);

        Assert.Empty(result);
        var defaultResult = (DoubleCollection)converter.Convert(0.5, typeof(DoubleCollection), "0", Culture);
        Assert.Equal(2, defaultResult.Count);
        Assert.Equal(defaultResult[0], defaultResult[1], 6);
    }

    [Fact]
    public void ProgressToDashArray_ConvertBackThrows()
    {
        var converter = new ProgressToStrokeDashArrayConverter();
        Assert.Throws<NotSupportedException>(() =>
            converter.ConvertBack(new DoubleCollection(), typeof(double), null!, Culture));
    }

    [Theory]
    [InlineData(0.0, 0.0, GridUnitType.Pixel)]
    [InlineData(-1.0, 0.0, GridUnitType.Pixel)]
    [InlineData(0.25, 0.25, GridUnitType.Star)]
    public void RatioToGridLength_MapsPositiveRatioToStar(double value, double expectedValue, GridUnitType expectedType)
    {
        var converter = new RatioToGridLengthConverter();
        var result = (GridLength)converter.Convert(value, typeof(GridLength), null!, Culture);

        Assert.Equal(expectedValue, result.Value);
        Assert.Equal(expectedType, result.GridUnitType);
    }

    [Fact]
    public void RatioToGridLength_ConvertBackThrows()
    {
        var converter = new RatioToGridLengthConverter();
        Assert.Throws<NotSupportedException>(() =>
            converter.ConvertBack(new GridLength(1, GridUnitType.Star), typeof(double), null!, Culture));
    }

    [Fact]
    public void TimeSpanToDateTime_ConvertsBothDirections()
    {
        var converter = new TimeSpanToDateTimeConverter();
        var time = new TimeSpan(13, 45, 30);
        var date = (DateTime)converter.Convert(time, typeof(DateTime), null!, Culture);

        Assert.Equal(DateTime.Today.Add(time), date);
        Assert.Equal(time, converter.ConvertBack(date, typeof(TimeSpan), null!, Culture));
        Assert.Equal(DateTime.Today, converter.Convert("invalid", typeof(DateTime), null!, Culture));
        Assert.Equal(TimeSpan.Zero, converter.ConvertBack("invalid", typeof(TimeSpan), null!, Culture));
    }
}
