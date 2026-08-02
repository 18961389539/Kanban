using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;

namespace MainAPP.Converters;

/// <summary>
/// 安全读取校验错误集合中的首条错误消息文本。
/// 用于替换 HandyControl 默认 Validation.ErrorTemplate 中直接索引
/// <c>(Validation.Errors)[0].ErrorContent</c> 的写法——
/// 当错误集合为空（如校验被清除的过渡瞬间）时，[0] 会抛
/// ArgumentOutOfRangeException，在调试输出刷出 "System.Windows.Data Error: 17" 噪音。
/// 本转换器只在 Count &gt; 0 时取 [0]，否则返回空串，彻底消除该异常。
/// </summary>
public class ValidationErrorsToMessageConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ReadOnlyObservableCollection<ValidationError> errors && errors.Count > 0)
        {
            var content = errors[0].ErrorContent;
            return content?.ToString() ?? string.Empty;
        }
        return string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}
