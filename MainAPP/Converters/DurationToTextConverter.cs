using System;
using System.Globalization;
using System.Windows.Data;

namespace MainAPP.Converters;

/// <summary>
/// 时长自适应显示转换器（单位：小时，double）。
/// ≥ 1h → "X.Xh"（1 位小数）；&lt; 1h → "Xm"（整分钟）。
/// 解决 KPI 卡用 F1 格式把"刚触发的短报警"四舍五入成 0.0h 的误导问题。
/// </summary>
public class DurationToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var hours = value switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            decimal m => (double)m,
            _ => 0.0,
        };

        if (hours >= 1.0)
            return $"{hours:F1}h";
        var minutes = (int)Math.Round(hours * 60);
        return $"{minutes}m";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException("DurationToTextConverter does not support ConvertBack");
}
