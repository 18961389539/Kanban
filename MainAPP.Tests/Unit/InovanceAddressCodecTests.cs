using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using Xunit;

namespace MainAPP.Tests.Unit;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public sealed class InovanceAddressCodecTests
{
    private static IPlcAddressCodec CreateCodec(InovancePlcSeries series)
    {
        var config = new PlcConfig { Brand = PlcBrand.Inovance };
        config.Inovance.Series = series;
        var settings = new AppSettings { PlcConfig = config };
        return new PlcAddressCodecResolver(settings).Resolve(PlcBrand.Inovance);
    }

    [Theory]
    [InlineData(InovancePlcSeries.H5U, "D100", PlcAddressType.DWord, 100, 2, "D")]
    [InlineData(InovancePlcSeries.H5U, "M10", PlcAddressType.MBit, 10, 1, "M")]
    [InlineData(InovancePlcSeries.H5U, "X10", PlcAddressType.MBit, 8, 1, "X")]
    [InlineData(InovancePlcSeries.H5U, "Y7", PlcAddressType.MBit, 7, 1, "Y")]
    [InlineData(InovancePlcSeries.H3U, "SD100", PlcAddressType.DWord, 100, 2, "SD")]
    [InlineData(InovancePlcSeries.H3U, "SM0", PlcAddressType.MBit, 0, 1, "SM")]
    [InlineData(InovancePlcSeries.AM, "MW100", PlcAddressType.DWord, 100, 2, "MW")]
    [InlineData(InovancePlcSeries.AM, "MD50", PlcAddressType.DWord, 50, 1, "MD")]
    [InlineData(InovancePlcSeries.AM, "MB8", PlcAddressType.MBit, 8, 1, "MB")]
    public void Parse_RecognizesSeriesAddress(
        InovancePlcSeries series, string address, PlcAddressType type, int offset, int stride, string group)
    {
        var result = CreateCodec(series).Parse(address);

        Assert.True(result.IsValid, result.ErrorMessage);
        Assert.Equal(type, result.Type);
        Assert.Equal(offset, result.AddressOffset);
        Assert.Equal(stride, result.AddressStride);
        Assert.Equal(group, result.AddressGroup);
    }

    [Theory]
    [InlineData(InovancePlcSeries.H5U, "X8")]
    [InlineData(InovancePlcSeries.H3U, "B0")]
    [InlineData(InovancePlcSeries.AM, "D100")]
    [InlineData(InovancePlcSeries.H5U, "MW0")]
    public void Parse_RejectsAddressOutsideSeries(InovancePlcSeries series, string address)
    {
        Assert.False(CreateCodec(series).Parse(address).IsValid);
    }

    [Fact]
    public void Add_WordAddress_UsesTwoWordStride()
    {
        Assert.Equal("D102", CreateCodec(InovancePlcSeries.H5U).Add("D100", 1));
    }

    [Fact]
    public void Add_OctalBit_CarriesIntoNextDigit()
    {
        Assert.Equal("X10", CreateCodec(InovancePlcSeries.H5U).Add("X7", 1));
    }

    [Fact]
    public void Add_MdAddress_StepsByOneDint()
    {
        Assert.Equal("MD51", CreateCodec(InovancePlcSeries.AM).Add("MD50", 1));
    }

    [Fact]
    public void InputAreas_AreReadOnly()
    {
        var h5u = CreateCodec(InovancePlcSeries.H5U);
        var x = h5u.Parse("X0");
        var y = h5u.Parse("Y0");
        Assert.False(h5u.CanWrite(x));
        Assert.True(h5u.CanWrite(y));

        var am = CreateCodec(InovancePlcSeries.AM);
        Assert.False(am.CanWrite(am.Parse("IX0")));
        Assert.False(am.CanWrite(am.Parse("I10")));
        Assert.True(am.CanWrite(am.Parse("Q0")));
    }
}
