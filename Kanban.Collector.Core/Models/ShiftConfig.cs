using CommunityToolkit.Mvvm.ComponentModel;
using NodaTime;
using System.Text.Json.Serialization;

namespace Kanban.Collector.Core.Models;

/// <summary>
/// 班次配置（时间部分，不绑定具体日期，仅表示每日班次起止时刻）
/// </summary>
public partial class ShiftConfig : ObservableObject
{
    /// <summary>
    /// 班次名称（如 早班 / 中班 / 晚班）
    /// </summary>
    [ObservableProperty]
    private string _name = "默认班次";

    /// <summary>
    /// 班次开始时间（仅时分秒部分有效，日期部分忽略）
    /// </summary>
    [ObservableProperty]
    private TimeSpan _startTime = new(8, 0, 0);

    /// <summary>
    /// 班次结束时间（仅时分秒部分有效；若小于 StartTime 表示跨天）。
    /// 允许 24:00（TimeSpan.FromHours(24)）表示当天结束；
    /// Contains/DurationHours/GetCurrentStart 对该值语义正确，ResolveRange 内部已做归一化。
    /// </summary>
    [ObservableProperty]
    private TimeSpan _endTime = new(20, 0, 0);

    /// <summary>
    /// 班次时长（小时）。跨天时自动 +24。
    /// </summary>
    [JsonIgnore]
    public double DurationHours
    {
        get
        {
            var diff = EndTime - StartTime;
            if (diff.TotalHours < 0) diff = diff.Add(TimeSpan.FromHours(24));
            return diff.TotalHours;
        }
    }

    /// <summary>
    /// 判断指定时刻是否在本班次内
    /// </summary>
    public bool Contains(TimeSpan timeOfDay)
    {
        if (EndTime > StartTime)
            return timeOfDay >= StartTime && timeOfDay < EndTime;
        // 跨天
        return timeOfDay >= StartTime || timeOfDay < EndTime;
    }

    /// <summary>
    /// 用 NodaTime 计算本班次相对 reference 的“最近一次实际发生”绝对区间 [Start, End)。
    /// 跨天（EndTime ≤ StartTime）由 NodaTime 的日期运算保证正确，无需手动 AddDays(-1) 判断。
    /// 约定：返回包含 reference 的班次实例；若 reference 落在班次间隙（相邻班次未铺满 24h），
    /// 则取最晚开始 ≤ reference 的那次实例。与历史 GetShiftAbsoluteRange 行为一致。
    /// EndTime 允许 24:00（TimeSpan 1.00:00:00，工厂“晚班 16:00-24:00”常规配置）：
    /// NodaTime LocalTime 合法上界为当天最后一 tick，这里对 24:00 归一化为 00:00，
    /// startT &gt; endT 必然进入跨天分支，得到 [start, 次日 00:00) 的正确区间（审查修复 2026-09-03）。
    /// </summary>
    public (DateTime Start, DateTime End) ResolveRange(DateTime reference)
    {
        var startT = LocalTime.FromTicksSinceMidnight(StartTime.Ticks % NodaConstants.TicksPerDay);
        var endT   = LocalTime.FromTicksSinceMidnight(EndTime.Ticks % NodaConstants.TicksPerDay);
        var refLocal = LocalDateTime.FromDateTime(reference); // 以本地墙钟解释，不带时区

        if (startT <= endT)
        {
            // 不跨天：当天 [startT, endT)
            var start = refLocal.Date.At(startT);
            var end   = refLocal.Date.At(endT);
            if (reference < start.ToDateTimeUnspecified())
            {
                start = start.PlusDays(-1);
                end   = end.PlusDays(-1);
            }
            return (start.ToDateTimeUnspecified(), end.ToDateTimeUnspecified());
        }

        // 跨天：[startT, 次日 endT)
        if (refLocal.TimeOfDay >= startT)
        {
            var start = refLocal.Date.At(startT);
            var end   = refLocal.Date.PlusDays(1).At(endT);
            return (start.ToDateTimeUnspecified(), end.ToDateTimeUnspecified());
        }
        var start2 = refLocal.Date.PlusDays(-1).At(startT);
        var end2   = refLocal.Date.At(endT);
        return (start2.ToDateTimeUnspecified(), end2.ToDateTimeUnspecified());
    }

    /// <summary>
    /// 计算“当前时刻所属班次”的起始 DateTime（用于重启后 OEE 时间重建范围下界）。
    /// 跨天班次若当前在凌晨段，起始落在昨天。与历史 GetCurrentShiftStart 行为一致。
    /// </summary>
    public DateTime GetCurrentStart(DateTime now)
    {
        // StartTime 同样做 24:00 归一化（配置异常时防 NodaTime 越界，与 ResolveRange 同口径）
        var startT = LocalTime.FromTicksSinceMidnight(StartTime.Ticks % NodaConstants.TicksPerDay);
        var refLocal = LocalDateTime.FromDateTime(now);

        if (EndTime <= StartTime) // 跨天
            return (refLocal.TimeOfDay >= startT
                        ? refLocal.Date.At(startT)
                        : refLocal.Date.PlusDays(-1).At(startT)).ToDateTimeUnspecified();

        return refLocal.Date.At(startT).ToDateTimeUnspecified();
    }
}
