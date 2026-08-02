using System.Globalization;
using System.Windows.Controls;

namespace MainAPP.Converters;

/// <summary>
/// 整数范围校验规则
/// </summary>
public class IntegerRangeValidationRule : ValidationRule
{
    public int MinValue { get; set; } = int.MinValue;
    public int MaxValue { get; set; } = int.MaxValue;
    public string FieldName { get; set; } = "字段";

    public override ValidationResult Validate(object? value, CultureInfo cultureInfo)
    {
        var text = value as string;
        if (string.IsNullOrWhiteSpace(text))
            return new ValidationResult(false, $"{FieldName}不能为空");

        if (!int.TryParse(text, NumberStyles.Integer, cultureInfo, out var num))
            return new ValidationResult(false, $"{FieldName}必须是整数");

        if (num < MinValue || num > MaxValue)
            return new ValidationResult(false, $"{FieldName}必须在 {MinValue}-{MaxValue} 之间");

        return ValidationResult.ValidResult;
    }
}
