using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Kanban.Core.Models;
using MainAPP.Models;

namespace MainAPP.Converters;

/// <summary>
/// 阈值着色转换器的共享画刷缓存基类。
/// 画刷在首次 Convert 时通过 <see cref="Application.TryFindResource"/> 懒加载并缓存，
/// 避免静态初始化器在资源字典尚未完全加载时调用 <see cref="Application.FindResource"/>
/// （资源缺失时抛 <see cref="ResourceReferenceKeyNotFoundException"/> → 触发 TypeInitializationException → 整个 Converter 类永久不可用）。
/// TryFindResource 返回 null 时回退到 <see cref="Brushes"/> 内置画刷，保证 UI 可用。
/// </summary>
public abstract class ThresholdBrushCacheBase
{
    private Brush? _green;
    private Brush? _yellow;
    private Brush? _red;
    private bool _loaded;

    private void Load()
    {
        if (_loaded) return;
        _loaded = true;
        var app = Application.Current;
        // TryFindResource 在资源缺失时返回 null（不抛异常），FindResource 会抛异常。
        _green = app?.TryFindResource("SuccessBrush") as Brush ?? Brushes.Green;
        _yellow = app?.TryFindResource("WarningBrush") as Brush ?? Brushes.Orange;
        _red = app?.TryFindResource("DangerBrush") as Brush ?? Brushes.Red;
    }

    protected Brush Green => LoadIfNeeded()._green!;
    protected Brush Yellow => LoadIfNeeded()._yellow!;
    protected Brush Red => LoadIfNeeded()._red!;

    private ThresholdBrushCacheBase LoadIfNeeded()
    {
        Load();
        return this;
    }
}

/// <summary>
/// OEE 阈值着色：>=OeeGood → SuccessBrush, >=OeeWarning → WarningBrush, else → DangerBrush。
/// </summary>
public class OeeThresholdConverter : ThresholdBrushCacheBase, IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double d)
            return d >= KpiThresholds.OeeGood ? Green : d >= KpiThresholds.OeeWarning ? Yellow : Red;
        return Green;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 达成率/速度比率阈值着色：>=AchievementGood → SuccessBrush, >=AchievementWarning → WarningBrush, else → DangerBrush。
/// </summary>
public class RatioThresholdConverter : ThresholdBrushCacheBase, IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double d)
            return d >= KpiThresholds.AchievementGood ? Green : d >= KpiThresholds.AchievementWarning ? Yellow : Red;
        return Green;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 比例值 → 像素宽度：ratio * ConverterParameter → double（用于进度条 Width 绑定）。
/// </summary>
public class RatioToWidthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var ratio = value is double d ? d : 0;
        var maxWidth = parameter is string s && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var w) ? w : 100;
        return Math.Max(0, ratio * maxWidth);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 反向比率阈值着色：用于"低=好"的指标（不良率/不良数）。
/// >=AchievementGood → DangerBrush（高不良率=红）, >=AchievementWarning → WarningBrush, else → SuccessBrush。
/// 与 OeeThresholdConverter 方向相反，避免不良率高时被染绿。
/// </summary>
public class InverseRatioThresholdConverter : ThresholdBrushCacheBase, IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double d)
            return d >= KpiThresholds.AchievementGood ? Red : d >= KpiThresholds.AchievementWarning ? Yellow : Green;
        return Green;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
