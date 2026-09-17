namespace Kanban.Contracts.Metrics;

/// <summary>
/// 按小时产量差分：累计 OK/NG 快照 → 每小时增量。
/// WPF 设备详情/主页与 Web 看板共用，避免两端桶边界与基线口径漂移。
/// </summary>
public static class HourlyProductionDiff
{
    /// <summary>小时桶起始时刻 + 该小时 OK/NG 增量。</summary>
    public readonly record struct Series(DateTime[] Hours, int[] OkDiff, int[] NgDiff);

    /// <summary>小时桶序列：从 from 对齐整点 AddHours(1) 累计到 to（含当前未结束小时）。</summary>
    public static DateTime[] BuildHourStarts(DateTime from, DateTime to)
    {
        List<DateTime> list = [];
        var cur = new DateTime(from.Year, from.Month, from.Day, from.Hour, 0, 0);
        while (cur <= to)
        {
            list.Add(cur);
            cur = cur.AddHours(1);
        }
        return list.ToArray();
    }

    /// <summary>
    /// 整班小时桶：<c>[shiftStart, shiftEnd)</c> 内每一整点（与小时计划板同一套边界，不含下班整点）。
    /// </summary>
    public static DateTime[] BuildShiftHourStarts(DateTime shiftStart, DateTime shiftEnd)
    {
        List<DateTime> list = [];
        var cur = new DateTime(shiftStart.Year, shiftStart.Month, shiftStart.Day, shiftStart.Hour, 0, 0);
        while (cur < shiftEnd)
        {
            list.Add(cur);
            cur = cur.AddHours(1);
        }
        return list.ToArray();
    }

    /// <summary>
    /// 把已过小时的差分补齐到整班横轴：未到的小时为 0，避免夜班刚开始只剩一根 20:00 柱。
    /// </summary>
    public static Series PadToShiftWindow(Series elapsed, DateTime shiftStart, DateTime shiftEnd)
    {
        var hours = BuildShiftHourStarts(shiftStart, shiftEnd);
        if (hours.Length == 0)
            return new Series([], [], []);

        Dictionary<DateTime, int> okByHour = [];
        Dictionary<DateTime, int> ngByHour = [];
        for (var i = 0; i < elapsed.Hours.Length; i++)
        {
            okByHour[elapsed.Hours[i]] = i < elapsed.OkDiff.Length ? Math.Max(0, elapsed.OkDiff[i]) : 0;
            ngByHour[elapsed.Hours[i]] = i < elapsed.NgDiff.Length ? Math.Max(0, elapsed.NgDiff[i]) : 0;
        }

        var ok = new int[hours.Length];
        var ng = new int[hours.Length];
        for (var i = 0; i < hours.Length; i++)
        {
            ok[i] = okByHour.TryGetValue(hours[i], out var o) ? o : 0;
            ng[i] = ngByHour.TryGetValue(hours[i], out var n) ? n : 0;
        }
        return new Series(hours, ok, ng);
    }
    /// <summary>定位时刻所属桶索引：先精确对齐，再回退到第一个 ≥ 对齐时刻的桶；无则 -1。</summary>
    public static int GetBucketIndex(DateTime[] hours, DateTime time)
    {
        var aligned = new DateTime(time.Year, time.Month, time.Day, time.Hour, 0, 0);
        for (var i = 0; i < hours.Length; i++)
        {
            if (hours[i] == aligned) return i;
        }
        for (var i = 0; i < hours.Length; i++)
        {
            if (hours[i] >= aligned) return i;
        }
        return -1;
    }

    /// <summary>
    /// 窗口内快照按小时取末点，空小时沿用上一小时累计值，再对窗口前基线做累计差分。
    /// 负增量置零（班次切换重置）。
    /// </summary>
    public static Series FromSamples(
        IEnumerable<(DateTime Time, int Ok, int Ng)> samples,
        DateTime from,
        DateTime to,
        int baselineOk,
        int baselineNg)
    {
        var hours = BuildHourStarts(from, to);
        if (hours.Length == 0)
            return new Series([], [], []);

        var okCumulative = new int[hours.Length];
        var ngCumulative = new int[hours.Length];
        var hasSample = new bool[hours.Length];
        foreach (var sample in samples.OrderBy(s => s.Time))
        {
            var idx = GetBucketIndex(hours, sample.Time);
            if (idx >= 0 && idx < hours.Length)
            {
                okCumulative[idx] = sample.Ok;
                ngCumulative[idx] = sample.Ng;
                hasSample[idx] = true;
            }
        }

        var lastOk = 0;
        var lastNg = 0;
        for (var i = 0; i < hours.Length; i++)
        {
            if (hasSample[i])
            {
                lastOk = okCumulative[i];
                lastNg = ngCumulative[i];
            }
            else
            {
                okCumulative[i] = lastOk;
                ngCumulative[i] = lastNg;
            }
        }

        return new Series(
            hours,
            DiffCumulative(okCumulative, baselineOk),
            DiffCumulative(ngCumulative, baselineNg));
    }

    /// <summary>累计值转增量：后一桶减前一桶，负数置零；首桶扣 baseline。</summary>
    public static int[] DiffCumulative(int[] cumulative, int baseline)
    {
        if (cumulative.Length == 0) return cumulative;
        var result = new int[cumulative.Length];
        result[0] = Math.Max(0, cumulative[0] - baseline);
        for (var i = 1; i < cumulative.Length; i++)
            result[i] = Math.Max(0, cumulative[i] - cumulative[i - 1]);
        return result;
    }
}
