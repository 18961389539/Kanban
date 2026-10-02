using System.Globalization;
using System.Windows.Controls;
using MainAPP.Resources;

namespace MainAPP.Converters;

/// <summary>
/// 整数范围校验规则
/// </summary>
public class IntegerRangeValidationRule : ValidationRule
{
    public int MinValue { get; set; } = int.MinValue;
    public int MaxValue { get; set; } = int.MaxValue;
    public string FieldName { get; set; } = Strings.Msg_Field;

    public override ValidationResult Validate(object? value, CultureInfo cultureInfo)
    {
        var text = value as string;
        if (string.IsNullOrWhiteSpace(text))
            return new ValidationResult(false, string.Format(Strings.Prompt_MustEmpty, FieldName));

        if (!int.TryParse(text, NumberStyles.Integer, cultureInfo, out var num))
            return new ValidationResult(false, string.Format(Strings.Prompt_MustInteger, FieldName));

        if (num < MinValue || num > MaxValue)
            return new ValidationResult(false, string.Format(Strings.Prompt_MustBetween, FieldName, MinValue, MaxValue));

        return ValidationResult.ValidResult;
    }
}
