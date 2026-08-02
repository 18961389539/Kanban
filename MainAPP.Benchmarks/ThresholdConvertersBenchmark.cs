using System.Globalization;
using BenchmarkDotNet.Attributes;
using MainAPP.Converters;

namespace MainAPP.Benchmarks;

/// <summary>
/// OEE/合格率阈值转换器性能基准。
/// 这些转换器在仪表板每 3 秒刷新时为每个设备卡片的 OEE/合格率列调用一次，
/// 是采集刷新热路径上的高频调用。
/// 注意：转换器静态构造依赖 Application.Current.FindResource，Benchmark 进程中
/// Application.Current 为 null，回退到 Brushes.Green/Orange/Red，不影响 Convert 方法本身的性能度量。
/// </summary>
[MemoryDiagnoser]
public class ThresholdConvertersBenchmark
{
    private readonly OeeThresholdConverter _oeeConverter = new();
    private readonly RatioThresholdConverter _ratioConverter = new();
    private readonly RatioToWidthConverter _widthConverter = new();

    // 模拟一轮刷新中所有设备的 OEE 值（5 台设备 × 4 个典型值）
    private readonly double[] _oeeValues = { 0.95, 0.75, 0.50, 0.0, 0.88, 0.62, 0.30, 0.99, 1.0, 0.45 };
    private readonly double[] _ratioValues = { 0.98, 0.85, 0.65, 0.0, 0.91, 0.72, 0.55, 0.99, 1.0, 0.40 };

    [Benchmark(Description = "OEE 阈值转换 ×10（模拟一轮 5 台设备刷新）")]
    public void OeeThresholdConverter_Convert_Batch10()
    {
        foreach (var value in _oeeValues)
            _oeeConverter.Convert(value, typeof(object), null, CultureInfo.InvariantCulture);
    }

    [Benchmark(Description = "合格率阈值转换 ×10")]
    public void RatioThresholdConverter_Convert_Batch10()
    {
        foreach (var value in _ratioValues)
            _ratioConverter.Convert(value, typeof(object), null, CultureInfo.InvariantCulture);
    }

    [Benchmark(Description = "比例转宽度 ×10")]
    public void RatioToWidthConverter_Convert_Batch10()
    {
        foreach (var value in _ratioValues)
            _widthConverter.Convert(value, typeof(double), "200", CultureInfo.InvariantCulture);
    }

    [Benchmark(Description = "单次 OEE 阈值转换")]
    public object OeeThresholdConverter_Single()
    {
        return _oeeConverter.Convert(0.75, typeof(object), null, CultureInfo.InvariantCulture);
    }

    [Benchmark(Description = "单次合格率阈值转换")]
    public object RatioThresholdConverter_Single()
    {
        return _ratioConverter.Convert(0.85, typeof(object), null, CultureInfo.InvariantCulture);
    }
}
