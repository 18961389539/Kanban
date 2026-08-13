using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanban.Core.Models;
using Kanban.Core.Services;
using MainAPP.Resources;
using MainAPP.Services;
using System.Windows;

namespace MainAPP.ViewModels;

/// <summary>
/// 用户管理 ViewModel：管理员可添加/删除用户、修改角色/启用状态、重置密码。
/// </summary>
public partial class UserManagerViewModel : ObservableObject
{
    private readonly UserStore _userStore;
    private readonly IDialogService _dialog;
    private readonly UserSession _session;
    private UserAuditSnapshot? _selectedUserBaseline;

    private sealed record UserAuditSnapshot(string Username, UserRole Role, string DisplayName, bool IsActive);

    /// <summary>可选角色列表（供下拉绑定）。</summary>
    public static IReadOnlyList<UserRole> AvailableRoles { get; } =
        [UserRole.Operator, UserRole.Engineer, UserRole.Admin];

    public ObservableCollection<User> Users { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteUserCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetPasswordCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpdateUserCommand))]
    private User? _selectedUser;

    /// <summary>新用户名（添加用户表单绑定）。</summary>
    [ObservableProperty]
    private string _newUsername = string.Empty;

    /// <summary>新用户显示名（添加用户表单绑定）。</summary>
    [ObservableProperty]
    private string _newDisplayName = string.Empty;

    /// <summary>新用户角色（添加用户表单绑定）。</summary>
    [ObservableProperty]
    private UserRole _newUserRole = UserRole.Operator;

    /// <summary>新用户密码（添加用户表单绑定，通过 code-behind 传入）。</summary>
    public string NewPassword { get; set; } = string.Empty;

    public UserManagerViewModel(UserStore userStore, IDialogService dialog, UserSession session)
    {
        _userStore = userStore;
        _dialog = dialog;
        _session = session;
        _userStore.UsersChanged += OnUsersChanged;
        RefreshUsers();
    }

    private void OnUsersChanged()
    {
        RefreshUsers();
    }

    private static UserAuditSnapshot Snapshot(User user)
        => new(user.Username, user.Role, user.DisplayName, user.IsActive);

    partial void OnSelectedUserChanged(User? value)
        => _selectedUserBaseline = value is null ? null : Snapshot(value);

    /// <summary>从 UserStore 刷新用户列表到 UI 集合。</summary>
    private void RefreshUsers()
    {
        Users.Clear();
        foreach (var u in _userStore.GetAll())
            Users.Add(u);
    }

    /// <summary>添加用户（密码通过参数传入）。</summary>
    [RelayCommand]
    private void AddUser(string? password)
    {
        if (string.IsNullOrWhiteSpace(NewUsername))
        {
            _dialog.NotifyWarning(Strings.M316);
            return;
        }
        if (string.IsNullOrEmpty(password))
        {
            _dialog.NotifyWarning(Strings.M317);
            return;
        }

        var user = new User
        {
            Username = NewUsername.Trim(),
            DisplayName = NewDisplayName.Trim(),
            Role = NewUserRole,
            PasswordHash = PasswordHasher.Hash(password),
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };

        if (!_userStore.Add(user))
        {
            _dialog.NotifyWarning(Strings.M326);
            return;
        }

        // 清空表单
        NewUsername = string.Empty;
        NewDisplayName = string.Empty;
        NewUserRole = UserRole.Operator;
        _dialog.NotifySuccess(string.Format(Strings.M322));
        AuditLog.Record("User.Add", "User", user.Username, detail: $"角色={user.Role}");
    }

    /// <summary>更新选中用户的角色/显示名/启用状态。</summary>
    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private void UpdateUser()
    {
        if (SelectedUser is null) return;
        var before = _selectedUserBaseline ?? Snapshot(SelectedUser);
        var after = Snapshot(SelectedUser);
        if (!_userStore.Update(SelectedUser.Username, SelectedUser.Role, SelectedUser.DisplayName, SelectedUser.IsActive))
        {
            _dialog.NotifyWarning(Strings.M327);
            return;
        }
        _selectedUserBaseline = after;
        _dialog.NotifySuccess(Strings.M320);
        AuditLog.Record("User.Update", "User", SelectedUser.Username,
            before: before,
            after: after);
    }

    /// <summary>删除选中用户。</summary>
    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private void DeleteUser()
    {
        if (SelectedUser is null) return;

        // 不能删除自己
        if (string.Equals(SelectedUser.Username, _session.CurrentUser?.Username, StringComparison.OrdinalIgnoreCase))
        {
            _dialog.NotifyWarning(Strings.M327);
            return;
        }

        var confirm = _dialog.Show(
            string.Format(Strings.F324, Strings.M323, SelectedUser.DisplayLabel),
            Strings.M323, MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        if (!_userStore.Remove(SelectedUser.Username))
        {
            _dialog.NotifyWarning(Strings.M327);
            return;
        }

        AuditLog.Record("User.Delete", "User", SelectedUser.Username);
        SelectedUser = null;
    }

    /// <summary>重置选中用户密码（新密码通过参数传入）。</summary>
    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private void ResetPassword(string? newPassword)
    {
        if (SelectedUser is null) return;
        if (string.IsNullOrEmpty(newPassword))
        {
            _dialog.NotifyWarning(Strings.M317);
            return;
        }

        _userStore.ResetPassword(SelectedUser.Username, newPassword);
        _dialog.NotifySuccess(Strings.M324);
        AuditLog.Record("User.ResetPassword", "User", SelectedUser.Username);
    }

    private bool CanEditSelected() => SelectedUser is not null;
}
