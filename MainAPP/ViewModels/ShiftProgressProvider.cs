using Kanban.Contracts.Formatting;
using Kanban.Collector.Core.Services;
using MainAPP.Resources;

namespace MainAPP.ViewModels;

/// <summary>
/// 主页班次进度计算：解析当前班次并计算已运行/剩余时间与百分比。
/// 纯计算服务，由 HomeViewModel 每 tick 调用并把快照应用到可绑定属性。
/// </summary>
public sealed class ShiftProgressProvider
{
    private readonly AppSettings _appSettings;

    public ShiftProgressProvider(AppSettings appSettings) => _appSettings = appSettings;

    public ShiftProgressSnapshot Compute(DateTime now)
    {
        // P0-1 修复 2026-09-02：写侧有两个线程（UI 的 CopySettings + Hub 的 ConfigSyncHandler），
        // 即使本方法在 UI 线程调用，也必须经锁内快照读取。
        var (shift, start, end) = ShiftConfigResolver.ResolveCurrentShift(_appSettings.GetShiftsSnapshot(), now);
        if (shift == null)
            return new ShiftProgressSnapshot(false, Strings.M115, "", 0, "");

        var totalSecs = (end - start).TotalSeconds;
        var elapsedSecs = (now - start).TotalSeconds;
        var remainingSecs = Math.Max(0, totalSecs - elapsedSecs);
        var ratio = totalSecs > 0 ? Math.Clamp(elapsedSecs / totalSecs, 0, 1) : 0;

        return new ShiftProgressSnapshot(
            true,
            shift.Name,
            string.Format(Strings.F117,
                DurationFormatter.FormatCompact(elapsedSecs),
                DurationFormatter.FormatCompact(remainingSecs)),
            ratio,
            $"{ratio * 100:F0}%");
    }
}

/// <summary>班次进度快照。</summary>
public sealed record ShiftProgressSnapshot(
    bool IsVisible,
    string Name,
    string Text,
    double Ratio,
    string Pct);
