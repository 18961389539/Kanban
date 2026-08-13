using System.Globalization;
using System.Text.Json;
using System.Windows.Data;

namespace MainAPP.Converters;

/// <summary>
/// 将 JSON 字符串格式化为缩进样式，用于审计详情面板的前后值展示。
/// 非法 JSON 原样返回（保证内容可见）；null/空白返回 null（配合 TargetNullValue 显示占位符）。
/// </summary>
public class JsonPrettyPrintConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s || string.IsNullOrWhiteSpace(s))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(s);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            // 历史数据可能是截断/手工编辑的非标准 JSON：原样展示，不吞内容
            return s;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
