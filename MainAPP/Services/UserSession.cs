using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Kanban.Collector.Core.Models;

namespace MainAPP.Services;

/// <summary>
/// 用户会话（DI 单例）：持有当前登录用户，驱动全应用权限状态。
/// 登录前 CurrentUser 为 null（未登录态）；登录后持有用户对象直到退出。
/// 属性变更通知供 ViewModel 绑定刷新导航项/按钮可用性。
/// </summary>
public partial class UserSession : ObservableObject
{
    /// <summary>当前登录用户（null = 未登录）。</summary>
    [ObservableProperty]
    private User? _currentUser;

    /// <summary>是否已登录。</summary>
    public bool IsLoggedIn => CurrentUser != null;

    /// <summary>当前角色（未登录时视为 Operator，仅可看展示页）。</summary>
    public UserRole CurrentRole => CurrentUser?.Role ?? UserRole.Operator;

    /// <summary>当前用户显示名（未登录时返回空串）。</summary>
    public string CurrentUserDisplay => CurrentUser?.DisplayLabel ?? string.Empty;

    /// <summary>是否工程师或以上。</summary>
    public bool IsEngineerOrAbove => CurrentRole.IsEngineerOrAbove();

    /// <summary>是否管理员。</summary>
    public bool IsAdmin => CurrentRole.IsAdmin();

    /// <summary>登录：设置当前用户并刷新派生属性。</summary>
    public void Login(User user)
    {
        CurrentUser = user;
        OnPropertyChanged(nameof(IsLoggedIn));
        OnPropertyChanged(nameof(CurrentRole));
        OnPropertyChanged(nameof(CurrentUserDisplay));
        OnPropertyChanged(nameof(IsEngineerOrAbove));
        OnPropertyChanged(nameof(IsAdmin));
    }

    /// <summary>退出登录。</summary>
    public void Logout()
    {
        CurrentUser = null;
        OnPropertyChanged(nameof(IsLoggedIn));
        OnPropertyChanged(nameof(CurrentRole));
        OnPropertyChanged(nameof(CurrentUserDisplay));
        OnPropertyChanged(nameof(IsEngineerOrAbove));
        OnPropertyChanged(nameof(IsAdmin));
    }
}

/// <summary>
/// 授权服务：封装权限检查，供 ViewModel 的 CanExecute 使用。
/// </summary>
public interface IAuthorizationService
{
    /// <summary>当前会话是否满足所需最低角色。</summary>
    bool IsInRole(UserRole required);

    /// <summary>是否工程师或以上（设备配置/导入导出/样本数据）。</summary>
    bool CanManageDevices { get; }

    /// <summary>是否管理员（用户管理/系统设置/备份恢复）。</summary>
    bool CanManageSystem { get; }
}

public class AuthorizationService : IAuthorizationService
{
    private readonly UserSession _session;

    public AuthorizationService(UserSession session)
    {
        _session = session;
        _session.PropertyChanged += OnSessionChanged;
    }

    public bool IsInRole(UserRole required) => _session.CurrentRole.AtLeast(required);
    public bool CanManageDevices => _session.IsEngineerOrAbove;
    public bool CanManageSystem => _session.IsAdmin;

    public event EventHandler? AuthorizationChanged;

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 会话角色变更时通知订阅者刷新 CanExecute
        if (e.PropertyName == nameof(UserSession.CurrentUser) ||
            e.PropertyName == nameof(UserSession.CurrentRole))
        {
            AuthorizationChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
