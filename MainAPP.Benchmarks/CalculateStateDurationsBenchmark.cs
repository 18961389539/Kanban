using System.Collections.Generic;
using System;
using Kanban.Collector.Core.Models;
using MainAPP.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using BenchmarkDotNet.Attributes;
using Kanban.Collector.Core.Entities;

namespace MainAPP.Benchmarks;

/// <summary>
/// 状态时长重建（OeeCalculator.CalculateStateDurations）性能基准。
/// 该路径在每次重启 OEE 重建、以及每次历史状态查询时都会执行，
/// 转换记录条数随运行时长线性增长，是典型热点。
/// </summary>
[MemoryDiagnoser]
public class CalculateStateDurationsBenchmark
{
    [Params(10, 100, 1000)]
    public int TransitionCount;

    private List<StatusTransitionRecord> _transitions = new();
    private DateTime _from;
    private DateTime _to;

    [GlobalSetup]
    public void Setup()
    {
        _from = new DateTime(2026, 7, 23, 8, 0, 0);
        _to = _from.AddHours(12);
        _transitions = new List<StatusTransitionRecord>(TransitionCount);
        var span = (_to - _from).TotalSeconds;
        var step = TransitionCount > 0 ? span / (TransitionCount + 1) : span;
        var state = 1;
        for (int i = 0; i < TransitionCount; i++)
        {
            var t = _from.AddSeconds(step * (i + 1));
            _transitions.Add(new StatusTransitionRecord
            {
                PreviousState = state,
                CurrentState = (state % 3) + 1, // 在 1/2/3 间循环
                EventTime = t
            });
            state = (state % 3) + 1;
        }
    }

    [Benchmark]
    public (double Run, double Alarm, double Paused) Calculate() =>
        OeeCalculator.CalculateStateDurations(_transitions, _from, _to, initialState: 1);
}
