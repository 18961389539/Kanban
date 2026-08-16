using System.Text.RegularExpressions;

namespace Kanban.Collector.Core.Services;

/// <summary>
/// PLC 地址类型
/// </summary>
public enum PlcAddressType
{
    /// <summary>D 寄存器（字地址，如 D12001）</summary>
    DWord,

    /// <summary>M 中间继电器（位地址，如 M10001）</summary>
    MBit
}

/// <summary>
/// PLC 地址解析结果
/// </summary>
public readonly struct PlcAddressParseResult
{
    /// <summary>是否合法</summary>
    public bool IsValid { get; init; }

    /// <summary>地址类型</summary>
    public PlcAddressType Type { get; init; }

    /// <summary>原始字符串（去首尾空白）</summary>
    public string Original { get; init; }

    /// <summary>错误信息（IsValid=false 时有值）</summary>
    public string ErrorMessage { get; init; }

    /// <summary>地址在所属区域中的数值偏移；用于连续批量规划。</summary>
    public int AddressOffset { get; init; }

    /// <summary>相邻逻辑值之间的地址步长。</summary>
    public int AddressStride { get; init; }

    /// <summary>地址所属区域，例如 D、M、DB1、HR、C。</summary>
    public string AddressGroup { get; init; }

    public static PlcAddressParseResult Invalid(string original, string message) => new()
    {
        IsValid = false,
        Original = original?.Trim() ?? string.Empty,
        ErrorMessage = message
    };

    public static PlcAddressParseResult Valid(string original, PlcAddressType type, int addressOffset = 0, int addressStride = 1, string? addressGroup = null)
    {
        var trimmed = original.Trim().ToUpperInvariant();
        if (addressOffset == 0 && trimmed.Length > 1 && int.TryParse(trimmed[1..], out var inferredOffset))
            addressOffset = inferredOffset;
        return new PlcAddressParseResult
        {
            IsValid = true,
            Type = type,
            Original = trimmed,
            AddressOffset = addressOffset,
            AddressStride = addressStride,
            AddressGroup = addressGroup ?? trimmed[..1]
        };
    }
}

/// <summary>
/// PLC 地址解析/校验工具。
/// D 类地址格式：D + 数字（如 D12001），用于数据寄存器。
/// M 类地址格式：M + 数字（如 M10001），用于报警位。
/// </summary>
public static class PlcAddressParser
{
    private static readonly Regex DPattern = new(@"^D\d+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MPattern = new(@"^M\d+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SiemensDWordPattern = new(@"^DB(?<db>\d+)\.DBD(?<offset>\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SiemensBitPattern = new(@"^DB(?<db>\d+)\.DBX(?<byte>\d+)\.(?<bit>[0-7])$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SiemensNativeDWordPattern = new(@"^DB(?<db>\d+)\.(?<offset>\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SiemensNativeBitPattern = new(@"^DB(?<db>\d+)\.(?<byte>\d+)\.(?<bit>[0-7])$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SiemensAreaDWordPattern = new(@"^(?<area>[MIQ])D(?<offset>\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SiemensAreaBitPattern = new(@"^(?<area>[MIQ])(?<byte>\d+)\.(?<bit>[0-7])$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ModbusRegisterPattern = new(@"^HR(?<offset>\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ModbusInputRegisterPattern = new(@"^IR(?<offset>\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ModbusCoilPattern = new(@"^C(?<offset>\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ModbusDiscretePattern = new(@"^DI(?<offset>\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 解析 PLC 地址字符串，返回解析结果
    /// </summary>
    /// <param name="address">如 "D12001"、"M10001"</param>
    public static PlcAddressParseResult Parse(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return PlcAddressParseResult.Invalid(address ?? string.Empty, "地址不能为空");
        }

        var trimmed = address.Trim().ToUpperInvariant();

        var siemensAreaDWord = SiemensAreaDWordPattern.Match(trimmed);
        if (siemensAreaDWord.Success)
        {
            return PlcAddressParseResult.Valid(trimmed, PlcAddressType.DWord,
                int.Parse(siemensAreaDWord.Groups["offset"].Value), 4,
                siemensAreaDWord.Groups["area"].Value);
        }

        var siemensAreaBit = SiemensAreaBitPattern.Match(trimmed);
        if (siemensAreaBit.Success)
        {
            var offset = int.Parse(siemensAreaBit.Groups["byte"].Value) * 8
                + int.Parse(siemensAreaBit.Groups["bit"].Value);
            return PlcAddressParseResult.Valid(trimmed, PlcAddressType.MBit, offset, 1,
                siemensAreaBit.Groups["area"].Value);
        }

        var siemensNativeBit = SiemensNativeBitPattern.Match(trimmed);
        if (siemensNativeBit.Success)
        {
            var offset = int.Parse(siemensNativeBit.Groups["byte"].Value) * 8
                + int.Parse(siemensNativeBit.Groups["bit"].Value);
            return PlcAddressParseResult.Valid(trimmed, PlcAddressType.MBit, offset, 1,
                $"DB{siemensNativeBit.Groups["db"].Value}");
        }

        var siemensNativeDWord = SiemensNativeDWordPattern.Match(trimmed);
        if (siemensNativeDWord.Success)
        {
            return PlcAddressParseResult.Valid(trimmed, PlcAddressType.DWord,
                int.Parse(siemensNativeDWord.Groups["offset"].Value), 4,
                $"DB{siemensNativeDWord.Groups["db"].Value}");
        }

        if (DPattern.IsMatch(trimmed))
        {
            return PlcAddressParseResult.Valid(trimmed, PlcAddressType.DWord, int.Parse(trimmed[1..]), 2, "D");
        }

        if (MPattern.IsMatch(trimmed))
        {
            return PlcAddressParseResult.Valid(trimmed, PlcAddressType.MBit, int.Parse(trimmed[1..]), 1, "M");
        }

        var siemensDWord = SiemensDWordPattern.Match(trimmed);
        if (siemensDWord.Success)
        {
            return PlcAddressParseResult.Valid(trimmed, PlcAddressType.DWord,
                int.Parse(siemensDWord.Groups["offset"].Value), 4, $"DB{siemensDWord.Groups["db"].Value}");
        }

        var siemensBit = SiemensBitPattern.Match(trimmed);
        if (siemensBit.Success)
        {
            var bitOffset = int.Parse(siemensBit.Groups["byte"].Value) * 8 + int.Parse(siemensBit.Groups["bit"].Value);
            return PlcAddressParseResult.Valid(trimmed, PlcAddressType.MBit, bitOffset, 1, $"DB{siemensBit.Groups["db"].Value}");
        }

        var modbusRegister = ModbusRegisterPattern.Match(trimmed);
        if (modbusRegister.Success)
        {
            return PlcAddressParseResult.Valid(trimmed, PlcAddressType.DWord,
                int.Parse(modbusRegister.Groups["offset"].Value), 2, "HR");
        }

        var modbusInputRegister = ModbusInputRegisterPattern.Match(trimmed);
        if (modbusInputRegister.Success)
        {
            return PlcAddressParseResult.Valid(trimmed, PlcAddressType.DWord,
                int.Parse(modbusInputRegister.Groups["offset"].Value), 2, "IR");
        }

        var modbusCoil = ModbusCoilPattern.Match(trimmed);
        if (modbusCoil.Success)
        {
            return PlcAddressParseResult.Valid(trimmed, PlcAddressType.MBit,
                int.Parse(modbusCoil.Groups["offset"].Value), 1, "C");
        }

        var modbusDiscrete = ModbusDiscretePattern.Match(trimmed);
        if (modbusDiscrete.Success)
        {
            return PlcAddressParseResult.Valid(trimmed, PlcAddressType.MBit,
                int.Parse(modbusDiscrete.Groups["offset"].Value), 1, "DI");
        }

        return PlcAddressParseResult.Invalid(trimmed, $"无法识别的地址格式: {trimmed}");
    }

    /// <summary>
    /// 是否为合法的 D 字地址
    /// </summary>
    public static bool IsDWord(string? address) =>
        Parse(address) is { IsValid: true, Type: PlcAddressType.DWord };

    /// <summary>
    /// 是否为合法的 M 位地址
    /// </summary>
    public static bool IsMBit(string? address) =>
        Parse(address) is { IsValid: true, Type: PlcAddressType.MBit };

    /// <summary>返回可用于缓存和批量映射的规范地址；非法地址返回空字符串。</summary>
    public static string Normalize(string? address)
    {
        var result = Parse(address);
        return result.IsValid ? result.Original : string.Empty;
    }
}
