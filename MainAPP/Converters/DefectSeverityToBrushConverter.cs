using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Kanban.Collector.Core.Models;
using MainAPP.Models;

namespace MainAPP.Converters;

/// <summary>
/// 缺陷严重度转画刷：Critical=红, Major=橙, Minor=黄。
/// 用于设备页缺陷列表项左侧色条与级别徽标颜色，配色语义与报警级别一致
/// （高/严重=红，中/一般=橙，低/轻微=黄），便于一眼区分严重程度。
/// 注意：Minor 不用绿——绿色在本应用中专属于「运行/正常」语义（StatusRunBrush），
/// 缺陷徽标显示绿色会被误读为「达标」。
/// </summary>
public class DefectSeverityToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush CriticalBrush = new(Color.FromRgb(0xF8, 0x71, 0x71));
    private static readonly SolidColorBrush MajorBrush = new(Color.FromRgb(0xFB, 0x92, 0x3C));
    private static readonly SolidColorBrush MinorBrush = new(Color.FromRgb(0xFB, 0xBF, 0x24));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is DefectSeverity severity)
        {
            return severity switch
            {
                DefectSeverity.Critical => CriticalBrush,
                DefectSeverity.Major => MajorBrush,
                DefectSeverity.Minor => MinorBrush,
                _ => Brushes.Gray,
            };
        }
        return Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
