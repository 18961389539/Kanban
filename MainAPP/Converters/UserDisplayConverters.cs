using System;
using System.Globalization;
using System.Windows.Data;
using Kanban.Core.Models;
using Kanban.Core.Services;
using MainAPP.Resources;

namespace MainAPP.Converters;

/// <summary>角色徽章前景/边框色：Admin=主色、Engineer=成功绿、Operator=中性灰。</summary>
public sealed class UserRoleToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is UserRole role
            ? role switch
            {
                UserRole.Admin => AppBrushes.Primary,
                UserRole.Engineer => AppBrushes.Success,
                _ => AppBrushes.Thirdly,
            }
            : AppBrushes.Thirdly;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>角色徽章文本：管理员/工程师/操作员。</summary>
public sealed class UserRoleToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is UserRole role
            ? role switch
            {
                UserRole.Admin => Strings.M334,
                UserRole.Engineer => Strings.M333,
                _ => Strings.M332,
            }
            : string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>账号锁定中 → Visible（解锁按钮显隐，User 直接转 Visibility）。</summary>
public sealed class UserIsLockedToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is User { LockedUntil: { } until } && until > DateTime.UtcNow
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>账号状态文本：锁定 &gt; 启用/禁用。</summary>
public sealed class UserStatusToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not User user) return string.Empty;
        if (user.LockedUntil is { } until && until > DateTime.UtcNow) return Strings.M376;
        return user.IsActive ? Strings.M331 : Strings.M378;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>账号状态色：锁定=黄、启用=绿、禁用=灰。</summary>
public sealed class UserStatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not User user) return AppBrushes.Thirdly;
        if (user.LockedUntil is { } until && until > DateTime.UtcNow) return AppBrushes.Warning;
        return user.IsActive ? AppBrushes.Success : AppBrushes.Thirdly;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 账号安全风险文本（顿号拼接，多个风险并显）：默认口令 / 免密账号 / 从未登录（创建超 30 天）。
/// 返回空字符串 = 无风险。
/// </summary>
public sealed class UserRiskToTextConverter : IValueConverter
{
    public static readonly TimeSpan NeverLoginThreshold = TimeSpan.FromDays(30);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not User user) return string.Empty;
        var parts = new System.Collections.Generic.List<string>();
        if (!string.IsNullOrEmpty(user.PasswordHash)
            && (PasswordHasher.Verify(UserStore.DefaultAdminPassword, user.PasswordHash)
                || PasswordHasher.Verify(UserStore.DefaultEngineerPassword, user.PasswordHash)))
        {
            parts.Add(Strings.M373);
        }
        if (string.IsNullOrEmpty(user.PasswordHash)) parts.Add(Strings.M374);
        if (user.LastLoginAt is null && DateTime.UtcNow - user.CreatedAt > NeverLoginThreshold)
            parts.Add(Strings.M375);
        return parts.Count == 0 ? string.Empty : string.Join("、", parts);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>资源画刷访问助手（converter 内取 Brush 资源，避免重复解析）。</summary>
internal static class AppBrushes
{
    public static System.Windows.Media.Brush Primary => Get("PrimaryBrush");
    public static System.Windows.Media.Brush Success => Get("SuccessBrush");
    public static System.Windows.Media.Brush Warning => Get("WarningBrush");
    public static System.Windows.Media.Brush Thirdly => Get("ThirdlyTextBrush");

    private static System.Windows.Media.Brush Get(string key)
        => (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource(key);
}
