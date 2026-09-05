using Kanban.Contracts.Enums;

namespace Kanban.Contracts.Metrics;

/// <summary>
/// 主页缺陷帕累托空状态。与列表是否有行对应：
/// HasData 才渲染 TOP 行；其余由卡片切换文案（未配置 / 全 0 / 无设备）。
/// </summary>
public enum DefectParetoEmptyKind
{
    HasData = 0,
    NoDevice = 1,
    NotConfigured = 2,
    AllZero = 3,
}

/// <summary>帕累托输入：一类缺陷的当前 PLC 计数与配置属性。</summary>
public readonly record struct DefectParetoInput(
    string Name,
    int Count,
    DefectSeverity Severity,
    DefectCategory Category,
    string PlcAddress);

/// <summary>帕累托一行（命名 TOP 或「其他」桶）。</summary>
public sealed class DefectParetoRow
{
    /// <summary>1-based 名次；「其他」为 0。</summary>
    public int Rank { get; init; }

    public required string Name { get; init; }

    public int Count { get; init; }

    /// <summary>占总缺陷件数的比例（0–1），条宽用此值而非相对第一名。</summary>
    public double ShareOfTotal { get; init; }

    /// <summary>从第一名累计到本行的占比（0–1）；末行钳为 1。</summary>
    public double CumulativeShare { get; init; }

    /// <summary>累计尚未越过 80% 阈值的命名项（含刚好跨过的那一项）。「其他」不加。</summary>
    public bool IsVitalFew { get; init; }

    public bool IsOthers { get; init; }

    /// <summary>「其他」桶包含的缺陷种类数；命名行为 0。</summary>
    public int OtherKindCount { get; init; }

    public DefectSeverity Severity { get; init; }

    public DefectCategory Category { get; init; }

    public string PlcAddress { get; init; } = "";
}

/// <summary>主页缺陷帕累托计算结果（WPF / WASM / Meta 共用）。</summary>
public sealed class DefectParetoResult
{
    public IReadOnlyList<DefectParetoRow> Rows { get; init; } = [];

    public int TotalCount { get; init; }

    public int ConfiguredCount { get; init; }

    public int PositiveKindCount { get; init; }

    public DefectParetoEmptyKind EmptyKind { get; init; }

    /// <summary>缺陷合计 / NG；NG≤0 时为 null（UI 不展示占 NG）。</summary>
    public double? ShareOfNg { get; init; }
}

/// <summary>
/// 主页缺陷帕累托的<strong>全局唯一实现</strong>。
/// 条宽相对缺陷合计（含「其他」），累计占比用于 80% 关键少数高亮。
/// 输入计数应为<strong>时间窗内新增</strong>（本班次差分，由历史快照差分提供）；
/// 本类不做差分、不读库。
/// </summary>
public static class DefectParetoMetrics
{
    public const int TopN = 8;

    public const double VitalFewThreshold = 0.80;

    public static DefectParetoResult Build(IReadOnlyList<DefectParetoInput> defects, int ngCount)
        => Build(defects, ngCount, hasDevice: true);

    /// <param name="hasDevice">false 时忽略缺陷列表，返回 <see cref="DefectParetoEmptyKind.NoDevice"/>。</param>
    public static DefectParetoResult Build(
        IReadOnlyList<DefectParetoInput>? defects,
        int ngCount,
        bool hasDevice)
    {
        if (!hasDevice)
            return Empty(DefectParetoEmptyKind.NoDevice, configured: 0);

        var list = defects ?? [];
        var configured = list.Count;
        if (configured == 0)
            return Empty(DefectParetoEmptyKind.NotConfigured, configured: 0);

        var positive = list
            .Where(d => d.Count > 0)
            .OrderByDescending(d => d.Count)
            .ThenBy(d => d.Name, StringComparer.Ordinal)
            .ToList();
        var total = 0;
        foreach (var d in positive)
            total += d.Count;

        if (total <= 0)
            return Empty(DefectParetoEmptyKind.AllZero, configured);

        var top = positive.Take(TopN).ToList();
        var rest = positive.Skip(TopN).ToList();
        var rows = new List<DefectParetoRow>(top.Count + (rest.Count > 0 ? 1 : 0));
        var cum = 0.0;
        var pastThreshold = false;
        var remainingAfterNamed = rest.Sum(x => x.Count);

        for (var i = 0; i < top.Count; i++)
        {
            var d = top[i];
            var isLastRow = rest.Count == 0 && i == top.Count - 1;
            var share = isLastRow ? Math.Max(0, 1.0 - cum) : (double)d.Count / total;
            cum = isLastRow ? 1.0 : cum + share;
            rows.Add(new DefectParetoRow
            {
                Rank = i + 1,
                Name = d.Name,
                Count = d.Count,
                ShareOfTotal = share,
                CumulativeShare = cum,
                IsVitalFew = !pastThreshold,
                IsOthers = false,
                Severity = d.Severity,
                Category = d.Category,
                PlcAddress = d.PlcAddress ?? "",
            });
            if (cum >= VitalFewThreshold)
                pastThreshold = true;
        }

        if (rest.Count > 0)
        {
            var share = Math.Max(0, 1.0 - cum);
            rows.Add(new DefectParetoRow
            {
                Rank = 0,
                Name = "",
                Count = remainingAfterNamed,
                ShareOfTotal = share,
                CumulativeShare = 1.0,
                IsVitalFew = false,
                IsOthers = true,
                OtherKindCount = rest.Count,
                Severity = DefectSeverity.Minor,
                Category = DefectCategory.Other,
                PlcAddress = "",
            });
        }

        return new DefectParetoResult
        {
            Rows = rows,
            TotalCount = total,
            ConfiguredCount = configured,
            PositiveKindCount = positive.Count,
            EmptyKind = DefectParetoEmptyKind.HasData,
            ShareOfNg = ngCount > 0 ? Math.Min(1.0, (double)total / ngCount) : null,
        };
    }

    private static DefectParetoResult Empty(DefectParetoEmptyKind kind, int configured)
        => new()
        {
            Rows = [],
            TotalCount = 0,
            ConfiguredCount = configured,
            PositiveKindCount = 0,
            EmptyKind = kind,
            ShareOfNg = null,
        };
}
