using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace MainAPP.Converters;

/// <summary>
/// 将进度值（0~1）转换为 Ellipse 的 StrokeDashArray，用于绘制环形进度条。
/// ConverterParameter 为圆周长（2πr），默认按 r=37 计算（StrokeThickness=8，圆 90×90 时半径 37）。
/// 进度 0.5 时返回 [0.5*circ, 0.5*circ]：实线半圈 + 空白半圈，配合 RotateTransform(-90) 从顶部顺时针绘制。
/// </summary>
public class ProgressToStrokeDashArrayConverter : IValueConverter
{
    private const double DefaultCircumference = 2 * Math.PI * 37; // ≈ 232.48

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double progress) return new DoubleCollection();
        if (parameter is string s && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var circ) && circ > 0)
        {
            // 使用参数指定周长
        }
        else
        {
            circ = DefaultCircumference;
        }

        progress = Math.Clamp(progress, 0, 1);
        return new DoubleCollection { progress * circ, (1 - progress) * circ };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 将比例值（0~1）转换为 GridLength(Star)，用于堆叠条形图按比例分配列宽。
/// 值为 0 时返回 GridLength(0)，避免空白列占据星形空间。
/// </summary>
public class RatioToGridLengthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double d && d > 0) return new GridLength(d, GridUnitType.Star);
        return new GridLength(0);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
