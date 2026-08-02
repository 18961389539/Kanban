using System;
using MainAPP.Models;
using BenchmarkDotNet.Attributes;

namespace MainAPP.Benchmarks;

/// <summary>
/// 班次解析（跨天）性能基准。ResolveRange / GetCurrentStart 在重启 OEE 重建、
/// 本班次查询、快捷时间等处频繁调用，且涉及 NodaTime 日期运算。
/// </summary>
[MemoryDiagnoser]
public class ShiftConfigResolveRangeBenchmark
{
    // 夜班 20:00-次日 08:00（跨天）
    private readonly ShiftConfig _night =
        new() { Name = "夜班", StartTime = new(20, 0, 0), EndTime = new(8, 0, 0) };
    // 白班 08:00-20:00（不跨天）
    private readonly ShiftConfig _day =
        new() { Name = "白班", StartTime = new(8, 0, 0), EndTime = new(20, 0, 0) };

    // 三个代表性时刻：晚间段、凌晨段、间隙段
    private static readonly DateTime[] Refs =
    {
        new(2026, 7, 23, 23, 0, 0),
        new(2026, 7, 23, 2, 0, 0),
        new(2026, 7, 23, 12, 0, 0)
    };

    [Benchmark]
    public (DateTime Start, DateTime End) ResolveNight() => _night.ResolveRange(Refs[0]);

    [Benchmark]
    public DateTime GetCurrentStartNight() => _night.GetCurrentStart(Refs[1]);

    [Benchmark]
    public (DateTime Start, DateTime End) ResolveDay() => _day.ResolveRange(Refs[2]);
}
