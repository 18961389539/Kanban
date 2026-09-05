using Kanban.Collector.Core.Models;
using Kanban.Contracts.Metrics;

namespace Kanban.Collector.Core.Services;

/// <summary>
/// 主页缺陷帕累托输入：当前班次（无班次则当日 0:00 起）窗口内新增件数。
/// 有历史库时按快照差分；无历史读侧时回退 PLC 当前累计值（测试/未落库场景）。
/// </summary>
public static class DefectParetoInputBuilder
{
    /// <summary>解析主页帕累托时间窗：当前班次 [start, now]；无班次则 [今日 0:00, now]。</summary>
    public static (DateTime From, DateTime To) ResolveHomeWindow(IReadOnlyList<ShiftConfig> shifts, DateTime now)
    {
        if (shifts.Count > 0)
        {
            foreach (var sc in shifts)
            {
                var (start, end) = sc.ResolveRange(now);
                if (start <= now && now < end)
                    return (start, now);
            }
        }

        return (now.Date, now);
    }

    public static IReadOnlyList<DefectParetoInput> BuildHomeInputs(
        Device device,
        IDefectHistoryReader? historyReader,
        IReadOnlyList<ShiftConfig> shifts,
        DateTime now)
    {
        if (device.Defects.Count == 0)
            return [];

        var (from, to) = ResolveHomeWindow(shifts, now);
        IReadOnlyDictionary<string, int> increments;
        if (historyReader != null)
        {
            var bounds = historyReader.QueryWindowBounds(from, to, device.Id);
            var points = bounds.Select(b => new DefectHistoryPoint(
                b.DefectId,
                b.DefectName,
                b.Timestamp,
                b.Count,
                b.ShiftName)).ToList();
            increments = DefectWindowMetrics.ComputeIncrements(points, from, to);
        }
        else
        {
            var live = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var d in device.Defects)
                live[d.Id] = Math.Max(0, d.Count);
            increments = live;
        }

        var inputs = new List<DefectParetoInput>(device.Defects.Count);
        foreach (var d in device.Defects)
        {
            increments.TryGetValue(d.Id, out var count);
            inputs.Add(new DefectParetoInput(
                d.Name,
                count,
                (Kanban.Contracts.Enums.DefectSeverity)(int)d.Severity,
                (Kanban.Contracts.Enums.DefectCategory)(int)d.Category,
                d.PlcAddress ?? ""));
        }

        return inputs;
    }
}
