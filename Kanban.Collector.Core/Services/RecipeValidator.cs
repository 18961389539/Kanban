using System.Globalization;
using Kanban.Collector.Core.Localization;
using Kanban.Contracts.Enums;
using Kanban.Collector.Core.Models;

namespace Kanban.Collector.Core.Services;

/// <summary>
/// 配方校验（**全局唯一实现**，ADR-4 单源约定）：
/// MainAPP 编辑侧与 Collector 下发侧共用，避免两端口径分叉。
/// 覆盖：配方名、参数项、地址可解析性与类型匹配、值按类型解析、数值范围、地址唯一。
/// </summary>
public static class RecipeValidator
{
    /// <summary>字符串参数值最大长度（字符数）。S7 STRING 标准上限 254；同时保证下发读回长度 len+1 ≤ 255，不会触发 ushort 回绕。</summary>
    public const int MaxStringLength = 254;

    /// <summary>校验配方，返回错误消息列表；空列表 = 通过。
    /// <paramref name="existing"/> 提供配方库当前集合时追加"同机型配方名唯一"检查（null 跳过）。</summary>
    public static List<string> Validate(Recipe recipe, IReadOnlyCollection<Recipe>? existing = null)
    {
        var errors = new List<string>();
        if (recipe == null)
        {
            errors.Add(RecipeValidationMessages.RecipeNull);
            return errors;
        }

        if (string.IsNullOrWhiteSpace(recipe.Name))
            errors.Add(RecipeValidationMessages.RecipeNameEmpty);
        else if (existing != null && existing.Any(r =>
            !string.Equals(r.Id, recipe.Id, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.Name, recipe.Name, StringComparison.OrdinalIgnoreCase) &&
            string.Equals((r.MachineType ?? "").Trim(), (recipe.MachineType ?? "").Trim(), StringComparison.OrdinalIgnoreCase)))
            errors.Add(string.Format(RecipeValidationMessages.RecipeNameDuplicate, recipe.Name));

        if (recipe.Items.Count == 0)
        {
            errors.Add(RecipeValidationMessages.RecipeItemsEmpty);
            return errors;
        }

        var seenAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < recipe.Items.Count; i++)
        {
            var item = recipe.Items[i];
            var prefix = $"参数[{i + 1}]";

            if (string.IsNullOrWhiteSpace(item.ParamName))
                errors.Add(RecipeValidationMessages.RecipeParamNameEmpty);

            if (string.IsNullOrWhiteSpace(item.PlcAddress))
            {
                errors.Add($"{prefix}({item.ParamName}) {RecipeValidationMessages.RecipeAddressEmpty}");
                continue;
            }

            if (!seenAddresses.Add(item.PlcAddress))
                errors.Add($"{prefix}({item.ParamName}) {string.Format(RecipeValidationMessages.RecipeAddressDuplicate, item.PlcAddress)}");

            // 地址可解析且类型匹配（Int32/Float/UInt16/String → DWord；Bool → MBit）
            var parse = PlcAddressParser.Parse(item.PlcAddress);
            if (!parse.IsValid)
            {
                errors.Add($"{prefix}({item.ParamName}) {string.Format(RecipeValidationMessages.RecipeAddressUnresolvable, item.PlcAddress)}");
                continue;
            }
            var expectedType = item.DataType == PlcDataType.Bool ? PlcAddressType.MBit : PlcAddressType.DWord;
            if (parse.Type != expectedType)
                errors.Add($"{prefix}({item.ParamName}) {string.Format(RecipeValidationMessages.RecipeAddressTypeMismatch, item.DataType, expectedType)}");

            // 字符串参数值超长（写 PLC 可能超缓冲区，读回长度也可能截断）
            if (item.DataType == PlcDataType.String && (item.Value?.Length ?? 0) > MaxStringLength)
                errors.Add($"{prefix}({item.ParamName}) {string.Format(RecipeValidationMessages.RecipeStringTooLong, MaxStringLength)}");

            // 值按类型解析 + 范围校验
            if (!TryParseValue(item, out double? numeric))
            {
                errors.Add($"{prefix}({item.ParamName}) {string.Format(RecipeValidationMessages.RecipeValueInvalid, item.Value, DataTypeText(item.DataType))}");
                continue;
            }
            if (item.Min is { } minValue && item.Max is { } maxValue && minValue > maxValue)
                errors.Add($"{prefix}({item.ParamName}) {string.Format(RecipeValidationMessages.RecipeMinMaxInverted, minValue, maxValue)}");
            if (numeric is { } n)
            {
                if (item.Min is { } min && n < min)
                    errors.Add($"{prefix}({item.ParamName}) {string.Format(RecipeValidationMessages.RecipeBelowMin, n, min)}");
                if (item.Max is { } max && n > max)
                    errors.Add($"{prefix}({item.ParamName}) {string.Format(RecipeValidationMessages.RecipeAboveMax, n, max)}");
            }
        }

        return errors;
    }

    /// <summary>
    /// 按 <see cref="PlcDataType"/> 解析参数值。返回数值型（Int32/Float/UInt16）的数值用于范围校验；
    /// Bool/String 解析成功但返回 null（无范围概念）。
    /// </summary>
    public static bool TryParseValue(RecipeItem item, out double? numeric)
    {
        numeric = null;
        switch (item.DataType)
        {
            case PlcDataType.Int32:
                if (int.TryParse(item.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) { numeric = i; return true; }
                return false;
            case PlcDataType.Float:
                if (float.TryParse(item.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) { numeric = f; return true; }
                return false;
            case PlcDataType.UInt16:
                if (ushort.TryParse(item.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var u)) { numeric = u; return true; }
                return false;
            case PlcDataType.Bool:
                return bool.TryParse(item.Value, out _) || item.Value is "0" or "1";
            case PlcDataType.String:
                return true;
            default:
                return false;
        }
    }

    /// <summary>解析为写入 PLC 的实际值（按类型转成驱动方法参数）。解析 <see cref="RecipeItem.Value"/>（配方目标值）。</summary>
    public static bool TryConvert(RecipeItem item, out int intValue, out float floatValue, out bool boolValue, out string stringValue, out ushort uint16Value)
        => TryConvert(item, item.Value, out intValue, out floatValue, out boolValue, out stringValue, out uint16Value);

    /// <summary>
    /// 解析任意字符串值为写入 PLC 的实际值（按 <see cref="RecipeItem.DataType"/> 转成驱动方法参数）。
    /// 供回滚使用：<paramref name="value"/> 为写入前读回的备份值（格式与 <see cref="TryRead"/> 输出对齐：
    /// 数值 InvariantCulture、Bool "1"/"0"、String 原文），而非配方目标值。
    /// </summary>
    public static bool TryConvert(RecipeItem item, string value, out int intValue, out float floatValue, out bool boolValue, out string stringValue, out ushort uint16Value)
    {
        intValue = 0; floatValue = 0; boolValue = false; stringValue = ""; uint16Value = 0;
        switch (item.DataType)
        {
            case PlcDataType.Int32:
                return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out intValue);
            case PlcDataType.Float:
                return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out floatValue);
            case PlcDataType.UInt16:
                return ushort.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint16Value);
            case PlcDataType.Bool:
                if (bool.TryParse(value, out var b)) { boolValue = b; return true; }
                if (value is "0") { boolValue = false; return true; }
                if (value is "1") { boolValue = true; return true; }
                return false;
            case PlcDataType.String:
                stringValue = value;
                return true;
            default:
                return false;
        }
    }

    private static string DataTypeText(PlcDataType type) => type switch
    {
        PlcDataType.Int32 => RecipeValidationMessages.RecipeTypeInt32,
        PlcDataType.Float => RecipeValidationMessages.RecipeTypeFloat,
        PlcDataType.Bool => RecipeValidationMessages.RecipeTypeBool,
        PlcDataType.String => RecipeValidationMessages.RecipeTypeString,
        PlcDataType.UInt16 => RecipeValidationMessages.RecipeTypeUInt16,
        _ => type.ToString(),
    };
}
