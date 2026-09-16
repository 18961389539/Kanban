namespace Kanban.Contracts.Metrics;

/// <summary>
/// 当前班次良率折线的点列：历史产量快照为班次会话累计 OK/NG，良率 = OK/(OK+NG)。
/// WPF 主页与 Web 看板共用，避免两端采样口径漂移。
/// </summary>
public static class ShiftQualityTrendBuilder
{
    public const int MaxPoints = 80;

    public static List<(DateTime Time, double Quality)> FromSamples(
        IEnumerable<(DateTime Time, int Ok, int Ng, string ShiftName)> samples,
        string? shiftName,
        DateTime from,
        DateTime to)
    {
        var points = samples
            .Where(sample => sample.Time >= from && sample.Time <= to)
            .Where(sample => string.IsNullOrEmpty(shiftName)
                || string.Equals(sample.ShiftName, shiftName, StringComparison.Ordinal))
            .Where(sample => sample.Ok + sample.Ng > 0)
            .OrderBy(sample => sample.Time)
            .Select(sample => (sample.Time, SnapshotMetrics.QualityRate(sample.Ok, sample.Ng)))
            .ToList();
        return Downsample(points, MaxPoints);
    }

    public static List<(DateTime Time, double Quality)> MergeLive(
        IReadOnlyList<(DateTime Time, double Quality)> history,
        DateTime now,
        double liveQuality,
        bool hasOutput)
    {
        var list = history.ToList();
        if (!hasOutput)
            return Downsample(list, MaxPoints);

        if (list.Count > 0 && (now - list[^1].Time).TotalSeconds < 20)
            list[^1] = (now, liveQuality);
        else
            list.Add((now, liveQuality));
        return Downsample(list, MaxPoints);
    }

    public static List<(DateTime Time, double Quality)> Downsample(
        IReadOnlyList<(DateTime Time, double Quality)> points, int max)
    {
        if (points.Count <= max)
            return points.ToList();

        var result = new List<(DateTime Time, double Quality)>(max);
        var step = (points.Count - 1) / (double)(max - 1);
        for (var i = 0; i < max; i++)
        {
            var index = Math.Clamp((int)Math.Round(i * step), 0, points.Count - 1);
            result.Add(points[index]);
        }

        return result;
    }
}
