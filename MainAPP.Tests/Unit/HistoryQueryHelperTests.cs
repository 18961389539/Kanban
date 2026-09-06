using System.Linq;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Models;
using MainAPP.Models;
using MainAPP.ViewModels;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// HistoryQueryHelper 单元测试：覆盖历史查询页共用的纯函数逻辑——
/// 班次实例切分、窗口产量差分、基线查找、状态/事件文本映射、CSV 序列化、统一区间过滤。
/// 该类为 internal static，MainAPP 已 InternalsVisibleTo("MainAPP.Tests")，可直接测。
/// </summary>
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class HistoryQueryHelperTests
{
    private static ProductionLog Log(string deviceId, string shift, int ok, int ng, System.DateTime ts)
        => new()
        {
            DeviceId = deviceId,
            DeviceName = "设备",
            ShiftName = shift,
            OkProduction = ok,
            NgProduction = ng,
            StatusWord = 1,
            Timestamp = ts,
        };

    // ──────────── SplitShiftInstances ────────────

    [Fact]
    public void SplitShiftInstances_SameShiftContinuousCumulative_StaysOneGroup()
    {
        var logs = new List<ProductionLog>
        {
            Log("d1", "白班", 10, 0, System.DateTime.Today.AddHours(1)),
            Log("d1", "白班", 20, 1, System.DateTime.Today.AddHours(2)),
            Log("d1", "白班", 30, 2, System.DateTime.Today.AddHours(3)),
        };

        var groups = HistoryQueryHelper.SplitShiftInstances(logs);

        Assert.Single(groups);
        Assert.Equal(3, groups[0].Count);
    }

    [Fact]
    public void SplitShiftInstances_CumulativeDecreases_SplitsGroup()
    {
        var logs = new List<ProductionLog>
        {
            Log("d1", "白班", 30, 0, System.DateTime.Today.AddHours(1)),
            Log("d1", "白班", 10, 1, System.DateTime.Today.AddHours(2)), // 累计回落 → 新班次实例
        };

        var groups = HistoryQueryHelper.SplitShiftInstances(logs);

        Assert.Equal(2, groups.Count);
        Assert.Single(groups[0]);
        Assert.Single(groups[1]);
    }

    [Fact]
    public void SplitShiftInstances_ShiftNameChanges_SplitsGroup()
    {
        var logs = new List<ProductionLog>
        {
            Log("d1", "白班", 10, 0, System.DateTime.Today.AddHours(1)),
            Log("d1", "夜班", 5, 0, System.DateTime.Today.AddHours(2)),
        };

        var groups = HistoryQueryHelper.SplitShiftInstances(logs);

        Assert.Equal(2, groups.Count);
    }

    // ──────────── SumWindowProduction ────────────

    [Fact]
    public void SumWindowProduction_EmptyWindow_ReturnsZero()
    {
        var r = HistoryQueryHelper.SumWindowProduction(new List<ProductionLog>(), new List<ProductionLog>(), System.DateTime.Today);
        Assert.Equal((0, 0), r);
    }

    [Fact]
    public void SumWindowProduction_BaselineAtWindowStart_WindowDiff()
    {
        // 窗口起点恰有快照：累计 100，窗口末条 130 → OK=30
        var window = new List<ProductionLog>
        {
            Log("d1", "白班", 100, 0, System.DateTime.Today.AddHours(2)),
            Log("d1", "白班", 130, 5, System.DateTime.Today.AddHours(3)),
        };
        var baseline = new List<ProductionLog>(); // 无需前置基线

        var (ok, ng) = HistoryQueryHelper.SumWindowProduction(window, baseline, System.DateTime.Today.AddHours(2));

        Assert.Equal(30, ok);
        Assert.Equal(5, ng);
    }

    [Fact]
    public void SumWindowProduction_NoBaseline_FallsBackToFirstInWindow()
    {
        // 窗口起点无快照：回退到窗口内首条累计值作基准 → 仅统计窗口可见部分
        var window = new List<ProductionLog>
        {
            Log("d1", "白班", 100, 0, System.DateTime.Today.AddHours(3)), // 首条=基准
            Log("d1", "白班", 130, 5, System.DateTime.Today.AddHours(4)),
        };
        var baseline = new List<ProductionLog>(); // 窗口前无同班次实例快照

        var (ok, ng) = HistoryQueryHelper.SumWindowProduction(window, baseline, System.DateTime.Today.AddHours(2));

        Assert.Equal(30, ok); // 130 - 100
        Assert.Equal(5, ng);
    }

    [Fact]
    public void SumWindowProduction_BaselineBeforeWindow_UsedAsBase()
    {
        // 窗口前同班次实例快照累计 80，窗口内末条 130 → OK=50
        var window = new List<ProductionLog>
        {
            Log("d1", "白班", 130, 5, System.DateTime.Today.AddHours(4)), // 窗口内首条晚于 windowFrom
        };
        var baseline = new List<ProductionLog>
        {
            Log("d1", "白班", 80, 0, System.DateTime.Today.AddHours(1)), // 窗口前同实例
        };
        var windowFrom = System.DateTime.Today.AddHours(3);

        var (ok, ng) = HistoryQueryHelper.SumWindowProduction(window, baseline, windowFrom);

        Assert.Equal(50, ok);
        Assert.Equal(5, ng);
    }

    [Fact]
    public void SumWindowProduction_DataGap_NotPollutedByOlderShiftInstance()
    {
        // 回归（2026-08-26 P1）：7 天等长窗口 + 数据缺口下，旧实现 FindBaselineBeforeWindow
        // 会拿"更早班次实例"的累计值（171）当基线，Math.Max(0, 130-171)=0 把产量砍没。
        // 修复后改为全量逐条差分：累计回落 171→100 即切组，窗口内实例自差 = 130-100。
        var window = new List<ProductionLog>
        {
            Log("d1", "白班", 100, 0, System.DateTime.Today.AddHours(3)), // 新实例起点（较 5 天前回落 → 切组）
            Log("d1", "白班", 130, 5, System.DateTime.Today.AddHours(4)),
        };
        var baseline = new List<ProductionLog>
        {
            Log("d1", "白班", 171, 2, System.DateTime.Today.AddDays(-5).AddHours(12)), // 更早班次实例，非同一实例
        };

        var (ok, ng) = HistoryQueryHelper.SumWindowProduction(window, baseline, System.DateTime.Today.AddHours(2));

        Assert.Equal(30, ok); // 不被 171 污染
        Assert.Equal(5, ng);
    }

    [Fact]
    public void SumWindowProduction_CrossWindowInstance_UsesBeforePartAsBase()
    {
        // 窗口起点无快照（首条 > from）、实例从窗口前延续：基线 = 窗口前同实例末条（90），
        // 窗口内产量 = 150 − 90 = 60（跨窗口边界的增量由窗口前日志承接，但不算窗口外部分）。
        var window = new List<ProductionLog>
        {
            Log("d1", "白班", 120, 2, System.DateTime.Today.AddHours(10.5)),
            Log("d1", "白班", 150, 3, System.DateTime.Today.AddHours(11)),
        };
        var baseline = new List<ProductionLog>
        {
            Log("d1", "白班", 90, 1, System.DateTime.Today.AddHours(9.5)),
        };

        var (ok, ng) = HistoryQueryHelper.SumWindowProduction(window, baseline, System.DateTime.Today.AddHours(10));

        Assert.Equal(60, ok);
        Assert.Equal(2, ng);
    }

    // ──────────── FindBaselineBeforeWindow ────────────

    [Fact]
    public void FindBaselineBeforeWindow_SameShiftInstance_ReturnsRecord()
    {
        var candidates = new List<ProductionLog>
        {
            Log("d1", "夜班", 0, 0, System.DateTime.Today.AddHours(0)),
            Log("d1", "白班", 80, 0, System.DateTime.Today.AddHours(1)), // 目标班次实例
        };

        var rec = HistoryQueryHelper.FindBaselineBeforeWindow(candidates, "白班");

        Assert.NotNull(rec);
        Assert.Equal(80, rec!.OkProduction);
    }

    [Fact]
    public void FindBaselineBeforeWindow_DifferentShiftSeenBefore_ReturnsNull()
    {
        // 倒序遍历中先遇到"其它班次名"再遇到目标班次 → 说明目标班次是更早实例，返回 null
        var candidates = new List<ProductionLog>
        {
            Log("d1", "白班", 80, 0, System.DateTime.Today.AddHours(1)), // 更早的同名班次实例
            Log("d1", "夜班", 0, 0, System.DateTime.Today.AddHours(2)),
        };

        var rec = HistoryQueryHelper.FindBaselineBeforeWindow(candidates, "白班");

        Assert.Null(rec);
    }

    [Fact]
    public void FindBaselineBeforeWindow_NoMatchingShift_ReturnsNull()
    {
        var candidates = new List<ProductionLog>
        {
            Log("d1", "夜班", 0, 0, System.DateTime.Today.AddHours(1)),
        };

        var rec = HistoryQueryHelper.FindBaselineBeforeWindow(candidates, "白班");
        Assert.Null(rec);
    }

    // ──────────── 文本映射 ────────────

    [Theory]
    [InlineData(0, "离线")]
    [InlineData(1, "运行")]
    [InlineData(2, "报警")]
    [InlineData(3, "待机")]
    [InlineData(99, "未知")]
    public void GetStateText_MapsAllStates(int state, string expected)
        => Assert.Equal(expected, HistoryQueryHelper.GetStateText(state));

    [Theory]
    [InlineData(0, 1, "离线（PLC）")]
    [InlineData(0, 2, "离线（通讯中断）")]
    [InlineData(0, 3, "离线（采集停止）")]
    [InlineData(0, 4, "离线（采集空窗）")]
    [InlineData(1, 2, "运行")]
    public void GetStateText_MapsOfflineCause(int state, int cause, string expected)
        => Assert.Equal(expected, HistoryQueryHelper.GetStateText(state, cause));

    [Theory]
    [InlineData(AlarmEventType.Triggered, "触发")]
    [InlineData(AlarmEventType.Recovered, "恢复")]
    [InlineData(AlarmEventType.ShiftChange, "班次切换")]
    public void GetEventTypeText_MapsKnownTypes(AlarmEventType type, string expected)
        => Assert.Equal(expected, HistoryQueryHelper.GetEventTypeText(type));

    [Fact]
    public void GetEventTypeText_Unknown_ReturnsUnknown()
        => Assert.Equal("未知", HistoryQueryHelper.GetEventTypeText((AlarmEventType)99));

    // ──────────── BuildCsv ────────────

    [Fact]
    public void BuildCsv_SerializesRowsAndAppendsFooter()
    {
        var rows = new[] { new { Name = "A", Count = 1 }, new { Name = "B", Count = 2 } };

        var csv = HistoryQueryHelper.BuildCsv(rows, "备注行1", "备注行2");

        Assert.Contains("Name", csv);
        Assert.Contains("A", csv);
        Assert.Contains("B", csv);
        Assert.Contains("备注行1", csv);
        Assert.Contains("备注行2", csv);
    }

    // ──────────── ApplyRangeFilter ────────────

    [Fact]
    public void ApplyRangeFilter_TimeDeviceShift_FiltersCorrectly()
    {
        var from = System.DateTime.Today.AddHours(1);
        var to = System.DateTime.Today.AddHours(5);
        var logs = new List<ProductionLog>
        {
            Log("d1", "白班", 10, 0, from.AddMinutes(30)),
            Log("d2", "白班", 20, 0, from.AddMinutes(40)),          // 其它设备
            Log("d1", "夜班", 30, 0, from.AddMinutes(50)),          // 其它班次
            Log("d1", "白班", 40, 0, to.AddHours(2)),              // 超出时间
        }.AsQueryable();

        var filtered = HistoryQueryHelper.ApplyRangeFilter(
            logs, from, to, "d1", "白班",
            nameof(ProductionLog.Timestamp), nameof(ProductionLog.DeviceId), nameof(ProductionLog.ShiftName));

        var list = filtered.ToList();
        Assert.Single(list);
        Assert.Equal("d1", list[0].DeviceId);
    }

    [Fact]
    public void ApplyRangeFilter_NoDeviceNoShift_OnlyTimeBound()
    {
        var from = System.DateTime.Today.AddHours(1);
        var to = System.DateTime.Today.AddHours(5);
        var logs = new List<ProductionLog>
        {
            Log("d1", "白班", 10, 0, from.AddMinutes(30)),
            Log("d2", "夜班", 20, 0, from.AddMinutes(40)),
            Log("d1", "白班", 40, 0, to.AddHours(2)),
        }.AsQueryable();

        var filtered = HistoryQueryHelper.ApplyRangeFilter(
            logs, from, to, null, null,
            nameof(ProductionLog.Timestamp), nameof(ProductionLog.DeviceId), nameof(ProductionLog.ShiftName));

        Assert.Equal(2, filtered.Count());
    }

    // ──────────── 日期边界：start > end / start == end / 跨午夜 / 跨年 ────────────
    // HistoryService.QueryProductionLogs 闭区间 [from, to]，无自动 swap / 跨午夜兜底。
    // 这些测试覆盖边界场景，避免 HistoryService 查询时返回意外结果。

    /// <summary>
    /// start > end 时应静默返回空（不抛异常、不自动 swap 顺序）。
    /// 生产场景：用户在 UI 误填 from > to，或班次切换时边界计算错误。
    /// </summary>
    [Fact]
    public void ApplyRangeFilter_StartGreaterThanEnd_ReturnsEmpty()
    {
        var from = System.DateTime.Today.AddHours(10); // 10:00
        var to = System.DateTime.Today.AddHours(5);    // 05:00（早于 from）
        var logs = new List<ProductionLog>
        {
            Log("d1", "白班", 10, 0, System.DateTime.Today.AddHours(7)), // 07:00 在两者之间
            Log("d2", "白班", 20, 0, System.DateTime.Today.AddHours(3)), // 03:00 早于两者
        }.AsQueryable();

        var filtered = HistoryQueryHelper.ApplyRangeFilter(
            logs, from, to, null, null,
            nameof(ProductionLog.Timestamp), nameof(ProductionLog.DeviceId), nameof(ProductionLog.ShiftName));

        Assert.Empty(filtered);
    }

    /// <summary>
    /// start == end 时仅匹配 Timestamp 恰好等于该时刻的记录（闭区间端点）。
    /// </summary>
    [Fact]
    public void ApplyRangeFilter_StartEqualsEnd_MatchesOnlyExactTimestamp()
    {
        var exact = System.DateTime.Today.AddHours(8);
        var logs = new List<ProductionLog>
        {
            Log("d1", "白班", 10, 0, exact),                          // 精确匹配
            Log("d2", "白班", 20, 0, exact.AddSeconds(1)),            // 1 秒后（不含）
            Log("d3", "白班", 30, 0, exact.AddSeconds(-1)),           // 1 秒前（不含）
        }.AsQueryable();

        var filtered = HistoryQueryHelper.ApplyRangeFilter(
            logs, exact, exact, null, null,
            nameof(ProductionLog.Timestamp), nameof(ProductionLog.DeviceId), nameof(ProductionLog.ShiftName));

        var list = filtered.ToList();
        Assert.Single(list);
        Assert.Equal(exact, list[0].Timestamp);
    }

    /// <summary>
    /// 跨午夜查询（今日 22:00 ~ 明日 02:00）应正常返回跨日记录。
    /// 这是夜班查询的典型场景，from < to 关系成立，闭区间过滤生效。
    /// </summary>
    [Fact]
    public void ApplyRangeFilter_CrossMidnight_ReturnsRecordsAcrossDayBoundary()
    {
        var today = System.DateTime.Today;
        var from = today.AddHours(22);   // 今日 22:00
        var to = today.AddDays(1).AddHours(2); // 明日 02:00
        var logs = new List<ProductionLog>
        {
            Log("d1", "夜班", 10, 0, today.AddHours(21)),        // 今日 21:00（不含）
            Log("d1", "夜班", 20, 0, today.AddHours(22)),        // 今日 22:00（端点含）
            Log("d1", "夜班", 30, 0, today.AddHours(23).AddMinutes(30)),    // 今日 23:30
            Log("d1", "夜班", 40, 0, today.AddDays(1)),          // 明日 00:00
            Log("d1", "夜班", 50, 0, today.AddDays(1).AddHours(2)), // 明日 02:00（端点含）
            Log("d1", "夜班", 60, 0, today.AddDays(1).AddHours(3)), // 明日 03:00（不含）
        }.AsQueryable();

        var filtered = HistoryQueryHelper.ApplyRangeFilter(
            logs, from, to, null, null,
            nameof(ProductionLog.Timestamp), nameof(ProductionLog.DeviceId), nameof(ProductionLog.ShiftName));

        Assert.Equal(4, filtered.Count());
    }

    /// <summary>
    /// 跨年/跨月查询（2025-12-30 ~ 2026-01-02）应正常返回跨年记录。
    /// 验证 DateTime 比较不因年份变更而出错。
    /// </summary>
    [Fact]
    public void ApplyRangeFilter_CrossYearBoundary_ReturnsRecordsAcrossYear()
    {
        var from = new System.DateTime(2025, 12, 30, 0, 0, 0);
        var to = new System.DateTime(2026, 1, 2, 0, 0, 0);
        var logs = new List<ProductionLog>
        {
            Log("d1", "白班", 10, 0, new System.DateTime(2025, 12, 29, 12, 0, 0)), // 12-29（不含）
            Log("d1", "白班", 20, 0, new System.DateTime(2025, 12, 31, 23, 59, 59)), // 12-31 23:59:59
            Log("d1", "白班", 30, 0, new System.DateTime(2026, 1, 1, 0, 0, 0)),     // 01-01 00:00
            Log("d1", "白班", 40, 0, new System.DateTime(2026, 1, 1, 12, 0, 0)),    // 01-01 12:00
            Log("d1", "白班", 50, 0, new System.DateTime(2026, 1, 3, 0, 0, 0)),     // 01-03（不含）
        }.AsQueryable();

        var filtered = HistoryQueryHelper.ApplyRangeFilter(
            logs, from, to, null, null,
            nameof(ProductionLog.Timestamp), nameof(ProductionLog.DeviceId), nameof(ProductionLog.ShiftName));

        Assert.Equal(3, filtered.Count());
    }

    /// <summary>
    /// 闭区间端点包含验证：from 和 to 时刻的记录都应返回。
    /// </summary>
    [Fact]
    public void ApplyRangeFilter_ClosedInterval_IncludesBothEndpoints()
    {
        var from = System.DateTime.Today.AddHours(8);
        var to = System.DateTime.Today.AddHours(20);
        var logs = new List<ProductionLog>
        {
            Log("d1", "白班", 10, 0, from),                 // from 端点（含）
            Log("d2", "白班", 20, 0, from.AddSeconds(1)),    // from 后 1 秒
            Log("d3", "白班", 30, 0, to.AddSeconds(-1)),     // to 前 1 秒
            Log("d4", "白班", 40, 0, to),                    // to 端点（含）
        }.AsQueryable();

        var filtered = HistoryQueryHelper.ApplyRangeFilter(
            logs, from, to, null, null,
            nameof(ProductionLog.Timestamp), nameof(ProductionLog.DeviceId), nameof(ProductionLog.ShiftName));

        Assert.Equal(4, filtered.Count());
    }

    /// <summary>
    /// CSV 单元格公式注入防护：危险前缀改写、普通文本原样。
    /// 注意：Tab 开头不构成公式注入，原样保留。
    /// </summary>
    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+SUM(A1)", "'+SUM(A1)")]
    [InlineData("-2+3", "'-2+3")]
    [InlineData("@cmd", "'@cmd")]
    [InlineData("  =1+1", "'  =1+1")] // 前导空白后接公式字符同样转义
    [InlineData("\tTab 开头", "\tTab 开头")]
    [InlineData("正常文本", "正常文本")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void SanitizeCsvCell_PrefixesFormulaChars(string? input, string expected)
    {
        Assert.Equal(expected, HistoryQueryHelper.SanitizeCsvCell(input));
    }

    /// <summary>
    /// 跨月查询（1月31日 ~ 2月1日）应正常返回跨月记录。
    /// </summary>
    [Fact]
    public void ApplyRangeFilter_CrossMonthBoundary_ReturnsRecordsAcrossMonth()
    {
        var from = new System.DateTime(2026, 1, 31, 12, 0, 0);
        var to = new System.DateTime(2026, 2, 1, 12, 0, 0);
        var logs = new List<ProductionLog>
        {
            Log("d1", "白班", 10, 0, new System.DateTime(2026, 1, 30, 12, 0, 0)), // 1-30（不含）
            Log("d1", "白班", 20, 0, new System.DateTime(2026, 1, 31, 18, 0, 0)), // 1-31 18:00
            Log("d1", "白班", 30, 0, new System.DateTime(2026, 2, 1, 6, 0, 0)),   // 2-1 06:00
            Log("d1", "白班", 40, 0, new System.DateTime(2026, 2, 2, 12, 0, 0)), // 2-2（不含）
        }.AsQueryable();

        var filtered = HistoryQueryHelper.ApplyRangeFilter(
            logs, from, to, null, null,
            nameof(ProductionLog.Timestamp), nameof(ProductionLog.DeviceId), nameof(ProductionLog.ShiftName));

        Assert.Equal(2, filtered.Count());
    }
}
