using Kanban.Contracts.Dtos;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;

namespace Kanban.Collector.Services;

/// <summary>
/// 班次进度计算（供 WASM/WPF 展示端查询）。
/// 口径与 MainAPP HomeViewModel.UpdateShiftProgress 一致：遍历班次配置，
/// 用 ShiftConfig.ResolveRange 找当前时段，输出已运行/剩余/比例。
/// </summary>
public sealed class ShiftProgressProvider
{
    private readonly AppSettings _appSettings;

    public ShiftProgressProvider(AppSettings appSettings)
    {
        _appSettings = appSettings;
    }

    /// <summary>计算当前班次进度。无班次配置或非班次时段时 IsInShift=false。</summary>
    public ShiftProgressDto GetProgress()
    {
        var now = DateTime.Now;
        // 锁内快照枚举：与 ConfigSyncHandler 的原地写入互斥（审查修复 2026-08-13）
        List<Kanban.Collector.Core.Models.ShiftConfig> shifts;
        lock (_appSettings.ShiftsLock) shifts = _appSettings.Shifts.ToList();
        if (shifts.Count == 0)
            return new ShiftProgressDto { Name = "非班次时段" };

        foreach (var sc in shifts)
        {
            var (start, end) = sc.ResolveRange(now);
            if (start <= now && now < end)
            {
                var totalSecs = (end - start).TotalSeconds;
                var elapsedSecs = (now - start).TotalSeconds;
                var remainingSecs = Math.Max(0, totalSecs - elapsedSecs);
                var ratio = totalSecs > 0 ? Math.Clamp(elapsedSecs / totalSecs, 0, 1) : 0;
                return new ShiftProgressDto
                {
                    IsInShift = true,
                    Name = sc.Name,
                    ElapsedText = FormatShiftTime(elapsedSecs),
                    RemainingText = FormatShiftTime(remainingSecs),
                    Ratio = ratio,
                    Pct = $"{ratio * 100:F0}%"
                };
            }
        }
        return new ShiftProgressDto { Name = "非班次时段" };
    }

    private static string FormatShiftTime(double secs)
        => Kanban.Contracts.Formatting.DurationFormatter.FormatCompact(secs);
}
