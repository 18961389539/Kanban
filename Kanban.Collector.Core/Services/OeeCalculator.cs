using Kanban.Core.Models;
using Kanban.Core.Entities;
using OeeFormulas = Kanban.Analysis.OeeCalculator;

namespace Kanban.Core.Services;

/// <summary>
/// OEE 计算门面（Core 侧）：四率公式**全部委托** <see cref="Kanban.Analysis.OeeCalculator"/>
/// （ADR-4 单源，2026-08-13 收敛——此前 WASM 在 OeeAnalysis 复制公式，现 Web 直接调用 Analysis）。
/// 本类保留依赖 Core 实体的 <see cref="CalculateStateDurations"/>（状态转换记录 → 各状态时长），
/// 供采集/历史链路使用。
/// </summary>
public static class OeeCalculator
{
    /// <summary>合格率 = OK / (OK + NG)，委托单源实现（Clamp [0,1]）</summary>
    public static double CalculateQualityRate(int ok, int ng) => OeeFormulas.CalculateQualityRate(ok, ng);

    /// <summary>性能率 = 累计实际产量 / 理想产量，委托单源实现（Clamp [0,1]）</summary>
    public static double CalculatePerformanceRate(int totalOk, int totalNg, int targetCycle, double runTimeSeconds)
        => OeeFormulas.CalculatePerformanceRate(totalOk, totalNg, targetCycle, runTimeSeconds);

    /// <summary>可用率 = 运行时间 / (运行时间 + 报警时间)，委托单源实现；业务口径不含 PausedTime</summary>
    public static double CalculateAvailabilityRate(double runTime, double alarmTime)
        => OeeFormulas.CalculateAvailabilityRate(runTime, alarmTime);

    /// <summary>OEE = 合格率 × 性能率 × 可用率，委托单源实现（Clamp [0,1]）</summary>
    public static double CalculateOee(double qualityRate, double performanceRate, double availabilityRate)
        => OeeFormulas.CalculateOee(qualityRate, performanceRate, availabilityRate);

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
