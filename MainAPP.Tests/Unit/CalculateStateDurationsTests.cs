using System;
using System.Collections.Generic;
using MainAPP.Models;
using MainAPP.Services;
using Xunit;
using MainAPP.Entities;

namespace MainAPP.Tests.Unit;

/// <summary>
/// OeeCalculator.CalculateStateDurations 单元测试。
/// 这是重启后 OEE 时间重建、历史查询共用的核心算法：遍历状态转换记录按状态累计时长。
/// 覆盖：无记录、单条/多条转换、Paused 仅计暂停、initialState 起始态、
/// 未来 to 防御性截断、from 超出当前时刻返回 0。
/// </summary>
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class CalculateStateDurationsTests
{
    private static StatusTransitionRecord Tr(int prev, int cur, DateTime t) =>
        new() { PreviousState = prev, CurrentState = cur, EventTime = t };

    [Fact]
    public void NoTransitions_InitialState1_AccruesToNow()
    {
        // 区间内无任何转换，整段按 initialState 累计
        var from = new DateTime(2026, 7, 23, 8, 0, 0);
        var to = from.AddMinutes(30);
        var (run, alarm, paused) = OeeCalculator.CalculateStateDurations(
            new List<StatusTransitionRecord>(), from, to, initialState: 1);

        Assert.Equal(1800, run); // 30 分钟运行
        Assert.Equal(0, alarm);
        Assert.Equal(0, paused);
    }

    [Fact]
    public void NoTransitions_InitialState0_AccruesNothing()
    {
        var from = new DateTime(2026, 7, 23, 8, 0, 0);
        var to = from.AddMinutes(30);
        var (run, alarm, paused) = OeeCalculator.CalculateStateDurations(
            new List<StatusTransitionRecord>(), from, to, initialState: 0);

        Assert.Equal(0, run);
        Assert.Equal(0, alarm);
        Assert.Equal(0, paused);
    }

    [Fact]
    public void SingleTransition_RunThenAlarm()
    {
        var t0 = new DateTime(2026, 7, 23, 8, 0, 0);
        var transitions = new List<StatusTransitionRecord>
        {
            Tr(1, 2, t0.AddMinutes(10)) // 8:10 运行→报警
        };
        var (run, alarm, paused) = OeeCalculator.CalculateStateDurations(
            transitions, t0, t0.AddMinutes(30), initialState: 1);

        // 8:00-8:10 运行 600s；8:10-8:30 报警 1200s
        Assert.Equal(600, run);
        Assert.Equal(1200, alarm);
        Assert.Equal(0, paused);
    }

    [Fact]
    public void MultipleTransitions_RunAlarmPaused()
    {
        var t0 = new DateTime(2026, 7, 23, 8, 0, 0);
        var transitions = new List<StatusTransitionRecord>
        {
            Tr(1, 2, t0.AddMinutes(10)), // 8:10 运行→报警
            Tr(2, 3, t0.AddMinutes(20)), // 8:20 报警→暂停
        };
        var (run, alarm, paused) = OeeCalculator.CalculateStateDurations(
            transitions, t0, t0.AddMinutes(30), initialState: 1);

        // 8:00-8:10 运行 600；8:10-8:20 报警 600；8:20-8:30 暂停 600
        Assert.Equal(600, run);
        Assert.Equal(600, alarm);
        Assert.Equal(600, paused);
    }

    [Fact]
    public void UnknownState_DoesNotAccrue()
    {
        var t0 = new DateTime(2026, 7, 23, 8, 0, 0);
        var transitions = new List<StatusTransitionRecord>
        {
            Tr(0, 0, t0.AddMinutes(10)) // 未知态切换
        };
        var (run, alarm, paused) = OeeCalculator.CalculateStateDurations(
            transitions, t0, t0.AddMinutes(30), initialState: 0);

        // 全程初始态 0，未知态也不累计
        Assert.Equal(0, run);
        Assert.Equal(0, alarm);
        Assert.Equal(0, paused);
    }

    [Fact]
    public void TransitionExactlyAtFrom_DurationZeroForFirstSegment()
    {
        var t0 = new DateTime(2026, 7, 23, 8, 0, 0);
        var transitions = new List<StatusTransitionRecord>
        {
            Tr(1, 2, t0) // 恰好在 from
        };
        // from==segmentStart，第一段 duration=0；之后 8:00-8:30 按 state=2 报警
        var (run, alarm, paused) = OeeCalculator.CalculateStateDurations(
            transitions, t0, t0.AddMinutes(30), initialState: 1);

        Assert.Equal(0, run);
        Assert.Equal(1800, alarm);
        Assert.Equal(0, paused);
    }

    [Fact]
    public void FutureTo_IsTruncatedToNow()
    {
        var from = DateTime.Now.AddHours(-1);
        var to = DateTime.Now.AddDays(1000); // 远未来
        var (run, alarm, paused) = OeeCalculator.CalculateStateDurations(
            new List<StatusTransitionRecord>(), from, to, initialState: 1);

        // 被截断到 now：总时长 ≈ 1 小时
        var total = run + alarm + paused;
        Assert.InRange(total, 3599, 3601);
    }

    [Fact]
    public void FromAfterNow_ReturnsZero()
    {
        var from = DateTime.Now.AddHours(5);
        var to = from.AddHours(1);
        var (run, alarm, paused) = OeeCalculator.CalculateStateDurations(
            new List<StatusTransitionRecord>(), from, to, initialState: 1);

        Assert.Equal(0, run);
        Assert.Equal(0, alarm);
        Assert.Equal(0, paused);
    }
}
