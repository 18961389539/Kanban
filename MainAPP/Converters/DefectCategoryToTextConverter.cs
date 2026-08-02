using System.Globalization;
using System.Windows.Data;
using MainAPP.Models;

namespace MainAPP.Converters;

/// <summary>
/// 缺陷类别转中文文本：Appearance=外观, Dimension=尺寸, Function=功能, Packaging=包装, Other=其他。
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
                DefectCategory.Appearance => "外观",
                DefectCategory.Dimension => "尺寸",
                DefectCategory.Function => "功能",
                DefectCategory.Packaging => "包装",
                DefectCategory.Other => "其他",
                _ => category.ToString(),
            };
        }
        return string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
