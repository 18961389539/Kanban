using System.Text.RegularExpressions;
using Kanban.Collector.Core.Models;

namespace Kanban.Collector.Core.Services;

public interface IPlcAddressCodec
{
    PlcBrand Brand { get; }
    PlcAddressParseResult Parse(string? address);
    string Normalize(string? address);
    string Add(string address, int logicalOffset);
    string ToTransportAddress(string address);
    string CanonicalKey(string address) => ToTransportAddress(address);
    bool CanRead(PlcAddressParseResult address) => address.IsValid;
    bool CanWrite(PlcAddressParseResult address) => address.IsValid;
}

public interface IPlcAddressCodecResolver
{
    IPlcAddressCodec Resolve(PlcBrand brand);
    IPlcAddressCodec Current { get; }
}

public sealed class PlcAddressCodecResolver : IPlcAddressCodecResolver
{
    private readonly AppSettings _settings;
    private readonly IPlcBrandRegistry _brandRegistry;

    public PlcAddressCodecResolver(AppSettings settings, IPlcBrandRegistry? brandRegistry = null)
    {
        _settings = settings;
        _brandRegistry = brandRegistry ?? PlcBrandDescriptors.CreateDefault();
    }

    public IPlcAddressCodec Current => Resolve(_settings.PlcConfig.Brand);

    public IPlcAddressCodec Resolve(PlcBrand brand) =>
        _brandRegistry.Resolve(brand).CreateAddressCodec(_settings.PlcConfig);
}

public sealed class MitsubishiAddressCodec : IPlcAddressCodec
{
    public PlcBrand Brand => PlcBrand.Mitsubishi;
    public PlcAddressParseResult Parse(string? address)
    {
        var parsed = PlcAddressParser.Parse(address);
        return parsed.IsValid && (parsed.AddressGroup is "D" or "M")
            ? parsed
            : PlcAddressParseResult.Invalid(address ?? string.Empty, $"不是有效的三菱 D/M 地址: {address}");
    }
    public string Normalize(string? address)
    {
        var parsed = Parse(address);
        return parsed.IsValid ? parsed.Original : string.Empty;
    }
    public string ToTransportAddress(string address) => Normalize(address);
    public bool CanRead(PlcAddressParseResult address) => address.IsValid;
    public bool CanWrite(PlcAddressParseResult address) => address.IsValid;

    public string Add(string address, int logicalOffset)
    {
        var parsed = Parse(address);
        if (!parsed.IsValid) throw new FormatException(parsed.ErrorMessage);
        return $"{parsed.Original[0]}{parsed.AddressOffset + logicalOffset * parsed.AddressStride}";
    }
}

internal sealed class SiemensAddressCodec : IPlcAddressCodec
{
    private static readonly Regex DWordPattern = new(@"^DB(?<db>\d+)\.DBD(?<offset>\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BitPattern = new(@"^DB(?<db>\d+)\.DBX(?<byte>\d+)\.(?<bit>[0-7])$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NativeDWordPattern = new(@"^DB(?<db>\d+)\.(?<offset>\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NativeBitPattern = new(@"^DB(?<db>\d+)\.(?<byte>\d+)\.(?<bit>[0-7])$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AreaDWordPattern = new(@"^(?<area>[MIQ])D(?<offset>\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AreaBitPattern = new(@"^(?<area>[MIQ])(?<byte>\d+)\.(?<bit>[0-7])$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 解析是纯函数且不依赖实例状态（Pattern 全静态），静态缓存避免热路径重复正则。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, PlcAddressParseResult> ParseCache =
        new(System.StringComparer.Ordinal);

    public PlcBrand Brand => PlcBrand.Siemens;
    public PlcAddressParseResult Parse(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return PlcAddressParseResult.Invalid(address ?? string.Empty, "地址不能为空");
        return ParseCache.GetOrAdd(address, static raw => ParseCore(raw));
    }

    /// <summary>清空解析缓存（配置变更时由 <see cref="PlcAddressParser.ClearCache"/> 聚合调用）。</summary>
    internal static void ClearParseCache() => ParseCache.Clear();

    public string Normalize(string? address) => Parse(address).Original ?? string.Empty;
    public bool CanRead(PlcAddressParseResult address) => address.IsValid;
    public bool CanWrite(PlcAddressParseResult address) => address.IsValid
        && !string.Equals(address.AddressGroup, "I", StringComparison.OrdinalIgnoreCase);
    public string ToTransportAddress(string address)
    {
        var parsed = Parse(address);
        if (!parsed.IsValid) return string.Empty;
        var original = parsed.Original;
        var match = DWordPattern.Match(original);
        if (match.Success)
            return $"DB{match.Groups["db"].Value}.{match.Groups["offset"].Value}";
        match = BitPattern.Match(original);
        if (match.Success)
            return $"DB{match.Groups["db"].Value}.{match.Groups["byte"].Value}.{match.Groups["bit"].Value}";
        match = AreaDWordPattern.Match(original);
        if (match.Success)
            return $"{match.Groups["area"].Value.ToUpperInvariant()}{match.Groups["offset"].Value}";
        return original;
    }

    public string Add(string address, int logicalOffset)
    {
        var parsed = Parse(address);
        if (!parsed.IsValid) throw new FormatException(parsed.ErrorMessage);
        var match = DWordPattern.Match(parsed.Original);
        if (match.Success)
            return $"DB{match.Groups["db"].Value}.DBD{parsed.AddressOffset + logicalOffset * parsed.AddressStride}";

        match = AreaDWordPattern.Match(parsed.Original);
        if (match.Success)
            return $"{match.Groups["area"].Value.ToUpperInvariant()}D{parsed.AddressOffset + logicalOffset * parsed.AddressStride}";

        match = BitPattern.Match(parsed.Original);
        if (match.Success)
        {
            var bit = parsed.AddressOffset + logicalOffset;
            return $"DB{match.Groups["db"].Value}.DBX{bit / 8}.{bit % 8}";
        }

        match = NativeDWordPattern.Match(parsed.Original);
        if (match.Success)
            return $"DB{match.Groups["db"].Value}.{parsed.AddressOffset + logicalOffset * parsed.AddressStride}";

        match = NativeBitPattern.Match(parsed.Original);
        if (match.Success)
        {
            var bit = parsed.AddressOffset + logicalOffset;
            return $"DB{match.Groups["db"].Value}.{bit / 8}.{bit % 8}";
        }

        match = AreaBitPattern.Match(parsed.Original);
        if (match.Success)
        {
            var bit = parsed.AddressOffset + logicalOffset;
            return $"{match.Groups["area"].Value.ToUpperInvariant()}{bit / 8}.{bit % 8}";
        }
        throw new FormatException($"不支持的 Siemens 地址: {address}");
    }

    private static PlcAddressParseResult ParseCore(string address)
    {
        var original = address.Trim().ToUpperInvariant();
        if (!DWordPattern.IsMatch(original) && !BitPattern.IsMatch(original)
            && !NativeDWordPattern.IsMatch(original) && !NativeBitPattern.IsMatch(original)
            && !AreaDWordPattern.IsMatch(original) && !AreaBitPattern.IsMatch(original))
            return PlcAddressParseResult.Invalid(address ?? string.Empty, $"不是有效的 Siemens DB 地址: {address}");
        var match = DWordPattern.Match(original);
        if (match.Success)
            return PlcAddressParseResult.Valid(original, PlcAddressType.DWord,
                int.Parse(match.Groups["offset"].Value), 4, $"DB{match.Groups["db"].Value}");

        match = BitPattern.Match(original);
        if (match.Success)
        {
            var bitOffset = int.Parse(match.Groups["byte"].Value) * 8 + int.Parse(match.Groups["bit"].Value);
            return PlcAddressParseResult.Valid(original, PlcAddressType.MBit, bitOffset, 1,
                $"DB{match.Groups["db"].Value}");
        }

        match = NativeDWordPattern.Match(original);
        if (match.Success)
            return PlcAddressParseResult.Valid(original, PlcAddressType.DWord,
                int.Parse(match.Groups["offset"].Value), 4, $"DB{match.Groups["db"].Value}");

        match = NativeBitPattern.Match(original);
        if (match.Success)
        {
            var bitOffset = int.Parse(match.Groups["byte"].Value) * 8 + int.Parse(match.Groups["bit"].Value);
            return PlcAddressParseResult.Valid(original, PlcAddressType.MBit, bitOffset, 1,
                $"DB{match.Groups["db"].Value}");
        }

        if (AreaDWordPattern.IsMatch(original))
        {
            var areaMatch = AreaDWordPattern.Match(original);
            return PlcAddressParseResult.Valid(original, PlcAddressType.DWord,
                int.Parse(areaMatch.Groups["offset"].Value), 4, areaMatch.Groups["area"].Value.ToUpperInvariant());
        }
        if (AreaBitPattern.IsMatch(original))
        {
            var areaBitMatch = AreaBitPattern.Match(original);
            var offset = int.Parse(areaBitMatch.Groups["byte"].Value) * 8 + int.Parse(areaBitMatch.Groups["bit"].Value);
            return PlcAddressParseResult.Valid(original, PlcAddressType.MBit, offset, 1,
                areaBitMatch.Groups["area"].Value.ToUpperInvariant());
        }
        return PlcAddressParseResult.Invalid(original, $"不是有效的 Siemens DB 地址: {address}");
    }
}

internal sealed class ModbusTcpAddressCodec : IPlcAddressCodec
{
    private static readonly Regex RegisterPattern = new(@"^HR(?<offset>\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex InputRegisterPattern = new(@"^IR(?<offset>\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CoilPattern = new(@"^C(?<offset>\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DiscreteInputPattern = new(@"^DI(?<offset>\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly int _registerFunction;
    private readonly int _bitFunction;

    // 解析是纯函数（不依赖 _registerFunction/_bitFunction），静态缓存避免热路径重复正则。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, PlcAddressParseResult> ParseCache =
        new(System.StringComparer.Ordinal);

    public ModbusTcpAddressCodec(PlcConfig config)
    {
        _registerFunction = config.ModbusTcp.RegisterFunction;
        _bitFunction = config.ModbusTcp.BitFunction;
    }
    public PlcBrand Brand => PlcBrand.ModbusTcp;

    public PlcAddressParseResult Parse(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return PlcAddressParseResult.Invalid(address ?? string.Empty, "地址不能为空");
        return ParseCache.GetOrAdd(address, static raw => ParseCore(raw));
    }

    /// <summary>清空解析缓存（配置变更时由 <see cref="PlcAddressParser.ClearCache"/> 聚合调用）。</summary>
    internal static void ClearParseCache() => ParseCache.Clear();

    private static PlcAddressParseResult ParseCore(string address)
    {
        var original = address.Trim().ToUpperInvariant();
        var match = RegisterPattern.Match(original);
        if (match.Success)
            return PlcAddressParseResult.Valid(original, PlcAddressType.DWord,
                int.Parse(match.Groups["offset"].Value), 2, "HR");

        match = InputRegisterPattern.Match(original);
        if (match.Success)
            return PlcAddressParseResult.Valid(original, PlcAddressType.DWord,
                int.Parse(match.Groups["offset"].Value), 2, "IR");

        match = CoilPattern.Match(original);
        if (match.Success)
            return PlcAddressParseResult.Valid(original, PlcAddressType.MBit,
                int.Parse(match.Groups["offset"].Value), 1, "C");

        match = DiscreteInputPattern.Match(original);
        if (match.Success)
            return PlcAddressParseResult.Valid(original, PlcAddressType.MBit,
                int.Parse(match.Groups["offset"].Value), 1, "DI");

        return PlcAddressParseResult.Invalid(original, $"不是有效的 Modbus 地址（HR/IR/C/DI）: {address}");
    }

    public string Normalize(string? address) => Parse(address).Original ?? string.Empty;
    public bool CanRead(PlcAddressParseResult address) => address.IsValid;
    public bool CanWrite(PlcAddressParseResult address) => address.IsValid
        && !string.Equals(address.AddressGroup, "IR", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(address.AddressGroup, "DI", StringComparison.OrdinalIgnoreCase);

    public string ToTransportAddress(string address)
    {
        var parsed = Parse(address);
        if (!parsed.IsValid) return string.Empty;
        var isTwoCharacterPrefix = parsed.Original.StartsWith("HR", StringComparison.OrdinalIgnoreCase)
            || parsed.Original.StartsWith("IR", StringComparison.OrdinalIgnoreCase)
            || parsed.Original.StartsWith("DI", StringComparison.OrdinalIgnoreCase);
        var numeric = parsed.Original[isTwoCharacterPrefix ? 2.. : 1..];
        var prefix = parsed.AddressGroup switch
        {
            "IR" => "x=4;",
            "DI" => "x=2;",
            "HR" when _registerFunction == 4 => "x=4;",
            "C" when _bitFunction == 2 => "x=2;",
            _ => string.Empty,
        };
        return prefix + numeric;
    }

    public string Add(string address, int logicalOffset)
    {
        var parsed = Parse(address);
        if (!parsed.IsValid) throw new FormatException(parsed.ErrorMessage);
        var prefix = parsed.AddressGroup switch
        {
            "IR" => "IR",
            "DI" => "DI",
            "C" => "C",
            _ => "HR",
        };
        return $"{prefix}{parsed.AddressOffset + logicalOffset * parsed.AddressStride}";
    }
}