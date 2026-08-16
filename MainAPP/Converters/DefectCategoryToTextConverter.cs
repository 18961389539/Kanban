using System.Globalization;
using System.Windows.Data;
using Kanban.Collector.Core.Models;
using MainAPP.Models;
using MainAPP.Resources;

namespace MainAPP.Converters;

/// <summary>
/// 缺陷类别转文本：Appearance=外观, Dimension=尺寸, Function=功能, Packaging=包装, Other=其他（文案走多语言资源）。
/// 用于设备页缺陷 ComboBox 选项与列表项显示。
/// </summary>
public class DefectCategoryToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is DefectCategory category)
        {
            return category switch
            {
                DefectCategory.Appearance => Strings.Defect_Appearance,
                DefectCategory.Dimension => Strings.Defect_Dimension,
                DefectCategory.Function => Strings.Defect_Function,
                DefectCategory.Packaging => Strings.Defect_Packaging,
                DefectCategory.Other => Strings.Defect_Other,
                _ => category.ToString(),
            };
        }
        return string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
