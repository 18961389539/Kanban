using Kanban.Collector.Core.Models;
using MainAPP.Models;
using Kanban.Collector.Core.Entities;
using Kanban.Contracts.Metrics;
using MainAPP.Resources;
using OfflineCause = Kanban.Contracts.Enums.OfflineCause;
using DeviceStatusLocKeys = Kanban.Contracts.Enums.DeviceStatusLocKeys;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using CsvHelper.Configuration.Attributes;

namespace MainAPP.ViewModels;

internal static class HistoryQueryHelper
{
    /// <summary>
    /// 在班次列表中查找包含指定时刻的班次（共享逻辑，替代 OverviewViewModel/ProductionLineViewModel/HistoryQueryViewModel 三处重复的 foreach）。
    /// 返回 (班次配置, 在列表中的索引)；未匹配返回 (null, -1)。
    /// shifts 为 null/空时返回 (null, -1)，由调用方决定回退策略。
    /// </summary>
    internal static (ShiftConfig? Shift, int Index) FindCurrentShift(
        System.Collections.Generic.IReadOnlyList<ShiftConfig>? shifts, TimeSpan timeOfDay)
    {
        if (shifts == null || shifts.Count == 0)
            return (null, -1);
        for (int i = 0; i < shifts.Count; i++)
        {
            if (shifts[i].Contains(timeOfDay))
                return (shifts[i], i);
        }
        return (null, -1);
    }

    /// <summary>小时桶序列：从 from 对齐整点 AddHours(1) 累计到 to（与 <see cref="HourlyProductionDiff.BuildHourStarts"/> 同一套）。</summary>
    internal static DateTime[] BuildHourlyBuckets(DateTime from, DateTime to)
        => HourlyProductionDiff.BuildHourStarts(from, to);

    /// <summary>定位时刻所属桶索引：先精确对齐，再回退到第一个 ≥ 对齐时刻的桶；无则 -1。</summary>
    internal static int GetBucketIndex(DateTime[] buckets, DateTime time)
        => HourlyProductionDiff.GetBucketIndex(buckets, time);

    public static List<List<ProductionLog>> SplitShiftInstances(List<ProductionLog> sortedLogs)
    {
        List<List<ProductionLog>> groups = [];
        List<ProductionLog> cur = [];
        foreach (var p in sortedLogs)
        {
            if (cur.Count > 0)
            {
                var prev = cur[^1];
                if (p.OkProduction < prev.OkProduction
                    || p.NgProduction < prev.NgProduction
                    || p.ShiftName != prev.ShiftName)
                {
                    groups.Add(cur);
                    cur = [];
                }
            }
            cur.Add(p);
        }
        if (cur.Count > 0) groups.Add(cur);
        return groups;
    }

    /// <summary>
    /// 计算查询窗口内的产量（窗口差分），使产量与时间口径严格对齐。
    /// <para>
    /// <see cref="ProductionLog.OkProduction"/> / <see cref="ProductionLog.NgProduction"/> 存储的是
    /// 「班次内累计值」（每班次起始重置），并非 PLC 全局累计。因此窗口内产量 = 窗口末条累计值 − 窗口起点同班次实例的累计值。
    /// 若直接取末条累计（旧实现），非整班次子集窗口下分子覆盖整个班次、分母只算窗口，性能率被高估，与主页 OEE 对不上。
    /// </para>
    /// <para>
    /// 修复 2026-08-26（P1，与产量趋势图 BuildProductionDeltas 同源统一）：
    /// 旧实现用 <see cref="FindBaselineBeforeWindow"/> 在 [from-1天, from) 里找「最近同班次快照」作基线，
    /// 但看不到窗口内后续班次切换——7 天等长窗口/数据缺口下会把更早班次实例的累计值误作基线，
    /// <c>Math.Max(0, 末条 − 基线)</c> 把产量砍成 0 或混入旧累计虚高；趋势图侧则把「窗口起点快照相对
    /// 窗口前快照」的增量（发生在窗口外）计入首桶。两侧口径互不一致。
    /// 现统一为「实例基线差分」：合并窗口内 + 窗口前日志按班次实例分组（累计回落/班次切换即切组，
    /// 组内窗口前日志天然属于同一实例），每实例基线 = 窗口起点快照 或 窗口前同实例末条 或 实例首条，
    /// 产量 = 实例窗口内末条 − 基线。总产量与趋势图各桶之和严格相等，且窗口外增量不再计入。
    /// </para>
    /// </summary>
    public static (int Ok, int Ng) SumWindowProduction(
        List<ProductionLog> logsInWindow,
        List<ProductionLog> baselineCandidates,
        DateTime windowFrom)
    {
        if (logsInWindow.Count == 0) return (0, 0);
        var all = new List<ProductionLog>(logsInWindow.Count + baselineCandidates.Count);
        all.AddRange(logsInWindow);
        all.AddRange(baselineCandidates);
        int ok = 0, ng = 0;
        foreach (var group in SplitShiftInstances(all.OrderBy(log => log.Timestamp).ToList()))
        {
            var winPart = group.Where(log => log.Timestamp >= windowFrom).ToList();
            if (winPart.Count == 0) continue;
            var last = winPart[^1];
            var baseRec = ResolveInstanceBase(group, winPart, windowFrom);
            ok += Math.Max(0, last.OkProduction - baseRec.OkProduction);
            ng += Math.Max(0, last.NgProduction - baseRec.NgProduction);
        }
        return (ok, ng);
    }

    /// <summary>
    /// 解析班次实例的窗口基线（与 <c>ProductionReviewMetricsService.BuildProductionDeltas</c> 共用，保证两侧严格一致）：
    /// <list type="bullet">
    /// <item>窗口起点恰有快照（实例窗口内首条时间 ≤ from）→ 该快照累计值（窗口起点即累计基线）；</item>
    /// <item>窗口起点无快照但实例从窗口前延续（组内存在 &lt; from 的同实例日志）→ 窗口前同实例末条累计值；</item>
    /// <item>无基线（窗口前无同实例数据）→ 实例窗口内首条累计值（只统计窗口可见部分）。</item>
    /// </list>
    /// <paramref name="group"/> 为 <see cref="SplitShiftInstances"/> 切分后的单实例组（含窗口前日志），
    /// <paramref name="winPart"/> 为其 Timestamp ≥ <paramref name="windowFrom"/> 的子序列（非空、升序）。
    /// </summary>
    internal static ProductionLog ResolveInstanceBase(
        List<ProductionLog> group,
        List<ProductionLog> winPart,
        DateTime windowFrom)
    {
        if (winPart[0].Timestamp <= windowFrom) return winPart[0];
        var before = group.LastOrDefault(log => log.Timestamp < windowFrom);
        return before ?? winPart[0];
    }

    /// <summary>
    /// 在窗口基准候选集中找到「与窗口内同班次实例、且最接近窗口起点」的快照。
    /// 倒序遍历：遇到目标班次名且此前未出现过其它班次名 → 即同一实例基准；
    /// 若此前已出现过其它班次名（说明发生过班次切换，目标班次是更早的实例）→ 返回 null（窗口前无同实例基准，记 0）。
    /// </summary>
    internal static ProductionLog? FindBaselineBeforeWindow(List<ProductionLog> baselineCandidates, string shiftName)
    {
        // 内部按时间倒序（最新在前）处理，调用方无需保证输入顺序。
        var ordered = baselineCandidates.OrderByDescending(p => p.Timestamp).ToList();
        bool seenDifferentShift = false;
        foreach (var r in ordered)
        {
            if (r.ShiftName == shiftName)
            {
                if (seenDifferentShift) return null;
                return r;
            }
            seenDifferentShift = true;
        }
        return null;
    }

    /// <summary>
    /// 设备状态字 → 统一文本。与 <see cref="DeviceStatus"/> 常量对齐：
    /// 0=离线, 1=运行, 2=报警, 3=待机, 其它=未知。
    /// 离线时 <paramref name="offlineCause"/> 区分 PLC 报 0 / 通讯中断 / 采集停止 / 空窗。
    /// 该映射为全应用唯一来源，<see cref="DeviceDetailViewModel.MapStatus"/> 等实时显示处复用本方法。
    /// </summary>
    public static string GetStateText(int state, int offlineCause = 0)
    {
        var key = DeviceStatusLocKeys.For(state, (OfflineCause)offlineCause);
        return key switch
        {
            "Status_Running" => Strings.Status_Running,
            "Status_Alarm" => Strings.Status_Alarm,
            "Status_Paused" => Strings.Status_Paused,
            "Status_Offline_PlcReported" => Strings.Status_Offline_PlcReported,
            "Status_Offline_CommsLost" => Strings.Status_Offline_CommsLost,
            "Status_Offline_AcquisitionStopped" => Strings.Status_Offline_AcquisitionStopped,
            "Status_Offline_GapFilled" => Strings.Status_Offline_GapFilled,
            "Status_Offline" => Strings.Status_Offline,
            _ => Strings.Status_Unknown,
        };
    }

    public static string GetEventTypeText(AlarmEventType type) => type switch
    {
        AlarmEventType.Triggered => Strings.EventType_Triggered,
        AlarmEventType.Recovered => Strings.EventType_Recovered,
        AlarmEventType.ShiftChange => Strings.EventType_ShiftChange,
        _ => Strings.Status_Unknown
    };

    /// <summary>
    /// 生成导出 CSV。表头按当前 UI 文化从 `Csv_Hd_&lt;属性名&gt;` 资源键解析
    /// （多语言修复 2026-09-18：此前行 DTO 用静态中文 [Name] 特性，切换语言后表头仍是中文）。
    /// 未配置资源键的属性回退为属性名（如审计导出的英文标识，保持原样）。
    /// </summary>
    public static string BuildCsv<T>(IEnumerable<T> rows, params string[] footerLines)
    {
        var sb = new StringBuilder();
        using (var writer = new StringWriter(sb))
        using (var csv = new CsvWriter(writer, CultureInfo.InvariantCulture))
        {
            RegisterLocalizedHeaderMap<T>(csv);
            csv.WriteRecords(rows);
        }
        if (footerLines.Length > 0)
        {
            sb.AppendLine();
            foreach (var line in footerLines)
                sb.AppendLine(line);
        }
        return sb.ToString();
    }

    /// <summary>按当前文化为行类型成员注册本地化表头映射（数据行 DTO 上的静态 [Name] 特性会被映射覆盖）。</summary>
    private static void RegisterLocalizedHeaderMap<T>(CsvWriter csv)
    {
        var map = new DefaultClassMap<T>();
        foreach (var prop in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetCustomAttribute<IgnoreAttribute>() != null) continue;
            map.Map(typeof(T), prop)?.Name(Strings.S("Csv_Hd_" + prop.Name, prop.Name));
        }
        csv.Context.RegisterClassMap(map);
    }

    /// <summary>
    /// CSV 单元格公式注入防护：以 = + - @ 开头的文本在 Excel/WPS 中会被当作公式执行，
    /// 归档类 CSV（尤其含操作人/详情等用户可控文本）必须转义。仅在确实命中危险前缀时改写，
    /// 普通文本原样返回，避免改变既有导出内容。
    /// </summary>
    public static string SanitizeCsvCell(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
        var trimmed = value.TrimStart();
        if (trimmed.Length == 0) return value;
        var first = trimmed[0];
        return (first is '=' or '+' or '-' or '@' or '\t' or '\r')
            ? "'" + value
            : value;
    }

    /// <summary>
    /// 将查询结束时间限制为不超过当前时间，避免查询未来时间段的无效数据。
    /// 集中替代 Status/Alarm/Oee 查询 VM 中重复的 effectiveTo 三元表达式。
    /// </summary>
    public static DateTime ClampToNow(DateTime to)
        => to > DateTime.Now ? DateTime.Now : to;

    /// <summary>
    /// 根据总记录数与每页大小计算总页数，集中替代各查询 VM 中重复的
    /// <c>(int)Math.Ceiling((double)totalCount / pageSize)</c>。
    /// </summary>
    public static int CalcTotalPages(int totalCount, int pageSize)
        => (int)Math.Ceiling((double)totalCount / pageSize);

    /// <summary>
    /// 对已排序的数据源执行 Skip/Take 分页取页内项，集中替代各查询 VM 中重复的
    /// <c>source.Skip((currentPage - 1) * pageSize).Take(pageSize).ToList()</c>。
    /// 调用方需在传入前完成所需的排序（如按时间倒序）。
    /// </summary>
    public static List<T> PageItems<T>(IEnumerable<T> source, int currentPage, int pageSize)
        => source.Skip((currentPage - 1) * pageSize).Take(pageSize).ToList();

    /// <summary>
    /// 对历史记录 IQueryable 应用统一的「时间区间 + 可选设备 + 可选班次」过滤。
    /// 集中替代 Production/Status/Oee 查询与 HistoryService 中重复的 Where 模板。
    /// timeProperty/deviceProperty/shiftProperty 用 nameof(实体.属性) 传入（如 nameof(ProductionLog.Timestamp)）。
    /// </summary>
    public static IQueryable<T> ApplyRangeFilter<T>(
        IQueryable<T> query,
        DateTime from, DateTime to,
        string? deviceId, string? shiftName,
        string timeProperty, string deviceProperty, string shiftProperty)
    {
        var param = Expression.Parameter(typeof(T), "x");
        Expression? body = Expression.AndAlso(
            Expression.GreaterThanOrEqual(Expression.Property(param, timeProperty), Expression.Constant(from)),
            Expression.LessThanOrEqual(Expression.Property(param, timeProperty), Expression.Constant(to)));
        if (deviceId != null)
            body = Expression.AndAlso(body, Expression.Equal(Expression.Property(param, deviceProperty), Expression.Constant(deviceId)));
        if (shiftName != null)
            body = Expression.AndAlso(body, Expression.Equal(Expression.Property(param, shiftProperty), Expression.Constant(shiftName)));
        return query.Where(Expression.Lambda<Func<T, bool>>(body!, param));
    }
}