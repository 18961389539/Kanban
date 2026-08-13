using System.Globalization;
using System.Text.RegularExpressions;
using Kanban.Core.Models;

namespace Kanban.Core.Services;

/// <summary>
/// Keyence MC-3E 地址编解码器。
/// 支持 Keyence 原生字区 DM/EM/FM/ZF/CM/TN/CN/W、位区 MR/LR/CR/TS/CS/R/B，
/// 并兼容三菱 D/M 地址。W、B 和 ZR 使用十六进制，其余区域使用十进制。
/// </summary>
internal sealed class KeyenceAddressCodec : IPlcAddressCodec
{
    private static readonly Regex AddressPattern = new(
        @"^(?<area>DM|EM|FM|ZF|CM|TN|CN|MR|LR|CR|TS|CS|ZR|SM|SD|D|M|R|B|L|W|X|Y)(?<offset>[0-9A-F]+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> WordAreas = new(StringComparer.OrdinalIgnoreCase)
    {
        "DM", "EM", "FM", "ZF", "CM", "TN", "CN", "D", "SD", "ZR", "W",
    };

    private static readonly HashSet<string> HexAreas = new(StringComparer.OrdinalIgnoreCase)
    {
        "B", "W", "ZR", "X", "Y",
    };

    public PlcBrand Brand => PlcBrand.Keyence;

    public PlcAddressParseResult Parse(string? address)
    {
        var original = address?.Trim().ToUpperInvariant() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(original))
            return PlcAddressParseResult.Invalid(original, "地址不能为空");

        var match = AddressPattern.Match(original);
        if (!match.Success)
            return PlcAddressParseResult.Invalid(original,
                $"不是有效的 Keyence MC 地址（如 DM100/MR100/D100/M100）: {address}");

        var area = match.Groups["area"].Value.ToUpperInvariant();
        var numericText = match.Groups["offset"].Value;
        var radix = HexAreas.Contains(area) ? 16 : 10;
        if (!TryParseOffset(numericText, radix, out var offset))
            return PlcAddressParseResult.Invalid(original, $"Keyence 地址超出有效范围: {address}");

        var type = WordAreas.Contains(area) ? PlcAddressType.DWord : PlcAddressType.MBit;
        var stride = type == PlcAddressType.DWord ? 2 : 1;
        return PlcAddressParseResult.Valid(original, type, offset, stride, area);
    }

    public string Normalize(string? address)
    {
        var parsed = Parse(address);
        return parsed.IsValid ? parsed.Original : string.Empty;
    }

    public string ToTransportAddress(string address) => Normalize(address);

    public bool CanRead(PlcAddressParseResult address) => address.IsValid;

    public bool CanWrite(PlcAddressParseResult address) => address.IsValid
        && !string.Equals(address.AddressGroup, "X", StringComparison.OrdinalIgnoreCase);

    public string Add(string address, int logicalOffset)
    {
        var parsed = Parse(address);
        if (!parsed.IsValid) throw new FormatException(parsed.ErrorMessage);

        var nextOffset = checked(parsed.AddressOffset + logicalOffset * parsed.AddressStride);
        if (nextOffset < 0) throw new ArgumentOutOfRangeException(nameof(logicalOffset));

        var numeric = HexAreas.Contains(parsed.AddressGroup)
            ? nextOffset.ToString("X", CultureInfo.InvariantCulture)
            : nextOffset.ToString(CultureInfo.InvariantCulture);
        return parsed.AddressGroup + numeric;
    }

    private static bool TryParseOffset(string value, int radix, out int offset)
    {
        try
        {
            offset = Convert.ToInt32(value, radix);
            return offset >= 0;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            offset = 0;
            return false;
        }
    }
}
