using System.Globalization;

namespace Kanban.Collector.Core.Localization;

/// <summary>启动时从外部 CSV 读取的一条已有资源覆盖值。</summary>
public readonly record struct LocalizationOverrideEntry(
    string Resource,
    string Key,
    string CultureName,
    string Value);

/// <summary>
/// 进程内本地化覆盖表。覆盖值只在启动阶段替换一次，读取侧无锁；
/// 未覆盖的资源继续使用编译进程序集的 RESX。
/// </summary>
public static class LocalizationOverrideStore
{
    private readonly record struct LookupKey(string Resource, string Key, string CultureName);

    private static IReadOnlyDictionary<LookupKey, string> s_values =
        new Dictionary<LookupKey, string>();

    /// <summary>原子替换当前进程的覆盖表。</summary>
    public static void Replace(IEnumerable<LocalizationOverrideEntry> entries)
    {
        var values = entries.ToDictionary(
            entry => new LookupKey(
                entry.Resource,
                entry.Key,
                NormalizeCulture(entry.CultureName)),
            entry => entry.Value);
        Volatile.Write(ref s_values, values);
    }

    private static string NormalizeCulture(string cultureName)
        => LocalizationCatalog.IsSupported(cultureName)
            ? LocalizationCatalog.Normalize(cultureName)
            : cultureName;

    /// <summary>清空覆盖表，供文件缺失/校验失败和测试恢复默认资源。</summary>
    public static void Clear()
        => Volatile.Write(ref s_values, new Dictionary<LookupKey, string>());

    /// <summary>返回当前覆盖表快照，供 Collector 只读下发给 Web 展示端。</summary>
    public static IReadOnlyList<LocalizationOverrideEntry> Snapshot()
        => Volatile.Read(ref s_values)
            .Select(pair => new LocalizationOverrideEntry(
                pair.Key.Resource,
                pair.Key.Key,
                pair.Key.CultureName,
                pair.Value))
            .ToList();

    /// <summary>按资源范围、Key 和文化查询覆盖值。</summary>
    public static bool TryGet(
        string resource,
        string key,
        CultureInfo culture,
        out string value)
    {
        var values = Volatile.Read(ref s_values);
        return values.TryGetValue(
            new LookupKey(resource, key, NormalizeCulture(culture.Name)),
            out value!);
    }
}
