using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Attributes;
using MainAPP.Services;

namespace MainAPP.Benchmarks;

[MemoryDiagnoser]
public class ChartRenderingBenchmark
{
    private readonly List<(string Name, int Count)> _defects = new();
    private readonly List<(string Name, int Count)> _defects50 = new();

    public ChartRenderingBenchmark()
    {
        for (int i = 1; i <= 10; i++)
            _defects.Add(($"缺陷{i}", 100 - i * 5));

        for (int i = 1; i <= 50; i++)
            _defects50.Add(($"缺陷类型{i:D2}", 1000 - i));
    }

    [Benchmark]
    public object BuildOeePieChart()
    {
        return ChartService.BuildOeeChart(0.95, 0.90, 0.95, 0.95 * 0.90 * 0.95)!;
    }

    [Benchmark]
    public object BuildStatusPieChart()
    {
        return ChartService.BuildStatusPieChart(7200, 600, 300)!;
    }

    [Benchmark]
    public object BuildDefectBarChart_10Items()
    {
        return ChartService.BuildDefectBarChart(_defects)!;
    }

    [Benchmark]
    public object BuildDefectBarChart_50Items_Top10()
    {
        return ChartService.BuildDefectBarChart(_defects50)!;
    }

    [Benchmark]
    public object BuildDefectBarChart_5ZeroItems()
    {
        var zeroDefects = new[] { ("A", 0), ("B", 0), ("C", 0), ("D", 0), ("E", 0) };
        return ChartService.BuildDefectBarChart(zeroDefects)!; // returns null
    }
}
