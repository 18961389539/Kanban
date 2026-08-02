using BenchmarkDotNet.Attributes;
using Kanban.Core.Services;
using MainAPP.Services;

namespace MainAPP.Benchmarks;

/// <summary>
/// OEE 计算路径的性能基准
/// </summary>
[MemoryDiagnoser]
public class OeeCalculationBenchmark
{
    [Params(100, 1000, 10000)]
    public int TotalOk { get; set; }

    [Params(5, 50)]
    public int TotalNg { get; set; }

    private const int TargetCycle = 60; // 60个/小时
    private const double RunTime = 3600; // 1小时

    [Benchmark]
    public double QualityRate() => OeeCalculator.CalculateQualityRate(TotalOk, TotalNg);

    [Benchmark]
    public double PerformanceRate() => OeeCalculator.CalculatePerformanceRate(TotalOk, TotalNg, TargetCycle, RunTime);

    [Benchmark]
    public double AvailabilityRate() => OeeCalculator.CalculateAvailabilityRate(RunTime, 300);

    [Benchmark]
    public double FullOee() => OeeCalculator.CalculateOee(0.99, 0.88, 0.96);
}
