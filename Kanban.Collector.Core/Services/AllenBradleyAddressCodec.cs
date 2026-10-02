using System.Globalization;
using Kanban.Collector.Core.Models;

namespace Kanban.Collector.Core.Services;

/// <summary>
/// 罗克韦尔 CIP 标签地址编解码器。
/// 支持标签、Program 作用域、一维数组和 DINT 取位（Tag.3）。
/// 同一数组的连续下标共享区域，可供批量规划使用；标量标签各自成组，不会被并到一次请求里。
/// 采集侧目前关闭批量读，逐点按配置的数据类型读取。
/// </summary>
internal sealed class AllenBradleyAddressCodec : IPlcAddressCodec
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, PlcAddressParseResult> ParseCache =
        new(System.StringComparer.Ordinal);

    public PlcBrand Brand => PlcBrand.AllenBradley;

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
        if (original.Contains(' ') || original.Contains('\t'))
            return Invalid(original, address);

        var body = original;
        if (body.StartsWith("PROGRAM:", StringComparison.Ordinal))
        {
            body = body["PROGRAM:".Length..];
            var split = body.IndexOf('.');
            if (split <= 0 || split == body.Length - 1 || !IsIdentifier(body[..split]))
                return Invalid(original, address);
            body = body[(split + 1)..];
        }

        var bit = -1;
        var lastDot = body.LastIndexOf('.');
        if (lastDot > 0
            && lastDot < body.Length - 1
            && body[(lastDot + 1)..].All(char.IsDigit)
            && int.TryParse(body[(lastDot + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var bitIndex))
        {
            if (bitIndex is < 0 or > 31)
                return Invalid(original, address);
            bit = bitIndex;
            body = body[..lastDot];
        }

        if (!TrySplitArray(body, out var tagPath, out var arrayIndex, out var multiDimensional))
            return Invalid(original, address);
        if (!IsTagPath(tagPath))
            return Invalid(original, address);

        if (bit >= 0)
            return Parsed(original, PlcAddressType.MBit, bit, BitGroup(original));

        if (arrayIndex is int index && !multiDimensional)
        {
            var group = original[..original.LastIndexOf('[')];
            return Parsed(original, PlcAddressType.DWord, index, group);
        }

        return Parsed(original, PlcAddressType.DWord, 0, original);
    }

    /// <summary>
    /// 不走 <see cref="PlcAddressParseResult.Valid"/>：那个工厂会在偏移为 0 时从地址尾部猜测数字，
    /// 会把标签 A1 的偏移改成 1。
    /// </summary>
    private static PlcAddressParseResult Parsed(string original, PlcAddressType type, int offset, string group) => new()
    {
        IsValid = true,
        Type = type,
        Original = original,
        AddressOffset = offset,
        AddressStride = 1,
        AddressGroup = group,
        ErrorMessage = string.Empty,
    };

    public string Normalize(string? address)
    {
        var parsed = Parse(address);
        return parsed.IsValid ? parsed.Original : string.Empty;
    }

    public string ToTransportAddress(string address)
    {
        var normalized = Normalize(address);
        if (normalized.StartsWith("PROGRAM:", StringComparison.Ordinal))
            return "Program:" + normalized["PROGRAM:".Length..];
        return normalized;
    }

    public bool CanRead(PlcAddressParseResult address) => address.IsValid;

    public bool CanWrite(PlcAddressParseResult address) => address.IsValid;

    public string Add(string address, int logicalOffset)
    {
        var parsed = Parse(address);
        if (!parsed.IsValid) throw new FormatException(parsed.ErrorMessage);
        if (logicalOffset == 0) return parsed.Original;

        if (parsed.Type == PlcAddressType.MBit)
        {
            var dot = parsed.Original.LastIndexOf('.');
            var nextBit = parsed.AddressOffset + logicalOffset;
            if (dot <= 0 || nextBit is < 0 or > 31)
                throw new ArgumentOutOfRangeException(nameof(logicalOffset));
            return parsed.Original[..(dot + 1)] + nextBit.ToString(CultureInfo.InvariantCulture);
        }

        var open = parsed.Original.LastIndexOf('[');
        var close = parsed.Original.LastIndexOf(']');
        if (open > 0 && close > open && !parsed.Original[(open + 1)..close].Contains(','))
        {
            var next = parsed.AddressOffset + logicalOffset;
            if (next < 0) throw new ArgumentOutOfRangeException(nameof(logicalOffset));
            return string.Concat(
                parsed.Original.AsSpan(0, open + 1),
                next.ToString(CultureInfo.InvariantCulture),
                parsed.Original.AsSpan(close));
        }

        throw new FormatException($"罗克韦尔标量标签 {parsed.Original} 不能按偏移推进");
    }

    private static string BitGroup(string original)
    {
        var dot = original.LastIndexOf('.');
        return dot > 0 ? original[..dot] : original;
    }

    private static bool TrySplitArray(string body, out string tagPath, out int? arrayIndex, out bool multiDimensional)
    {
        tagPath = body;
        arrayIndex = null;
        multiDimensional = false;
        var open = body.IndexOf('[');
        if (open < 0)
            return !body.Contains(']');
        if (open == 0 || !body.EndsWith(']') || body.IndexOf('[', open + 1) >= 0)
            return false;
        tagPath = body[..open];
        var inside = body[(open + 1)..^1];
        if (inside.Length == 0)
            return false;
        if (inside.Contains(','))
        {
            multiDimensional = true;
            return inside.Split(',').All(part => int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= 0);
        }

        if (!int.TryParse(inside, NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index < 0)
            return false;
        arrayIndex = index;
        return true;
    }

    private static bool IsTagPath(string path)
    {
        if (path.Length == 0)
            return false;
        var parts = path.Split('.');
        return parts.Length > 0 && parts.All(IsIdentifier);
    }

    private static bool IsIdentifier(string value)
    {
        if (value.Length is < 1 or > 128)
            return false;
        if (!char.IsLetter(value[0]) && value[0] != '_')
            return false;
        for (var i = 1; i < value.Length; i++)
        {
            var ch = value[i];
            if (!char.IsLetterOrDigit(ch) && ch != '_')
                return false;
        }
        return true;
    }

    private static PlcAddressParseResult Invalid(string original, string address) =>
        PlcAddressParseResult.Invalid(original, $"不是有效的罗克韦尔标签地址: {address}");
}
