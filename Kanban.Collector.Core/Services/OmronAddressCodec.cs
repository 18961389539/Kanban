using System.Text.RegularExpressions;
using Kanban.Core.Models;

namespace Kanban.Core.Services;

/// <summary>
/// 欧姆龙 FINS 地址编解码器。
/// 支持 HslCommunication OmronFinsNet 文档中的字区和位区别名：
/// D/DM、C/CIO、W/WR、H/HR、A/AR、E/EM、TIM、CNT、IR、DR。
/// </summary>
internal sealed class OmronAddressCodec : IPlcAddressCodec
{
    private static readonly Regex WordPattern = new(
        "^(?<area>D|DM|C|CIO|W|WR|H|HR|A|AR|E|EM|TIM|CNT|IR|DR)(?<offset>\\d+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BitPattern = new(
        "^(?<area>D|DM|C|CIO|W|WR|H|HR|A|AR|E|EM|TIM|CNT|CF)(?<word>\\d+)\\.(?<bit>[0-9]|1[0-5])$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public PlcBrand Brand => PlcBrand.Omron;

    public PlcAddressParseResult Parse(string? address)
    {
        var original = address?.Trim().ToUpperInvariant() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(original))
            return PlcAddressParseResult.Invalid(original, "地址不能为空");

        var bit = BitPattern.Match(original);
        if (bit.Success)
        {
            var area = NormalizeArea(bit.Groups["area"].Value);
            if (!IsBitArea(area))
                return PlcAddressParseResult.Invalid(original, $"欧姆龙地址区域 {area} 不支持位读取");
            var word = int.Parse(bit.Groups["word"].Value);
            var bitIndex = int.Parse(bit.Groups["bit"].Value);
            return PlcAddressParseResult.Valid(original, PlcAddressType.MBit,
                word * 16 + bitIndex, 1, area);
        }

        var wordMatch = WordPattern.Match(original);
        if (wordMatch.Success)
        {
            var area = NormalizeArea(wordMatch.Groups["area"].Value);
            var offset = int.Parse(wordMatch.Groups["offset"].Value);
            return PlcAddressParseResult.Valid(original, PlcAddressType.DWord,
                offset, 2, area);
        }

        return PlcAddressParseResult.Invalid(original, $"不是有效的欧姆龙 FINS 地址: {address}");
    }

    public string Normalize(string? address)
    {
        var parsed = Parse(address);
        return parsed.IsValid ? parsed.Original : string.Empty;
    }

    public string ToTransportAddress(string address) => Normalize(address);

    public string Add(string address, int logicalOffset)
    {
        var parsed = Parse(address);
        if (!parsed.IsValid) throw new FormatException(parsed.ErrorMessage);
        if (parsed.Type == PlcAddressType.MBit)
        {
            var word = parsed.AddressOffset / 16;
            var bit = parsed.AddressOffset % 16 + logicalOffset;
            return $"{parsed.AddressGroup}{word + bit / 16}.{bit % 16}";
        }
        return $"{parsed.AddressGroup}{parsed.AddressOffset + logicalOffset * parsed.AddressStride}";
    }

    private static string NormalizeArea(string area) => area.ToUpperInvariant() switch
    {
        "DM" => "D",
        "CIO" => "C",
        "WR" => "W",
        "HR" => "H",
        "AR" => "A",
        "EM" => "E",
        _ => area.ToUpperInvariant(),
    };

    private static bool IsBitArea(string area) => area is "D" or "C" or "W" or "H" or "A" or "E" or "TIM" or "CNT" or "CF";
}
