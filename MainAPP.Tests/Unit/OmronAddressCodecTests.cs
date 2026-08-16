using Kanban.Collector.Core.Models;
using MainAPP.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using Xunit;

namespace MainAPP.Tests.Unit;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public sealed class OmronAddressCodecTests
{
    private static IPlcAddressCodec CreateCodec()
    {
        var settings = new AppSettings { PlcConfig = new PlcConfig { Brand = PlcBrand.Omron } };
        return new PlcAddressCodecResolver(settings).Resolve(PlcBrand.Omron);
    }

    [Theory]
    [InlineData("D100", PlcAddressType.DWord, "D")]
    [InlineData("DM100", PlcAddressType.DWord, "D")]
    [InlineData("CIO20", PlcAddressType.DWord, "C")]
    [InlineData("W10", PlcAddressType.DWord, "W")]
    [InlineData("H10.3", PlcAddressType.MBit, "H")]
    [InlineData("E0.0", PlcAddressType.MBit, "E")]
    public void Parse_RecognizesOmronAddress(string address, PlcAddressType type, string group)
    {
        var result = CreateCodec().Parse(address);

        Assert.True(result.IsValid, result.ErrorMessage);
        Assert.Equal(type, result.Type);
        Assert.Equal(group, result.AddressGroup);
    }

    [Fact]
    public void Add_Int32_UsesTwoWordStride()
    {
        var address = CreateCodec().Add("D100", 1);

        Assert.Equal("D102", address);
    }
}
