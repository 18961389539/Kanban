using System.Globalization;
using System.Windows.Data;
using Kanban.Core.Models;
using MainAPP.Models;

namespace MainAPP.Converters;

/// <summary>
/// 缺陷严重度转中文文本：Critical=严重, Major=一般, Minor=轻微。用于列表项右侧级别徽标。
/// </summary>
public class DefectSeverityToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is DefectSeverity severity)
        {
            return severity switch
            {
                DefectSeverity.Critical => "严重",
                DefectSeverity.Major => "一般",
                DefectSeverity.Minor => "轻微",
                _ => string.Empty,
            };
        }
        return string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
