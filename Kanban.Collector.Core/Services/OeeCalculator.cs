using MainAPP.Models;
using MainAPP.Entities;

namespace MainAPP.Services;

/// <summary>
/// OEE（设备综合效率）计算器。
/// 从 Device 模型中提取计算逻辑，独立可测试。
/// 所有比率（合格率/性能率/可用率/OEE）均限制在 [0, 1] 范围内，
/// 避免出现 > 100% 的异常值（如设备超速运转、节拍设置过保守等场景）。
/// </summary>
public static class OeeCalculator
{
    /// <summary>
    /// 将比率限制在 [0, 1] 范围内
    /// </summary>
    private static double Clamp(double value) =>
        value < 0 ? 0 : (value > 1 ? 1 : value);

    /// <summary>
    /// 合格率 = OK / (OK + NG)
    /// </summary>
    public static double CalculateQualityRate(int ok, int ng)
    {
        var total = ok + ng;
        return total > 0 ? Clamp((double)ok / total) : 0;
    }

    /// <summary>
    /// 性能率 = 累计实际产量 / 理想产量
    /// 理想产量 = 目标节拍(件/小时) × 运行时间(小时)
    /// </summary>
    public static double CalculatePerformanceRate(int totalOk, int totalNg, int targetCycle, double runTimeSeconds)
    {
        if (targetCycle <= 0 || runTimeSeconds <= 0) return 0;
        var idealOutput = targetCycle * (runTimeSeconds / 3600.0);
        return idealOutput > 0 ? Clamp((totalOk + totalNg) / idealOutput) : 0;
    }

    /// <summary>
    /// 可用率 = 运行时间 / (运行时间 + 报警时间)
    /// 注意：按业务设计不含 PausedTime（见 project_memory.md）
    /// </summary>
    public static double CalculateAvailabilityRate(double runTime, double alarmTime)
    {
        var denom = runTime + alarmTime;
        return denom > 0 ? Clamp(runTime / denom) : 0;
    }

    /// <summary>
    /// OEE = 合格率 × 性能率 × 可用率
    /// </summary>
    public static double CalculateOee(double qualityRate, double performanceRate, double availabilityRate)
    {
        return Clamp(qualityRate * performanceRate * availabilityRate);
    }

    /// <summary>
    /// 从状态转换记录中计算各状态累计时长。
    /// 遍历 [from, to] 区间内的转换事件，按当前状态累计时长直到下一事件或区间结束。
    /// 区间外（from 之前）最近一条转换的 CurrentState 作为区间起始状态。
    /// 注意：to 会被防御性截断到 DateTime.Now——实时查询时 ToDate 可能是未来时刻
    /// （如"今天"= 23:59:59），但设备状态只能持续到现在，未来时间不应计入任何状态时长，
    /// 否则 RunTime 被高估导致性能率趋近 0、可用率虚假接近 100%。
    /// </summary>
    /// <param name="transitions">按 EventTime 升序排列的状态转换记录</param>
    /// <param name="from">查询起始时间</param>
    /// <param name="to">查询结束时间（若超过当前时刻将被截断）</param>
    /// <param name="initialState">区间外的起始状态（from 之前最近一条记录的 CurrentState）</param>
    /// <returns>(runTime, alarmTime, pausedTime) 秒</returns>
    public static (double RunTime, double AlarmTime, double PausedTime) CalculateStateDurations(
        IReadOnlyList<StatusTransitionRecord> transitions,
        DateTime from, DateTime to, int initialState)
    {
        // 防御性截断：未来时间不计入状态时长（实时查询 ToDate=23:59:59 场景）
        var now = DateTime.Now;
        if (to > now) to = now;
        if (from > now) return (0, 0, 0);

        double run = 0, alarm = 0, paused = 0;
        var currentState = initialState;
        var segmentStart = from;

        foreach (var t in transitions)
        {
            // 查询已过滤 EventTime >= from，此处只需跳过 from 之前的（理论上不会出现）
            if (t.EventTime < from) continue;

            // 当前状态从 segmentStart 持续到 t.EventTime
            // 若 t.EventTime == from == segmentStart，duration 为 0，不累计，但仍需更新状态
            var duration = (t.EventTime - segmentStart).TotalSeconds;
            if (duration > 0)
                AccumulateState(ref run, ref alarm, ref paused, currentState, duration);

            currentState = t.CurrentState;
            segmentStart = t.EventTime;
        }

        // 最后一段：从最后事件到 to
        if (segmentStart < to)
        {
            var duration = (to - segmentStart).TotalSeconds;
            if (duration > 0)
                AccumulateState(ref run, ref alarm, ref paused, currentState, duration);
        }

        return (run, alarm, paused);
    }

    private static void AccumulateState(ref double run, ref double alarm, ref double paused,
        int state, double seconds)
    {
        switch (state)
        {
            case (int)DeviceStatus.Running: run += seconds; break;
            case (int)DeviceStatus.Alarm: alarm += seconds; break;
            case (int)DeviceStatus.Paused: paused += seconds; break;
            // DeviceStatus.Unknown — 不累计任何时间
        }
    }
}
