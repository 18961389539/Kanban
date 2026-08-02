using MainAPP.Models;
using MainAPP.Entities;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using CsvHelper;

namespace MainAPP.ViewModels;

internal static class HistoryQueryHelper
{
    /// <summary>
    /// 在班次列表中查找包含指定时刻的班次（共享逻辑，替代 OverviewViewModel/ProductionLineViewModel/HistoryQueryViewModel 三处重复的 foreach）。
    /// 返回 (班次配置, 在列表中的索引)；未匹配返回 (null, -1)。
    /// shifts 为 null/空时返回 (null, -1)，由调用方决定回退策略。
    /// </summary>
    internal static (ShiftConfig? Shift, int Index) FindCurrentShift(
        System.Collections.Generic.IList<ShiftConfig>? shifts, TimeSpan timeOfDay)
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
    /// <paramref name="baselineCandidates"/> 应为窗口起点之前（<c>[windowFrom-1天, windowFrom)</c>）按时间倒序的「全部班次」快照
    /// （不按班次过滤，以便通过班次名变化识别「同一班次实例」，避免把前一天同名班次误当基准）。
    /// </para>
    /// </summary>
    public static (int Ok, int Ng) SumWindowProduction(
        List<ProductionLog> logsInWindow,
        List<ProductionLog> baselineCandidates,
        DateTime windowFrom)
    {
        if (logsInWindow.Count == 0) return (0, 0);
        var groups = SplitShiftInstances(logsInWindow);
        int ok = 0, ng = 0;
        foreach (var g in groups)
        {
            var last = g.OrderByDescending(p => p.Timestamp).First();
            int okBase, ngBase;
            if (g.First().Timestamp <= windowFrom)
            {
                // 窗口起点恰有快照：直接以该快照累计值作基准（= 累计值 @ windowFrom）
                okBase = g.First().OkProduction;
                ngBase = g.First().NgProduction;
            }
            else
            {
                // 窗口起点无快照：取窗口前最近、且属于同一班次实例的快照累计值
                var baseRec = FindBaselineBeforeWindow(baselineCandidates, g.First().ShiftName);
                if (baseRec == null)
                {
                    // 基准缺失：窗口前无同班次实例快照（数据缺失或查询窗口从班次中段开始且无前置历史）。
                    // 旧实现记 0 导致整班次产量被算进窗口、性能率虚高；改为回退到班次实例内首条快照累计值，
                    // 即只统计窗口可见部分，与窗口差分本意一致。
                    okBase = g.First().OkProduction;
                    ngBase = g.First().NgProduction;
                }
                else
                {
                    okBase = baseRec.OkProduction;
                    ngBase = baseRec.NgProduction;
                }
            }
            ok += Math.Max(0, last.OkProduction - okBase);
            ng += Math.Max(0, last.NgProduction - ngBase);
        }
        return (ok, ng);
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
    /// 设备状态字 → 统一中文文本。与 <see cref="DeviceStatus"/> 常量对齐：
    /// 0=初始, 1=运行, 2=报警, 3=待机, 其它=未知。
    /// 该映射为全应用唯一来源，<see cref="DeviceDetailViewModel.MapStatus"/> 等实时显示处复用本方法，
    /// 避免历史日志与实时状态文本分叉。
    /// </summary>
    public static string GetStateText(int state) => state switch
    {
        0 => "初始", 1 => "运行", 2 => "报警", 3 => "待机", _ => "未知"
    };

    public static string GetEventTypeText(AlarmEventType type) => type switch
    {
        AlarmEventType.Triggered => "触发",
        AlarmEventType.Recovered => "恢复",
        AlarmEventType.ShiftChange => "班次切换",
        _ => "未知"
    };

    /// <summary>
    /// 将记录集合序列化为 CSV 文本，并在末尾追加可选注释行（每条注释行成为独立一行）。
    /// 集中替代各 QueryViewModel 中重复的 StringBuilder + CsvWriter 样板。
    /// </summary>
    public static string BuildCsv<T>(IEnumerable<T> rows, params string[] footerLines)
    {
        var sb = new StringBuilder();
        using (var writer = new StringWriter(sb))
        using (var csv = new CsvWriter(writer, CultureInfo.InvariantCulture))
        {
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