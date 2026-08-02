using System.Globalization;
using System.Windows.Data;

namespace MainAPP.Converters;

/// <summary>
/// 通用数值阈值比较 Converter。
/// ConverterParameter 形式："&gt;=5"、"&gt;5"、"&lt;=0.6"、"&lt;0.6"、"==10"、"&gt;=5"。
/// 比较成立返回 true，否则 false；解析失败返回 false。
/// 用于 DataGrid 行高亮：在 DataTrigger 中 Value="True" 匹配命中时设置行背景。
/// </summary>
public class ComparisonConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == null || parameter is not string expr || string.IsNullOrWhiteSpace(expr))
            return false;

        // 支持的运算符（按长度降序匹配，避免 "<" 抢先匹配 "<="）
        string[] ops = { ">=", "<=", "==", "!=", ">", "<" };
        string? op = null;
        string? numPart = null;
        foreach (var o in ops)
        {
            if (expr.StartsWith(o))
            {
                op = o;
                numPart = expr.Substring(o.Length).Trim();
                break;
            }
        }
        if (op == null || numPart == null) return false;

        // 数值统一用 double 比较（int 字段也会被装箱为 int，先转 double）
        if (!double.TryParse(numPart, NumberStyles.Any, CultureInfo.InvariantCulture, out var threshold))
            return false;

        double current;
        try
        {
            current = System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return false;
        }

        return op switch
        {
            ">=" => current >= threshold,
            "<=" => current <= threshold,
            "==" => Math.Abs(current - threshold) < 1e-9,
            "!=" => Math.Abs(current - threshold) >= 1e-9,
            ">"  => current > threshold,
            "<"  => current < threshold,
            _ => false
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}
