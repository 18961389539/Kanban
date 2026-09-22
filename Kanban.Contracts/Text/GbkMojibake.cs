namespace Kanban.Contracts.Text;

/// <summary>
/// 还原 UTF-8 中文被系统代码页误读后再按 UTF-8 落盘的常见乱码（生产看板 / 白班 / 夜班）。
/// </summary>
public static class GbkMojibake
{
    private static readonly (string From, string To)[] Known =
    [
        ("鐢熶骇鐪嬫澘", "生产看板"),
        ("鐧界彮", "白班"),
        ("澶滅彮", "夜班"),
    ];

    public static string Repair(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? "";
        var result = value;
        foreach (var (from, to) in Known)
            result = result.Replace(from, to, StringComparison.Ordinal);
        return result;
    }
}
