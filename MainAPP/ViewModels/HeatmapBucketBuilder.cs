using MainAPP.Resources;

namespace MainAPP.ViewModels;

/// <summary>
/// 设备状态热力图桶构建器：把状态时间线段切成等宽时间桶，桶色取桶内重叠最长的状态段。
/// 每个桶记录着色归属段（SourceSegment），点击交互直接使用它，与着色逻辑保持一致。
/// </summary>
internal static class HeatmapBucketBuilder
{
    /// <summary>
    /// 桶数上限：超过时加大桶宽而非缩小（P2-14 教训：30→15 分钟会使桶数翻倍，
    /// UniformGrid 等分后每格宽度过小导致视觉失效），7 天窗口按 120 分钟桶宽收敛到 84 桶。
    /// </summary>
    private const int MaxBucketCount = 120;

    public static List<HeatmapBucket> Build(IReadOnlyList<ReviewStatusSegment> segments)
    {
        if (segments.Count == 0) return new();
        var totalStart = segments[0].Start;
        var totalEnd = segments[^1].End;
        var totalMin = Math.Max(1, (totalEnd - totalStart).TotalMinutes);
        var bucketMin = totalMin > 480 ? 30 : totalMin > 120 ? 15 : 5;
        var bucketCount = (int)Math.Ceiling(totalMin / bucketMin);
        while (bucketCount > MaxBucketCount)
        {
            bucketMin *= 2;
            bucketCount = (int)Math.Ceiling(totalMin / bucketMin);
        }
        // 时间轴刻度：约 8 个均匀取样；跨天窗口显示日期，单日窗口显示时刻
        var crossDay = totalEnd.Date > totalStart.Date;
        var tickInterval = Math.Max(1, bucketCount / 8);

        var buckets = new List<HeatmapBucket>(bucketCount);
        for (var i = 0; i < bucketCount; i++)
        {
            var bucketStart = totalStart.AddMinutes(i * bucketMin);
            var bucketEnd = bucketStart.AddMinutes(bucketMin);
            // 末桶裁剪：窗口时长非桶宽整数倍时，最后一个桶可能越过窗口终点，
            // 越界部分不与任何段重叠会显示为"未知"灰格，裁剪后直接覆盖到窗口终点。
            if (bucketEnd > totalEnd) bucketEnd = totalEnd;

            ReviewStatusSegment? bestSegment = null;
            var best = (StatusWord: 0, TotalMin: 0.0);
            foreach (var seg in segments)
            {
                var overlapStart = bucketStart > seg.Start ? bucketStart : seg.Start;
                var overlapEnd = bucketEnd < seg.End ? bucketEnd : seg.End;
                if (overlapEnd <= overlapStart) continue;
                var overlapMin = (overlapEnd - overlapStart).TotalMinutes;
                if (overlapMin > best.TotalMin)
                {
                    best = (seg.StatusWord, overlapMin);
                    bestSegment = seg;
                }
            }
            var statusText = best.StatusWord switch
            {
                0 => Strings.Status_Initial,
                1 => Strings.Status_Running,
                2 => Strings.Status_Alarm,
                3 => Strings.Status_Paused,
                _ => Strings.Status_Unknown,
            };
            buckets.Add(new HeatmapBucket
            {
                Start = bucketStart,
                End = bucketEnd,
                StatusWord = best.StatusWord,
                StatusText = statusText,
                SourceSegment = bestSegment,
                TickLabel = i % tickInterval == 0
                    ? (crossDay ? bucketStart.ToString("MM-dd") : bucketStart.ToString("HH:mm"))
                    : null,
            });
        }
        return buckets;
    }
}
