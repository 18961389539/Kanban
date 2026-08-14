using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanban.Core.Models;
using Kanban.Core.Services;
using MainAPP.Resources;
using MainAPP.Services;
using Serilog;

namespace MainAPP.ViewModels;

/// <summary>
/// 登录窗口 ViewModel：验证用户名密码，成功时设置 UserSession 并关闭窗口。
/// </summary>
public partial class LoginViewModel : ObservableObject
{
    private readonly UserStore _userStore;
    private readonly UserSession _session;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    private User? _selectedUser;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    /// <summary>登录成功时设为 true，供窗口关闭逻辑判断。</summary>
    public bool LoginSucceeded { get; private set; }

    /// <summary>登录成功但账号要求首次改密（MustChangePassword）——登录窗口在关闭前弹出改密对话框。</summary>
    public bool NeedsPasswordChange { get; private set; }

    /// <summary>可选用户名列表，绑定到下拉框供选择。</summary>
    public ObservableCollection<User> AvailableUsers { get; } = new();

    public LoginViewModel(UserStore userStore, UserSession session)
    {
        _userStore = userStore;
        _session = session;
        RefreshAvailableUsers();
    }

    /// <summary>从 UserStore 加载活跃用户列表到下拉框。</summary>
    public void RefreshAvailableUsers()
    {
        AvailableUsers.Clear();
        foreach (var user in _userStore.GetAll())
        {
            if (user.IsActive)
                AvailableUsers.Add(user);
        }
    }

    /// <summary>登录命令（绑定到登录按钮和回车键）。密码通过参数传入（PasswordBox 不支持双向绑定）。
    /// 免密账号（PasswordHash 为空）允许空密码登录。</summary>
    [RelayCommand(CanExecute = nameof(CanLogin))]
    private void Login(string? password)
    {
        ErrorMessage = string.Empty;
        if (SelectedUser is null)
        {
            ErrorMessage = Strings.M319;
            return;
        }

        User? user;
        try
        {
            user = _userStore.Authenticate(SelectedUser.Username, password ?? string.Empty);
        }
        // 登录收尾要写 users.json（LastLoginAt 持久化）；磁盘/权限/文件被占用（如 .bak 备份被
        // 安全软件短暂锁定）失败时不能把未处理异常抛到 UI 线程导致整个进程崩溃，降级为提示重试。
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error(ex, "登录持久化失败（{Username}）：users.json 写入被拒绝", SelectedUser.Username);
            AuditLog.Record("Auth.Login", "User", SelectedUser.Username, succeeded: false, detail: "persist-failed");
            ErrorMessage = Strings.M_LoginPersistFailed;
            return;
        }

        if (user is null)
        {
            AuditLog.Record("Auth.Login", "User", SelectedUser.Username, succeeded: false, detail: Strings.M_LoginFailed);
            // 失败原因区分：账号锁定 vs 密码错误（锁定给出剩余时间）
            var remaining = _userStore.GetLockRemaining(SelectedUser.Username);
            ErrorMessage = remaining is { } r
                ? string.Format(Strings.M366, Math.Ceiling(r.TotalMinutes))
                : Strings.M319;
            return;
        }

        _session.Login(user);
        AuditLog.Record("Auth.Login", "User", user.Username, succeeded: true);
        LoginSucceeded = true;
        NeedsPasswordChange = user.MustChangePassword;
    }

    /// <summary>完成首次强制改密：重置密码并清除标记（由登录窗口在改密对话框成功后调用）。</summary>
    public void CompletePasswordChange(string newPassword)
    {
        if (NeedsPasswordChange && _session.CurrentUser is { } user)
        {
            _userStore.ResetPassword(user.Username, newPassword);
            user.MustChangePassword = false;
            _userStore.Save();
            AuditLog.Record("Auth.PasswordChange", "User", user.Username);
        }
    }

    /// <summary>用户拒绝强制改密：退出本次登录（登录窗口据此关闭对话框）。</summary>
    public void LogoutAfterPasswordChangeDeclined()
    {
        _session.Logout();
        LoginSucceeded = false;
        NeedsPasswordChange = false;
    }

    private bool CanLogin() => SelectedUser is not null;
}
