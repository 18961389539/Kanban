using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using Xunit;

namespace MainAPP.Tests.Unit;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public sealed class AllenBradleyAddressCodecTests
{
    private static IPlcAddressCodec CreateCodec()
    {
        var settings = new AppSettings { PlcConfig = new PlcConfig { Brand = PlcBrand.AllenBradley } };
        return new PlcAddressCodecResolver(settings).Resolve(PlcBrand.AllenBradley);
    }

    [Theory]
    [InlineData("Count", PlcAddressType.DWord, 0, "COUNT")]
    [InlineData("A1", PlcAddressType.DWord, 0, "A1")]
    [InlineData("Counts[5]", PlcAddressType.DWord, 5, "COUNTS")]
    [InlineData("Program:Main.Count", PlcAddressType.DWord, 0, "PROGRAM:MAIN.COUNT")]
    [InlineData("Flags.3", PlcAddressType.MBit, 3, "FLAGS")]
    [InlineData("Counts[2].3", PlcAddressType.MBit, 3, "COUNTS[2]")]
    public void Parse_RecognizesTagForms(string address, PlcAddressType type, int offset, string group)
    {
        var result = CreateCodec().Parse(address);

        Assert.True(result.IsValid, result.ErrorMessage);
        Assert.Equal(type, result.Type);
        Assert.Equal(offset, result.AddressOffset);
        Assert.Equal(group, result.AddressGroup);
    }

    [Theory]
    [InlineData("")]
    [InlineData("123")]
    [InlineData("Count.")]
    [InlineData("Counts[]")]
    [InlineData("Program:Main")]
    [InlineData("Flags.32")]
    public void Parse_RejectsMalformedTags(string address)
    {
        Assert.False(CreateCodec().Parse(address).IsValid);
    }

    [Fact]
    public void Add_ArrayElement_IncrementsIndex()
    {
        Assert.Equal("COUNTS[6]", CreateCodec().Add("Counts[5]", 1));
    }

    [Fact]
    public void Add_Bit_IncrementsBitIndex()
    {
        Assert.Equal("FLAGS.4", CreateCodec().Add("Flags.3", 1));
    }

    [Fact]
    public void Add_ScalarTag_RejectsNonZeroOffset()
    {
        Assert.Throws<FormatException>(() => CreateCodec().Add("Count", 1));
    }

    [Fact]
    public void ToTransportAddress_KeepsProgramScopePrefix()
    {
        Assert.Equal("Program:MAIN.COUNT", CreateCodec().ToTransportAddress("program:Main.Count"));
    }

    [Fact]
    public void ScalarTags_DoNotShareABatchGroup()
    {
        var codec = CreateCodec();
        var count = codec.Parse("Count");
        var speed = codec.Parse("Speed");

        Assert.NotEqual(count.AddressGroup, speed.AddressGroup);
    }
}
