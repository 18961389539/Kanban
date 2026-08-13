using System.Globalization;
using System.Resources;

namespace Kanban.Collector.Core.Localization;

/// <summary>
/// 配方校验/下发错误消息（被 Kanban.Collector 和 MainAPP 共同依赖的 Kanban.Collector.Core 使用）。
/// 默认值是中文以保留向后兼容；MainAPP / Collector 启动时根据用户界面语言调用 ApplyLanguage 整体覆盖。
/// en/ja 文案从 Resources/Messages.{en,ja}.resx 卫星程序集读取（与 <see cref="ValidationMessages"/> 同源）。
/// 模板中使用 {0}/{1} 占位符，由调用方通过 string.Format 填充。
/// </summary>
public static class RecipeValidationMessages
{
    private static readonly ResourceManager s_rm = new(
        "Kanban.Collector.Core.Resources.Messages",
        typeof(RecipeValidationMessages).Assembly);

    // ─── 默认中文模板（与 Messages.resx 中性资源一致，向后兼容）───
    public const string DefaultRecipeNull = "配方不能为空";
    public const string DefaultRecipeNameEmpty = "配方名称不能为空";
    public const string DefaultRecipeItemsEmpty = "配方至少需要一项参数";
    public const string DefaultRecipeParamNameEmpty = "参数名称不能为空";
    public const string DefaultRecipeAddressEmpty = "地址不能为空";
    public const string DefaultRecipeAddressDuplicate = "地址 {0} 与配方内其他参数重复";
    public const string DefaultRecipeAddressUnresolvable = "地址 {0} 无法解析";
    public const string DefaultRecipeAddressTypeMismatch = "地址类型不匹配：{0} 需要 {1} 区域";
    public const string DefaultRecipeValueInvalid = "值 \"{0}\" 不是有效的 {1}";
    public const string DefaultRecipeBelowMin = "值 {0} 低于下限 {1}";
    public const string DefaultRecipeAboveMax = "值 {0} 高于上限 {1}";
    public const string DefaultRecipeTypeInt32 = "32 位整数";
    public const string DefaultRecipeTypeFloat = "32 位浮点";
    public const string DefaultRecipeTypeBool = "位(0/1)";
    public const string DefaultRecipeTypeString = "字符串";
    public const string DefaultRecipeTypeUInt16 = "16 位整数";
    public const string DefaultRecipeDeviceOrRecipeEmpty = "设备或配方为空";
    public const string DefaultRecipePlcNotConnected = "PLC 未连接，无法下发配方";
    public const string DefaultRecipeReadBackupFailed = "读取地址 {0} 备份当前值失败";
    public const string DefaultRecipeBackupAbort = "下发前备份失败，已中止";
    public const string DefaultRecipeValueParseFailed = "值 \"{0}\" 无法解析";
    public const string DefaultRecipeValueParseAbort = "参数 {0} 值解析失败，已中止";
    public const string DefaultRecipeWriteFailed = "写入失败：{0}";
    public const string DefaultRecipeWriteRollback = "参数 {0} 写入失败，已回滚";
    public const string DefaultRecipeReadBackFailed = "读回校验失败";
    public const string DefaultRecipeReadBackRollback = "参数 {0} 读回失败，已回滚";
    public const string DefaultRecipeReadBackMismatch = "读回校验不一致（期望 {0}，读到 {1}）";
    public const string DefaultRecipeMismatchRollback = "参数 {0} 校验不一致，已回滚";
    public const string DefaultRecipeApplySuccess = "配方下发成功";
    public const string DefaultRecipeApplyException = "下发异常：{0}";
    public const string DefaultRecipeUnsupportedType = "不支持的数据类型 {0}";
    public const string DefaultRecipeWriteVerified = "写入并校验通过";
    public const string DefaultRecipeAddressReadonly = "地址 {0} 所属区域只读，不支持写入";
    public const string DefaultRecipeNameDuplicate = "配方名 \"{0}\" 在当前机型下已存在";
    public const string DefaultRecipeMinMaxInverted = "下限 {0} 大于上限 {1}";
    public const string DefaultRecipeStringTooLong = "字符串值超过最大长度 {0} 字符";
    public const string DefaultRecipeDeviceNotFound = "设备不存在：{0}";
    public const string DefaultRecipeNotFound = "配方不存在：{0}";
    public const string DefaultRecipeApplyCancelled = "下发已取消，已回滚";
    public const string DefaultRecipeWriteInProgress = "参数 {0} 写入中…";

    private static string s_recipeNull = DefaultRecipeNull;
    private static string s_recipeNameEmpty = DefaultRecipeNameEmpty;
    private static string s_recipeItemsEmpty = DefaultRecipeItemsEmpty;
    private static string s_recipeParamNameEmpty = DefaultRecipeParamNameEmpty;
    private static string s_recipeAddressEmpty = DefaultRecipeAddressEmpty;
    private static string s_recipeAddressDuplicate = DefaultRecipeAddressDuplicate;
    private static string s_recipeAddressUnresolvable = DefaultRecipeAddressUnresolvable;
    private static string s_recipeAddressTypeMismatch = DefaultRecipeAddressTypeMismatch;
    private static string s_recipeValueInvalid = DefaultRecipeValueInvalid;
    private static string s_recipeBelowMin = DefaultRecipeBelowMin;
    private static string s_recipeAboveMax = DefaultRecipeAboveMax;
    private static string s_recipeTypeInt32 = DefaultRecipeTypeInt32;
    private static string s_recipeTypeFloat = DefaultRecipeTypeFloat;
    private static string s_recipeTypeBool = DefaultRecipeTypeBool;
    private static string s_recipeTypeString = DefaultRecipeTypeString;
    private static string s_recipeTypeUInt16 = DefaultRecipeTypeUInt16;
    private static string s_recipeDeviceOrRecipeEmpty = DefaultRecipeDeviceOrRecipeEmpty;
    private static string s_recipePlcNotConnected = DefaultRecipePlcNotConnected;
    private static string s_recipeReadBackupFailed = DefaultRecipeReadBackupFailed;
    private static string s_recipeBackupAbort = DefaultRecipeBackupAbort;
    private static string s_recipeValueParseFailed = DefaultRecipeValueParseFailed;
    private static string s_recipeValueParseAbort = DefaultRecipeValueParseAbort;
    private static string s_recipeWriteFailed = DefaultRecipeWriteFailed;
    private static string s_recipeWriteRollback = DefaultRecipeWriteRollback;
    private static string s_recipeReadBackFailed = DefaultRecipeReadBackFailed;
    private static string s_recipeReadBackRollback = DefaultRecipeReadBackRollback;
    private static string s_recipeReadBackMismatch = DefaultRecipeReadBackMismatch;
    private static string s_recipeMismatchRollback = DefaultRecipeMismatchRollback;
    private static string s_recipeApplySuccess = DefaultRecipeApplySuccess;
    private static string s_recipeApplyException = DefaultRecipeApplyException;
    private static string s_recipeUnsupportedType = DefaultRecipeUnsupportedType;
    private static string s_recipeWriteVerified = DefaultRecipeWriteVerified;
    private static string s_recipeAddressReadonly = DefaultRecipeAddressReadonly;
    private static string s_recipeNameDuplicate = DefaultRecipeNameDuplicate;
    private static string s_recipeMinMaxInverted = DefaultRecipeMinMaxInverted;
    private static string s_recipeStringTooLong = DefaultRecipeStringTooLong;
    private static string s_recipeDeviceNotFound = DefaultRecipeDeviceNotFound;
    private static string s_recipeNotFound = DefaultRecipeNotFound;
    private static string s_recipeApplyCancelled = DefaultRecipeApplyCancelled;
    private static string s_recipeWriteInProgress = DefaultRecipeWriteInProgress;

    public static string RecipeNull => s_recipeNull;
    public static string RecipeNameEmpty => s_recipeNameEmpty;
    public static string RecipeItemsEmpty => s_recipeItemsEmpty;
    public static string RecipeParamNameEmpty => s_recipeParamNameEmpty;
    public static string RecipeAddressEmpty => s_recipeAddressEmpty;
    public static string RecipeAddressDuplicate => s_recipeAddressDuplicate;
    public static string RecipeAddressUnresolvable => s_recipeAddressUnresolvable;
    public static string RecipeAddressTypeMismatch => s_recipeAddressTypeMismatch;
    public static string RecipeValueInvalid => s_recipeValueInvalid;
    public static string RecipeBelowMin => s_recipeBelowMin;
    public static string RecipeAboveMax => s_recipeAboveMax;
    public static string RecipeTypeInt32 => s_recipeTypeInt32;
    public static string RecipeTypeFloat => s_recipeTypeFloat;
    public static string RecipeTypeBool => s_recipeTypeBool;
    public static string RecipeTypeString => s_recipeTypeString;
    public static string RecipeTypeUInt16 => s_recipeTypeUInt16;
    public static string RecipeDeviceOrRecipeEmpty => s_recipeDeviceOrRecipeEmpty;
    public static string RecipePlcNotConnected => s_recipePlcNotConnected;
    public static string RecipeReadBackupFailed => s_recipeReadBackupFailed;
    public static string RecipeBackupAbort => s_recipeBackupAbort;
    public static string RecipeValueParseFailed => s_recipeValueParseFailed;
    public static string RecipeValueParseAbort => s_recipeValueParseAbort;
    public static string RecipeWriteFailed => s_recipeWriteFailed;
    public static string RecipeWriteRollback => s_recipeWriteRollback;
    public static string RecipeReadBackFailed => s_recipeReadBackFailed;
    public static string RecipeReadBackRollback => s_recipeReadBackRollback;
    public static string RecipeReadBackMismatch => s_recipeReadBackMismatch;
    public static string RecipeMismatchRollback => s_recipeMismatchRollback;
    public static string RecipeApplySuccess => s_recipeApplySuccess;
    public static string RecipeApplyException => s_recipeApplyException;
    public static string RecipeUnsupportedType => s_recipeUnsupportedType;
    public static string RecipeWriteVerified => s_recipeWriteVerified;
    public static string RecipeAddressReadonly => s_recipeAddressReadonly;
    public static string RecipeNameDuplicate => s_recipeNameDuplicate;
    public static string RecipeMinMaxInverted => s_recipeMinMaxInverted;
    public static string RecipeStringTooLong => s_recipeStringTooLong;
    public static string RecipeDeviceNotFound => s_recipeDeviceNotFound;
    public static string RecipeNotFound => s_recipeNotFound;
    public static string RecipeApplyCancelled => s_recipeApplyCancelled;
    public static string RecipeWriteInProgress => s_recipeWriteInProgress;

    /// <summary>
    /// 简单语言预设：根据三语设置同时覆盖全部文案。传入 null 还原默认中文。
    /// en/ja 文案从 Messages.{en,ja}.resx 卫星程序集读取，避免硬编码副本。
    /// 与 <see cref="ValidationMessages.ApplyLanguage"/> 行为一致。
    /// </summary>
    public static void ApplyLanguage(string? langCode)
    {
        if (langCode is null)
        {
            RestoreDefaults();
            return;
        }

        CultureInfo culture;
        try
        {
            culture = CultureInfo.GetCultureInfo(langCode);
        }
        catch (CultureNotFoundException)
        {
            RestoreDefaults();
            return;
        }

        s_recipeNull = s_rm.GetString("RecipeNull", culture) ?? DefaultRecipeNull;
        s_recipeNameEmpty = s_rm.GetString("RecipeNameEmpty", culture) ?? DefaultRecipeNameEmpty;
        s_recipeItemsEmpty = s_rm.GetString("RecipeItemsEmpty", culture) ?? DefaultRecipeItemsEmpty;
        s_recipeParamNameEmpty = s_rm.GetString("RecipeParamNameEmpty", culture) ?? DefaultRecipeParamNameEmpty;
        s_recipeAddressEmpty = s_rm.GetString("RecipeAddressEmpty", culture) ?? DefaultRecipeAddressEmpty;
        s_recipeAddressDuplicate = s_rm.GetString("RecipeAddressDuplicate", culture) ?? DefaultRecipeAddressDuplicate;
        s_recipeAddressUnresolvable = s_rm.GetString("RecipeAddressUnresolvable", culture) ?? DefaultRecipeAddressUnresolvable;
        s_recipeAddressTypeMismatch = s_rm.GetString("RecipeAddressTypeMismatch", culture) ?? DefaultRecipeAddressTypeMismatch;
        s_recipeValueInvalid = s_rm.GetString("RecipeValueInvalid", culture) ?? DefaultRecipeValueInvalid;
        s_recipeBelowMin = s_rm.GetString("RecipeBelowMin", culture) ?? DefaultRecipeBelowMin;
        s_recipeAboveMax = s_rm.GetString("RecipeAboveMax", culture) ?? DefaultRecipeAboveMax;
        s_recipeTypeInt32 = s_rm.GetString("RecipeTypeInt32", culture) ?? DefaultRecipeTypeInt32;
        s_recipeTypeFloat = s_rm.GetString("RecipeTypeFloat", culture) ?? DefaultRecipeTypeFloat;
        s_recipeTypeBool = s_rm.GetString("RecipeTypeBool", culture) ?? DefaultRecipeTypeBool;
        s_recipeTypeString = s_rm.GetString("RecipeTypeString", culture) ?? DefaultRecipeTypeString;
        s_recipeTypeUInt16 = s_rm.GetString("RecipeTypeUInt16", culture) ?? DefaultRecipeTypeUInt16;
        s_recipeDeviceOrRecipeEmpty = s_rm.GetString("RecipeDeviceOrRecipeEmpty", culture) ?? DefaultRecipeDeviceOrRecipeEmpty;
        s_recipePlcNotConnected = s_rm.GetString("RecipePlcNotConnected", culture) ?? DefaultRecipePlcNotConnected;
        s_recipeReadBackupFailed = s_rm.GetString("RecipeReadBackupFailed", culture) ?? DefaultRecipeReadBackupFailed;
        s_recipeBackupAbort = s_rm.GetString("RecipeBackupAbort", culture) ?? DefaultRecipeBackupAbort;
        s_recipeValueParseFailed = s_rm.GetString("RecipeValueParseFailed", culture) ?? DefaultRecipeValueParseFailed;
        s_recipeValueParseAbort = s_rm.GetString("RecipeValueParseAbort", culture) ?? DefaultRecipeValueParseAbort;
        s_recipeWriteFailed = s_rm.GetString("RecipeWriteFailed", culture) ?? DefaultRecipeWriteFailed;
        s_recipeWriteRollback = s_rm.GetString("RecipeWriteRollback", culture) ?? DefaultRecipeWriteRollback;
        s_recipeReadBackFailed = s_rm.GetString("RecipeReadBackFailed", culture) ?? DefaultRecipeReadBackFailed;
        s_recipeReadBackRollback = s_rm.GetString("RecipeReadBackRollback", culture) ?? DefaultRecipeReadBackRollback;
        s_recipeReadBackMismatch = s_rm.GetString("RecipeReadBackMismatch", culture) ?? DefaultRecipeReadBackMismatch;
        s_recipeMismatchRollback = s_rm.GetString("RecipeMismatchRollback", culture) ?? DefaultRecipeMismatchRollback;
        s_recipeApplySuccess = s_rm.GetString("RecipeApplySuccess", culture) ?? DefaultRecipeApplySuccess;
        s_recipeApplyException = s_rm.GetString("RecipeApplyException", culture) ?? DefaultRecipeApplyException;
        s_recipeUnsupportedType = s_rm.GetString("RecipeUnsupportedType", culture) ?? DefaultRecipeUnsupportedType;
        s_recipeAddressReadonly = s_rm.GetString("RecipeAddressReadonly", culture) ?? DefaultRecipeAddressReadonly;
        s_recipeNameDuplicate = s_rm.GetString("RecipeNameDuplicate", culture) ?? DefaultRecipeNameDuplicate;
        s_recipeMinMaxInverted = s_rm.GetString("RecipeMinMaxInverted", culture) ?? DefaultRecipeMinMaxInverted;
        s_recipeStringTooLong = s_rm.GetString("RecipeStringTooLong", culture) ?? DefaultRecipeStringTooLong;
        s_recipeDeviceNotFound = s_rm.GetString("RecipeDeviceNotFound", culture) ?? DefaultRecipeDeviceNotFound;
        s_recipeNotFound = s_rm.GetString("RecipeNotFound", culture) ?? DefaultRecipeNotFound;
        s_recipeApplyCancelled = s_rm.GetString("RecipeApplyCancelled", culture) ?? DefaultRecipeApplyCancelled;
        s_recipeWriteInProgress = s_rm.GetString("RecipeWriteInProgress", culture) ?? DefaultRecipeWriteInProgress;
    }

    private static void RestoreDefaults()
    {
        s_recipeNull = DefaultRecipeNull;
        s_recipeNameEmpty = DefaultRecipeNameEmpty;
        s_recipeItemsEmpty = DefaultRecipeItemsEmpty;
        s_recipeParamNameEmpty = DefaultRecipeParamNameEmpty;
        s_recipeAddressEmpty = DefaultRecipeAddressEmpty;
        s_recipeAddressDuplicate = DefaultRecipeAddressDuplicate;
        s_recipeAddressUnresolvable = DefaultRecipeAddressUnresolvable;
        s_recipeAddressTypeMismatch = DefaultRecipeAddressTypeMismatch;
        s_recipeValueInvalid = DefaultRecipeValueInvalid;
        s_recipeBelowMin = DefaultRecipeBelowMin;
        s_recipeAboveMax = DefaultRecipeAboveMax;
        s_recipeTypeInt32 = DefaultRecipeTypeInt32;
        s_recipeTypeFloat = DefaultRecipeTypeFloat;
        s_recipeTypeBool = DefaultRecipeTypeBool;
        s_recipeTypeString = DefaultRecipeTypeString;
        s_recipeTypeUInt16 = DefaultRecipeTypeUInt16;
        s_recipeDeviceOrRecipeEmpty = DefaultRecipeDeviceOrRecipeEmpty;
        s_recipePlcNotConnected = DefaultRecipePlcNotConnected;
        s_recipeReadBackupFailed = DefaultRecipeReadBackupFailed;
        s_recipeBackupAbort = DefaultRecipeBackupAbort;
        s_recipeValueParseFailed = DefaultRecipeValueParseFailed;
        s_recipeValueParseAbort = DefaultRecipeValueParseAbort;
        s_recipeWriteFailed = DefaultRecipeWriteFailed;
        s_recipeWriteRollback = DefaultRecipeWriteRollback;
        s_recipeReadBackFailed = DefaultRecipeReadBackFailed;
        s_recipeReadBackRollback = DefaultRecipeReadBackRollback;
        s_recipeReadBackMismatch = DefaultRecipeReadBackMismatch;
        s_recipeMismatchRollback = DefaultRecipeMismatchRollback;
        s_recipeApplySuccess = DefaultRecipeApplySuccess;
        s_recipeApplyException = DefaultRecipeApplyException;
        s_recipeUnsupportedType = DefaultRecipeUnsupportedType;
        s_recipeWriteVerified = DefaultRecipeWriteVerified;
        s_recipeAddressReadonly = DefaultRecipeAddressReadonly;
        s_recipeNameDuplicate = DefaultRecipeNameDuplicate;
        s_recipeMinMaxInverted = DefaultRecipeMinMaxInverted;
        s_recipeStringTooLong = DefaultRecipeStringTooLong;
        s_recipeDeviceNotFound = DefaultRecipeDeviceNotFound;
        s_recipeNotFound = DefaultRecipeNotFound;
        s_recipeApplyCancelled = DefaultRecipeApplyCancelled;
        s_recipeWriteInProgress = DefaultRecipeWriteInProgress;
    }
}
