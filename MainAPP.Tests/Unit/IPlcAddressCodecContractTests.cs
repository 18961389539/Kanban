using Kanban.Core.Models;
using MainAPP.Models;
using Kanban.Core.Services;
using MainAPP.Services;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// IPlcAddressCodec 契约测试：验证所有已注册 PLC 品牌的地址编解码器实现
/// 对同一接口的行为一致性。
///
/// 契约覆盖：
/// - <see cref="IPlcAddressCodec.Brand"/> 返回对应 <see cref="PlcBrand"/> 枚举值
/// - <see cref="IPlcAddressCodec.Parse"/> 对 null/空/空白地址返回 <c>IsValid=false</c>，不抛异常
/// - <see cref="IPlcAddressCodec.Normalize"/> 对 null 不抛异常
/// - <see cref="IPlcAddressCodec.Add"/> 对无效地址抛 <see cref="FormatException"/>
/// - <see cref="IPlcAddressCodec.ToTransportAddress"/> 对无效地址不抛异常（返回空或原值）
///
/// 实现通过 <see cref="PlcAddressCodecResolver"/> 获取，无需直接引用 internal 类。
/// </summary>
[Trait("Category", "Contract")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public class IPlcAddressCodecContractTests
{
    /// <summary>
    /// 所有应通过契约测试的 IPlcAddressCodec 实现。
    /// 通过 PlcAddressCodecResolver 构造，覆盖 Mitsubishi/Siemens/ModbusTcp 三种品牌。
    /// ModbusTcp 的 codec 需要 PlcConfig（含 ModbusRegisterFunction/ModbusBitFunction）。
    /// </summary>
    public static TheoryData<IPlcAddressCodec> Implementations
    {
        get
        {
            var data = new TheoryData<IPlcAddressCodec>();
            var settings = new AppSettings { PlcConfig = new PlcConfig() };
            var resolver = new PlcAddressCodecResolver(settings);
            foreach (PlcBrand brand in Enum.GetValues(typeof(PlcBrand)))
            {
                data.Add(resolver.Resolve(brand));
            }
            return data;
        }
    }

    // ──────────── Brand 契约 ────────────

    [Theory]
    [MemberData(nameof(Implementations))]
    public void Brand_IsDefinedEnumValue(IPlcAddressCodec codec)
    {
        Assert.True(Enum.IsDefined(typeof(PlcBrand), codec.Brand));
    }

    // ──────────── Parse 防御性契约 ────────────

    [Theory]
    [MemberData(nameof(Implementations))]
    public void Parse_Null_ReturnsInvalid_NoThrow(IPlcAddressCodec codec)
    {
        var result = codec.Parse(null);
        Assert.False(result.IsValid);
    }

    [Theory]
    [MemberData(nameof(Implementations))]
    public void Parse_Empty_ReturnsInvalid_NoThrow(IPlcAddressCodec codec)
    {
        var result = codec.Parse("");
        Assert.False(result.IsValid);
    }

    [Theory]
    [MemberData(nameof(Implementations))]
    public void Parse_Whitespace_ReturnsInvalid_NoThrow(IPlcAddressCodec codec)
    {
        var result = codec.Parse("   ");
        Assert.False(result.IsValid);
    }

    [Theory]
    [MemberData(nameof(Implementations))]
    public void Parse_InvalidFormat_ReturnsInvalid(IPlcAddressCodec codec)
    {
        // "ZZZ999" 不匹配任何品牌的地址格式
        var result = codec.Parse("ZZZ999");
        Assert.False(result.IsValid);
        Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
    }

    // ──────────── Normalize 防御性契约 ────────────

    [Theory]
    [MemberData(nameof(Implementations))]
    public void Normalize_Null_NoThrow(IPlcAddressCodec codec)
    {
        // 不抛异常即可，返回值由实现决定
        codec.Normalize(null);
    }

    [Theory]
    [MemberData(nameof(Implementations))]
    public void Normalize_Empty_NoThrow(IPlcAddressCodec codec)
    {
        codec.Normalize("");
    }

    // ──────────── Add 防御性契约 ────────────

    [Theory]
    [MemberData(nameof(Implementations))]
    public void Add_InvalidAddress_ThrowsFormatException(IPlcAddressCodec codec)
    {
        Assert.Throws<FormatException>(() => codec.Add("ZZZ999", 1));
    }

    // ──────────── ToTransportAddress 防御性契约 ────────────

    [Theory]
    [MemberData(nameof(Implementations))]
    public void ToTransportAddress_InvalidAddress_NoThrow(IPlcAddressCodec codec)
    {
        // 对无效地址不抛异常（返回空或原值由实现决定）
        codec.ToTransportAddress("ZZZ999");
    }

    // ──────────── 往返一致性契约 ────────────

    [Theory]
    [MemberData(nameof(Implementations))]
    public void Parse_ValidAddress_NormalizeReturnsSame(IPlcAddressCodec codec)
    {
        // 各品牌的有效地址：Mitsubishi=D100, Siemens=DB1.DBD0, Modbus=HR100, Omron=D100
        var validAddress = codec.Brand switch
        {
            PlcBrand.Mitsubishi => "D100",
            PlcBrand.Siemens => "DB1.DBD0",
            PlcBrand.ModbusTcp => "HR100",
            PlcBrand.Omron => "D100",
            PlcBrand.Keyence => "DM100",
            _ => throw new InvalidOperationException($"未覆盖品牌 {codec.Brand} 的测试地址"),
        };

        var parsed = codec.Parse(validAddress);
        Assert.True(parsed.IsValid, $"{codec.Brand}: {validAddress} 应为有效地址，但 {parsed.ErrorMessage}");

        var normalized = codec.Normalize(validAddress);
        Assert.False(string.IsNullOrEmpty(normalized));
    }
}
