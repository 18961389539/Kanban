using System.Globalization;
using Kanban.Collector.Core.Models;

namespace Kanban.Collector.Core.Services;

/// <summary>
/// 汇川地址编解码器。接受 HslCommunication <c>InovanceTcpNet</c> 能翻译的软元件地址，
/// 字区按字地址步进（Int32 步长 2，MD 除外），位区按位步进。X/Y 在 H3U、H5U 上是八进制。
/// 带小数点的 AM 位地址（如 MW0.0、QX7.2）各自独立，不参与跨地址批量合并。
/// </summary>
internal sealed class InovanceAddressCodec : IPlcAddressCodec
{
    private readonly record struct AreaRule(
        string Name,
        PlcAddressType Type,
        int Stride,
        bool Octal,
        bool ReadOnly,
        bool AllowDot);

    private static readonly AreaRule[] H5URules = Order(
    [
        new("X", PlcAddressType.MBit, 1, true, true, false),
        new("Y", PlcAddressType.MBit, 1, true, false, false),
        new("M", PlcAddressType.MBit, 1, false, false, false),
        new("B", PlcAddressType.MBit, 1, false, false, false),
        new("S", PlcAddressType.MBit, 1, false, false, false),
        new("D", PlcAddressType.DWord, 2, false, false, false),
        new("R", PlcAddressType.DWord, 2, false, false, false),
    ]);

    private static readonly AreaRule[] H3URules = Order(
    [
        new("SM", PlcAddressType.MBit, 1, false, false, false),
        new("SD", PlcAddressType.DWord, 2, false, false, false),
        new("X", PlcAddressType.MBit, 1, true, true, false),
        new("Y", PlcAddressType.MBit, 1, true, false, false),
        new("M", PlcAddressType.MBit, 1, false, false, false),
        new("S", PlcAddressType.MBit, 1, false, false, false),
        new("T", PlcAddressType.DWord, 2, false, false, false),
        new("C", PlcAddressType.DWord, 2, false, false, false),
        new("D", PlcAddressType.DWord, 2, false, false, false),
        new("R", PlcAddressType.DWord, 2, false, false, false),
    ]);

    private static readonly AreaRule[] AMRules = Order(
    [
        new("MW", PlcAddressType.DWord, 2, false, false, true),
        new("MD", PlcAddressType.DWord, 1, false, false, false),
        new("MX", PlcAddressType.MBit, 1, false, false, true),
        new("MB", PlcAddressType.MBit, 1, false, false, false),
        new("QX", PlcAddressType.MBit, 1, false, false, true),
        new("IX", PlcAddressType.MBit, 1, false, true, true),
        new("SD", PlcAddressType.DWord, 2, false, false, false),
        new("SM", PlcAddressType.MBit, 1, false, false, false),
        new("Q", PlcAddressType.DWord, 2, false, false, true),
        new("I", PlcAddressType.DWord, 2, false, true, false),
        new("M", PlcAddressType.DWord, 2, false, false, false),
    ]);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, PlcAddressParseResult> ParseCache =
        new(System.StringComparer.Ordinal);

    private readonly InovancePlcSeries _series;

    public InovanceAddressCodec(PlcConfig config)
    {
        _series = Enum.IsDefined(config.Inovance.Series) ? config.Inovance.Series : InovancePlcSeries.H5U;
    }

    public PlcBrand Brand => PlcBrand.Inovance;

    public PlcAddressParseResult Parse(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return PlcAddressParseResult.Invalid(address ?? string.Empty, "地址不能为空");
        var key = $"{_series}|{address}";
        return ParseCache.GetOrAdd(key, _ => ParseCore(address, _series));
    }

    /// <summary>清空解析缓存（配置变更时由 <see cref="PlcAddressParser.ClearCache"/> 聚合调用）。</summary>
    internal static void ClearParseCache() => ParseCache.Clear();

    private static PlcAddressParseResult ParseCore(string address, InovancePlcSeries series)
    {
        var original = address.Trim().ToUpperInvariant();
        foreach (var rule in RulesFor(series))
        {
            if (!original.StartsWith(rule.Name, StringComparison.Ordinal))
                continue;
            var rest = original[rule.Name.Length..];
            if (!TryParseRest(rest, rule, out var word, out var dotted))
                return PlcAddressParseResult.Invalid(original, $"不是有效的汇川 {series} 地址: {address}");

            if (dotted)
            {
                // 点号位地址的换算随系列变化，批量规划无法安全合并，每个地址单独成组。
                return PlcAddressParseResult.Valid(original, PlcAddressType.MBit, 0, 1, original);
            }

            return PlcAddressParseResult.Valid(original, rule.Type, word, rule.Stride, rule.Name);
        }

        return PlcAddressParseResult.Invalid(original, $"不是有效的汇川 {series} 地址: {address}");
    }

    public string Normalize(string? address)
    {
        var parsed = Parse(address);
        return parsed.IsValid ? parsed.Original : string.Empty;
    }

    public string ToTransportAddress(string address) => Normalize(address);

    public bool CanRead(PlcAddressParseResult address) => address.IsValid;

    public bool CanWrite(PlcAddressParseResult address) => address.IsValid && !IsReadOnly(address);

    public string Add(string address, int logicalOffset)
    {
        var parsed = Parse(address);
        if (!parsed.IsValid) throw new FormatException(parsed.ErrorMessage);
        if (logicalOffset == 0) return parsed.Original;
        if (string.Equals(parsed.AddressGroup, parsed.Original, StringComparison.Ordinal))
            throw new FormatException($"汇川位地址 {parsed.Original} 不能按偏移推进");

        var next = checked(parsed.AddressOffset + logicalOffset * parsed.AddressStride);
        if (next < 0) throw new ArgumentOutOfRangeException(nameof(logicalOffset));
        var numeric = parsed.AddressGroup is "X" or "Y"
            ? Convert.ToString(next, 8)
            : next.ToString(CultureInfo.InvariantCulture);
        return parsed.AddressGroup + numeric;
    }

    private static bool IsReadOnly(PlcAddressParseResult address)
    {
        if (address.AddressGroup is "X" or "IX" or "I")
            return true;
        return address.AddressGroup.StartsWith("IX", StringComparison.Ordinal);
    }

    private static bool TryParseRest(string rest, AreaRule rule, out int word, out bool dotted)
    {
        word = 0;
        dotted = false;
        if (rest.Length == 0)
            return false;

        var body = rest;
        var dot = rest.IndexOf('.');
        if (dot >= 0)
        {
            if (!rule.AllowDot || dot == 0 || dot == rest.Length - 1)
                return false;
            body = rest[..dot];
            var bitText = rest[(dot + 1)..];
            if (bitText.Length == 0 || bitText.Any(ch => !char.IsDigit(ch)) || !int.TryParse(bitText, NumberStyles.None, CultureInfo.InvariantCulture, out var bit) || bit is < 0 or > 15)
                return false;
            dotted = true;
        }

        if (body.Length == 0 || body.Any(ch => !char.IsDigit(ch)))
            return false;
        if (rule.Octal)
        {
            if (body.Any(ch => ch is < '0' or > '7'))
                return false;
            word = Convert.ToInt32(body, 8);
            return true;
        }

        return int.TryParse(body, NumberStyles.None, CultureInfo.InvariantCulture, out word) && word >= 0;
    }

    private static AreaRule[] RulesFor(InovancePlcSeries series) => series switch
    {
        InovancePlcSeries.AM => AMRules,
        InovancePlcSeries.H3U => H3URules,
        _ => H5URules,
    };

    private static AreaRule[] Order(AreaRule[] rules) =>
        rules.OrderByDescending(rule => rule.Name.Length).ToArray();
}
