using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using MainAPP.Models;

namespace MainAPP.Converters;

/// <summary>
/// 报警级别转画刷：High=红, Medium=橙, Low=黄。
/// 用于实时故障卡片左侧色条与图标颜色。
/// </summary>
public class AlarmLevelToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush HighBrush = new(Color.FromRgb(0xF8, 0x71, 0x71));
    private static readonly SolidColorBrush MediumBrush = new(Color.FromRgb(0xFB, 0xBF, 0x24));
    private static readonly SolidColorBrush LowBrush = new(Color.FromRgb(0xFB, 0x92, 0x3C));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is AlarmLevel level)
        {
            return level switch
            {
                AlarmLevel.High => HighBrush,
                AlarmLevel.Medium => MediumBrush,
                AlarmLevel.Low => LowBrush,
                _ => Brushes.Gray,
            };
        }
        return Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
