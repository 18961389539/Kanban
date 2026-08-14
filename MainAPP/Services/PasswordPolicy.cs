using MainAPP.Resources;

namespace MainAPP.Services;

/// <summary>
/// 密码策略（添加用户 / 重置密码 / 首次强制改密统一入口）：
/// 最小长度 8；强度 = 长度 + 字符类型组合（0=弱 1=中 2=强）。
/// </summary>
public static class PasswordPolicy
{
    public const int MinLength = 8;

    /// <summary>长度校验（最小 8 位）。</summary>
    public static bool IsLongEnough(string password) => password.Length >= MinLength;

    /// <summary>强度等级：0=弱 1=中 2=强。</summary>
    public static int EvaluateStrength(string password)
    {
        if (password.Length < MinLength) return 0;
        var hasLetter = password.Any(char.IsLetter);
        var hasDigit = password.Any(char.IsDigit);
        var hasSymbol = password.Any(c => !char.IsLetterOrDigit(c));
        if (password.Length >= 12 && hasLetter && hasDigit && hasSymbol) return 2;
        if (hasLetter && hasDigit) return 1;
        return 0;
    }

    /// <summary>强度文案键（M369/M370/M371）。</summary>
    public static string StrengthText(int level) => level switch
    {
        1 => Strings.M370,
        2 => Strings.M371,
        _ => Strings.M369,
    };
}
