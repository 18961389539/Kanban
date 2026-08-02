using MainAPP.Services;
using MainAPP.Models;
using Xunit;

namespace MainAPP.Tests.Unit;


[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public sealed class PlcBatchReadPlannerTests
{
    [Theory]
    [InlineData(" d100 ", "D100")]
    [InlineData("m10", "M10")]
    [InlineData("X10", "")]
    public void Normalize_ReturnsCanonicalAddress(string address, string expected)
    {
        Assert.Equal(expected, PlcAddressParser.Normalize(address));
    }

    [Fact]
    public void Plan_DWord_UsesTwoRegisterStride()
    {
        var blocks = PlcBatchReadPlanner.Plan(["D100", "D102", "D104", "D110"], PlcAddressType.DWord);

        Assert.Equal(2, blocks.Count);
        Assert.Equal(new PlcReadBlock("D100", 3, PlcAddressType.DWord), blocks[0]);
        Assert.Equal(new PlcReadBlock("D110", 1, PlcAddressType.DWord), blocks[1]);
    }

    [Fact]
    public void Plan_MBit_UsesOneBitStrideAndDeduplicates()
    {
        var blocks = PlcBatchReadPlanner.Plan(["M10", "M11", "M11", "M13"], PlcAddressType.MBit);

        Assert.Equal(2, blocks.Count);
        Assert.Equal(new PlcReadBlock("M10", 2, PlcAddressType.MBit), blocks[0]);
        Assert.Equal(new PlcReadBlock("M13", 1, PlcAddressType.MBit), blocks[1]);
    }

    [Fact]
    public void Plan_SplitsAtMaximumLength()
    {
        var blocks = PlcBatchReadPlanner.Plan(["D0", "D2", "D4"], PlcAddressType.DWord, 2);

        Assert.Equal(2, blocks.Count);
        Assert.Equal((ushort)2, blocks[0].Length);
        Assert.Equal((ushort)1, blocks[1].Length);
    }

    [Fact]
    public void Plan_AllowsConfiguredSingleSlotGap()
    {
        var blocks = PlcBatchReadPlanner.Plan(
            ["D10", "D12", "D16"],
            PlcAddressType.DWord,
            64,
            2,
            new MitsubishiAddressCodecForTest(),
            maxGapSlots: 1);

        var block = Assert.Single(blocks);
        Assert.Equal("D10", block.StartAddress);
        Assert.Equal((ushort)4, block.Length);
    }

    [Fact]
    public void Plan_DoesNotMergeGapBeyondConfiguredLimit()
    {
        var blocks = PlcBatchReadPlanner.Plan(
            ["D10", "D12", "D18"],
            PlcAddressType.DWord,
            64,
            2,
            new MitsubishiAddressCodecForTest(),
            maxGapSlots: 1);

        Assert.Equal(2, blocks.Count);
        Assert.Equal((ushort)2, blocks[0].Length);
        Assert.Equal((ushort)1, blocks[1].Length);
    }

    private sealed class MitsubishiAddressCodecForTest : IPlcAddressCodec
    {
        private readonly IPlcAddressCodec _inner = new MitsubishiAddressCodec();

        public PlcBrand Brand => _inner.Brand;
        public PlcAddressParseResult Parse(string? address) => _inner.Parse(address);
        public string Normalize(string? address) => _inner.Normalize(address);
        public string Add(string address, int logicalOffset) => _inner.Add(address, logicalOffset);
        public string ToTransportAddress(string address) => _inner.ToTransportAddress(address);
    }

    [Fact]
    public void Plan_SiemensDWord_UsesCodecAndFourByteStride()
    {
        var settings = new MainAPP.Services.AppSettings();
        settings.PlcConfig.Brand = MainAPP.Models.PlcBrand.Siemens;
        var codec = new PlcAddressCodecResolver(settings).Current;

        var blocks = PlcBatchReadPlanner.Plan(
            ["MD100", "MD104", "MD108"], PlcAddressType.DWord, 64, 4, codec);

        Assert.Single(blocks);
        Assert.Equal(new PlcReadBlock("MD100", 3, PlcAddressType.DWord), blocks[0]);
    }
}
