using System.Globalization;
using System.Windows.Data;
using Kanban.Collector.Core.Models;
using MainAPP.Models;
using MainAPP.Resources;

namespace MainAPP.Converters;

/// <summary>
/// 缺陷严重度转文本：Critical=严重, Major=一般, Minor=轻微（文案走多语言资源）。用于列表项右侧级别徽标。
/// </summary>
public class DefectSeverityToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is DefectSeverity severity)
        {
            return severity switch
            {
                DefectSeverity.Critical => Strings.Severity_Critical,
                DefectSeverity.Major => Strings.Severity_Major,
                DefectSeverity.Minor => Strings.Severity_Minor,
                _ => string.Empty,
            };
        }
        return string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
