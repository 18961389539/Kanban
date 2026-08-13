using System.Globalization;
using BenchmarkDotNet.Attributes;
using MainAPP.Converters;

namespace MainAPP.Benchmarks;

/// <summary>
/// 通用转换器性能基准：ComparisonConverter（DataGrid 行高亮）、
/// NullToVisibilityConverter、TimeSpanToTimeStringConverter（班次时间显示）。
/// 这些转换器在 DataGrid 渲染和 Tab 切换热路径上高频调用。
/// ComparisonConverter 每次调用都做字符串解析（运算符提取 + double.TryParse + Convert.ToDouble），
/// 是潜在的渲染瓶颈，重点度量。
/// </summary>
[MemoryDiagnoser]
public class ConvertersBenchmark
{
    private readonly ComparisonConverter _comparisonConverter = new();
    private readonly NullToVisibilityConverter _nullToVisibilityConverter = new();
    private readonly TimeSpanToTimeStringConverter _timeSpanConverter = new();

    // 模拟 DataGrid 100 行渲染时每行调用一次 ComparisonConverter
    private readonly int[] _rowValues = Enumerable.Range(0, 100).ToArray();

    [Params(">=5", "<=0.6", "==10", "!=0", ">100")]
    public string ComparisonExpression { get; set; } = ">=5";

    [Benchmark(Description = "ComparisonConverter ×100（DataGrid 100 行高亮）")]
    public void ComparisonConverter_Batch100()
    {
        foreach (var value in _rowValues)
            _comparisonConverter.Convert(value, typeof(bool), ComparisonExpression, CultureInfo.InvariantCulture);
    }

    [Benchmark(Description = "ComparisonConverter 单次")]
    public object ComparisonConverter_Single()
    {
        return _comparisonConverter.Convert(42, typeof(bool), ">=5", CultureInfo.InvariantCulture);
    }

    [Benchmark(Description = "ComparisonConverter 无效参数")]
    public object ComparisonConverter_InvalidParam()
    {
        return _comparisonConverter.Convert(42, typeof(bool), "not-an-expression", CultureInfo.InvariantCulture);
    }

    [Benchmark(Description = "NullToVisibilityConverter ×100")]
    public void NullToVisibilityConverter_Batch100()
    {
        for (int i = 0; i < 100; i++)
            _nullToVisibilityConverter.Convert(i % 2 == 0 ? (object?)i : null, typeof(object), null, CultureInfo.InvariantCulture);
    }

    [Benchmark(Description = "TimeSpanToTimeStringConverter ×100")]
    public void TimeSpanConverter_Batch100()
    {
        var ts = new System.TimeSpan(8, 30, 0);
        for (int i = 0; i < 100; i++)
            _timeSpanConverter.Convert(ts, typeof(string), null, CultureInfo.InvariantCulture);
    }

    [Benchmark(Description = "TimeSpanToTimeString 反向解析 ×100")]
    public void TimeSpanConverter_ConvertBack_Batch100()
    {
        for (int i = 0; i < 100; i++)
            _timeSpanConverter.ConvertBack("20:30", typeof(System.TimeSpan), null, CultureInfo.InvariantCulture);
    }
}
