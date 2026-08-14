using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanban.Core.Models;
using Kanban.Core.Services;
using MainAPP.Resources;
using MainAPP.Services;

namespace MainAPP.ViewModels;

/// <summary>
/// 用户管理 ViewModel：管理员可添加/删除用户、修改角色/启用状态、重置密码、解锁账号。
/// - 编辑区基于副本（Edit* 属性），保存时才提交（列表行不被未保存输入污染）
/// - 列表支持搜索 + 角色筛选（ICollectionView 单一数据源）
/// - 登录失败锁定（UserStore 持久化）+ 管理员解锁
/// - 安全体检横幅：默认口令 / 免密账号 / 从未登录
/// - 密码策略：最小 8 位 + 确认输入 + 强度条（PasswordPolicy 统一）
/// </summary>
public partial class UserManagerViewModel : ObservableObject, IDisposable
{
    /// <summary>"全部"角色筛选哨兵（null = 全部）。</summary>
    public static readonly UserRole? AllRolesFilter = null;

    private readonly UserStore _userStore;
    private readonly IDialogService _dialog;
    private readonly UserSession _session;
    private readonly ListCollectionView _filteredView;

    /// <summary>可选角色列表（下拉绑定单源；XAML 经 x:Static 引用）。</summary>
    public static IReadOnlyList<UserRole> AvailableRoles { get; } =
        [UserRole.Operator, UserRole.Engineer, UserRole.Admin];

    public ObservableCollection<User> Users { get; } = new();

    /// <summary>按搜索 + 角色筛选后的展示视图（DataGrid 绑定源）。</summary>
    public ICollectionView FilteredUsers => _filteredView;

    /// <summary>筛选结果数（空态判断）。</summary>
    [ObservableProperty] private int _filteredCount;

    /// <summary>角色筛选胶囊（全部 + 各角色，含计数与选中态）。</summary>
    public ObservableCollection<UserRoleFilterOption> RoleFilters { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteUserCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetPasswordForCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpdateUserCommand))]
    private User? _selectedUser;

    /// <summary>是否编辑模式（选中用户 → 右侧 Tab 显示编辑面板）。</summary>
    public bool IsEditMode => SelectedUser is not null;

    /// <summary>列表搜索关键词（用户名/显示名模糊）。</summary>
    [ObservableProperty] private string _searchKeyword = "";

    /// <summary>当前角色筛选（null = 全部）。</summary>
    [ObservableProperty] private UserRole? _selectedRoleFilter;

    /// <summary>新用户名（添加用户表单绑定）。</summary>
    [ObservableProperty] private string _newUsername = string.Empty;

    /// <summary>新用户显示名（添加用户表单绑定）。</summary>
    [ObservableProperty] private string _newDisplayName = string.Empty;

    /// <summary>新用户角色（添加用户表单绑定）。</summary>
    [ObservableProperty] private UserRole _newUserRole = UserRole.Operator;

    /// <summary>新用户密码（code-behind 经 PasswordBox 传入）。</summary>
    public string NewPassword { get; set; } = string.Empty;

    /// <summary>新用户确认密码（code-behind 同步；与 NewPassword 一致性校验）。</summary>
    public string NewPasswordConfirm { get; set; } = string.Empty;

    /// <summary>新用户是否"首次登录必须改密"。</summary>
    [ObservableProperty] private bool _newMustChangePassword;

    /// <summary>新密码强度等级（0=弱 1=中 2=强，code-behind 实时更新）。</summary>
    [ObservableProperty] private int _newPasswordStrength;

    /// <summary>强度进度（0/50/100，供 ProgressBar）。</summary>
    public int NewPasswordStrengthPercent => NewPasswordStrength * 50;

    /// <summary>新密码强度文案（M369/M370/M371）。</summary>
    public string NewPasswordStrengthText => PasswordPolicy.StrengthText(NewPasswordStrength);

    // ──────────── 安全体检（方案5） ────────────

    /// <summary>安全风险项（横幅展示：默认口令=危险，免密/从未登录=提示）。</summary>
    public ObservableCollection<SecurityRiskItem> SecurityRisks { get; } = new();

    /// <summary>是否存在风险（横幅整体可见性）。</summary>
    public bool HasSecurityRisks => SecurityRisks.Count > 0;

    // ──────────── 编辑区（副本，保存时提交） ────────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateUserCommand))]
    [NotifyPropertyChangedFor(nameof(HasUserEdits))]
    private string _editDisplayName = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateUserCommand))]
    [NotifyPropertyChangedFor(nameof(HasUserEdits))]
    private UserRole _editRole;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateUserCommand))]
    [NotifyPropertyChangedFor(nameof(HasUserEdits))]
    private bool _editIsActive;

    /// <summary>编辑区是否有未保存修改（保存按钮可用性与"未保存"提示依据）。</summary>
    public bool HasUserEdits =>
        SelectedUser is not null &&
        (EditDisplayName != SelectedUser.DisplayName
         || EditRole != SelectedUser.Role
         || EditIsActive != SelectedUser.IsActive);

    public UserManagerViewModel(UserStore userStore, IDialogService dialog, UserSession session)
    {
        _userStore = userStore;
        _dialog = dialog;
        _session = session;
        _filteredView = new ListCollectionView(Users) { Filter = FilterPredicate };
        _userStore.UsersChanged += OnUsersChanged;
        RefreshUsers();
    }

    private void OnUsersChanged()
    {
        RefreshUsers();
    }

    partial void OnSelectedUserChanged(User? value)
    {
        OnPropertyChanged(nameof(IsEditMode));
        if (value is null)
        {
            EditDisplayName = "";
            EditRole = UserRole.Operator;
            EditIsActive = false;
            return;
        }
        LoadEditorState(value);
    }

    partial void OnSearchKeywordChanged(string value) => ApplyFilters();

    partial void OnSelectedRoleFilterChanged(UserRole? value)
    {
        foreach (var f in RoleFilters) f.IsSelected = f.Role == value;
        ApplyFilters();
    }

    private void LoadEditorState(User user)
    {
        EditDisplayName = user.DisplayName;
        EditRole = user.Role;
        EditIsActive = user.IsActive;
    }

    /// <summary>刷新用户列表 + 角色胶囊 + 安全体检；按用户名对齐选中项（引用重建后编辑目标一致）。</summary>
    private void RefreshUsers()
    {
        var currentName = SelectedUser?.Username;
        Users.Clear();
        foreach (var u in _userStore.GetAll())
            Users.Add(u);

        // 角色胶囊（计数随集合重建，选中态回填）
        var counts = Users.GroupBy(u => u.Role).ToDictionary(g => g.Key, g => g.Count());
        RoleFilters.Clear();
        RoleFilters.Add(new UserRoleFilterOption(null, Strings.K004, Users.Count) { IsSelected = SelectedRoleFilter is null });
        foreach (var role in AvailableRoles)
        {
            RoleFilters.Add(new UserRoleFilterOption(role, RoleText(role), counts.GetValueOrDefault(role)) { IsSelected = SelectedRoleFilter == role });
        }

        ApplyFilters();
        RefreshSecurityRisks();

        if (currentName is not null)
        {
            SelectedUser = Users.FirstOrDefault(u =>
                string.Equals(u.Username, currentName, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void ApplyFilters()
    {
        _filteredView.Refresh();
        FilteredCount = FilteredUsers.Cast<User>().Count();
    }

    /// <summary>过滤委托：搜索词（用户名/显示名）+ 角色。</summary>
    private bool FilterPredicate(object o)
    {
        if (o is not User u) return false;
        if (SelectedRoleFilter is { } role && u.Role != role) return false;
        var kw = SearchKeyword?.Trim();
        if (!string.IsNullOrEmpty(kw)
            && !u.Username.Contains(kw, StringComparison.OrdinalIgnoreCase)
            && !u.DisplayName.Contains(kw, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return true;
    }

    /// <summary>点击角色筛选胶囊。</summary>
    [RelayCommand]
    private void SelectRoleFilter(UserRoleFilterOption? option)
    {
        if (option is null) return;
        SelectedRoleFilter = option.Role;
    }

    private static string RoleText(UserRole role) => role switch
    {
        UserRole.Admin => Strings.M334,
        UserRole.Engineer => Strings.M333,
        _ => Strings.M332,
    };

    /// <summary>刷新安全体检：默认口令 / 免密账号 / 从未登录（创建超 30 天）。</summary>
    private void RefreshSecurityRisks()
    {
        SecurityRisks.Clear();
        foreach (var u in Users)
        {
            var parts = new List<string>();
            var isDanger = false;
            if (!string.IsNullOrEmpty(u.PasswordHash)
                && (PasswordHasher.Verify(UserStore.DefaultAdminPassword, u.PasswordHash)
                    || PasswordHasher.Verify(UserStore.DefaultEngineerPassword, u.PasswordHash)))
            {
                parts.Add(Strings.M373);
                isDanger = true;
            }
            if (string.IsNullOrEmpty(u.PasswordHash)) parts.Add(Strings.M374);
            if (u.LastLoginAt is null && DateTime.UtcNow - u.CreatedAt > SecurityRiskItem.NeverLoginThreshold)
                parts.Add(Strings.M375);
            if (parts.Count > 0)
            {
                SecurityRisks.Add(new SecurityRiskItem(u, string.Join("、", parts), isDanger));
            }
        }
        OnPropertyChanged(nameof(HasSecurityRisks));
    }

    // ──────────── 密码强度（code-behind 实时更新） ────────────

    /// <summary>密码框内容变化时由 code-behind 调用：更新强度条与确认一致性。</summary>
    public void UpdatePasswordInput(string? password, string? confirm)
    {
        NewPassword = password ?? "";
        NewPasswordConfirm = confirm ?? "";
        NewPasswordStrength = PasswordPolicy.EvaluateStrength(NewPassword);
        OnPropertyChanged(nameof(NewPasswordStrengthText));
        OnPropertyChanged(nameof(NewPasswordStrengthPercent));
    }

    // ──────────── 添加用户 ────────────

    /// <summary>添加用户（密码经 code-behind 传入，与确认密码一致性/最小长度在 VM 校验）。</summary>
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
        if (!PasswordPolicy.IsLongEnough(password))
        {
            _dialog.NotifyWarning(Strings.M364);
            return;
        }
        if (password != NewPasswordConfirm)
        {
            _dialog.NotifyWarning(Strings.M365);
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
            MustChangePassword = NewMustChangePassword,
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
        NewPassword = string.Empty;
        NewPasswordConfirm = string.Empty;
        NewMustChangePassword = false;
        NewPasswordStrength = 0;
        OnPropertyChanged(nameof(NewPasswordStrengthText));
        _dialog.NotifySuccess(string.Format(Strings.M322));
        AuditLog.Record("User.Add", "User", user.Username, detail: $"角色={user.Role}" + (user.MustChangePassword ? "，首登改密" : ""));
    }

    // ──────────── 编辑选中用户 ────────────

    /// <summary>保存编辑区副本（角色/显示名/启用状态）。用户名不可改。</summary>
    [RelayCommand(CanExecute = nameof(CanSaveUserEdits))]
    private void UpdateUser()
    {
        if (SelectedUser is null) return;
        var username = SelectedUser.Username;

        // 自我保护：不能修改自己的角色或禁用自己（防误操作锁死系统管理入口）
        if (string.Equals(username, _session.CurrentUser?.Username, StringComparison.OrdinalIgnoreCase)
            && (EditRole != SelectedUser.Role || !EditIsActive))
        {
            _dialog.NotifyWarning(Strings.M361);
            return;
        }

        // 最后管理员预检（与 UserStore.Update 兜底同口径）
        if (SelectedUser.Role == UserRole.Admin && SelectedUser.IsActive
            && (EditRole != UserRole.Admin || !EditIsActive)
            && Users.Count(u => u.Role == UserRole.Admin && u.IsActive) <= 1)
        {
            _dialog.NotifyWarning(Strings.M360);
            return;
        }

        var before = (SelectedUser.Role, SelectedUser.DisplayName, SelectedUser.IsActive);
        if (!_userStore.Update(username, EditRole, EditDisplayName, EditIsActive))
        {
            _dialog.NotifyWarning(Strings.M360);
            return;
        }

        LoadEditorState(SelectedUser);
        _dialog.NotifySuccess(Strings.M320);
        AuditLog.Record("User.Update", "User", username,
            before: before,
            after: (SelectedUser.Role, SelectedUser.DisplayName, SelectedUser.IsActive));
    }

    private bool CanSaveUserEdits() => SelectedUser is not null && HasUserEdits;

    // ──────────── 删除 / 重置密码 / 解锁（选中用户 + 行内快捷共用） ────────────

    /// <summary>删除用户（列表行快捷操作传目标用户；未传则用选中用户）。</summary>
    [RelayCommand]
    private void DeleteUser(User? user = null)
    {
        var target = user ?? SelectedUser;
        if (target is null) return;

        // 不能删除自己
        if (string.Equals(target.Username, _session.CurrentUser?.Username, StringComparison.OrdinalIgnoreCase))
        {
            _dialog.NotifyWarning(Strings.M327);
            return;
        }

        var confirm = _dialog.Show(
            string.Format(Strings.F324, Strings.M323, target.DisplayLabel),
            Strings.M323, MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var username = target.Username;
        var role = target.Role;
        if (!_userStore.Remove(username))
        {
            _dialog.NotifyWarning(Strings.M327);
            return;
        }

        AuditLog.Record("User.Delete", "User", username, detail: $"角色={role}");
        SelectedUser = null;
    }

    /// <summary>重置用户密码（列表选中/行内快捷共用；密码经 ChangePasswordDialog 收集）。</summary>
    [RelayCommand]
    private void ResetPasswordFor(User? user)
    {
        var target = user ?? SelectedUser;
        if (target is null) return;

        var dialog = new Views.ChangePasswordDialog(Strings.M324, target.DisplayLabel);
        if (Application.Current?.MainWindow is Window owner) dialog.Owner = owner;
        if (dialog.ShowDialog() != true) return;

        var password = dialog.Password;
        if (!PasswordPolicy.IsLongEnough(password))
        {
            _dialog.NotifyWarning(Strings.M364);
            return;
        }

        _userStore.ResetPassword(target.Username, password);
        _dialog.NotifySuccess(Strings.M324);
        AuditLog.Record("User.ResetPassword", "User", target.Username);
    }

    /// <summary>解锁账号（清零失败计数与锁定时间）。</summary>
    [RelayCommand]
    private void Unlock(User? user)
    {
        var target = user ?? SelectedUser;
        if (target is null) return;
        if (!_userStore.Unlock(target.Username)) return;
        _dialog.NotifySuccess(Strings.M367);
        AuditLog.Record("User.Unlock", "User", target.Username);
    }

    public void Dispose()
    {
        _userStore.UsersChanged -= OnUsersChanged;
    }
}

/// <summary>角色筛选胶囊项（列表上方筛选条；null Role = 全部）。</summary>
public sealed partial class UserRoleFilterOption : ObservableObject
{
    public UserRoleFilterOption(UserRole? role, string display, int count)
    {
        Role = role;
        Display = display;
        Count = count;
    }

    public UserRole? Role { get; }
    public string Display { get; }
    public int Count { get; }

    [ObservableProperty] private bool _isSelected;
}

/// <summary>安全体检风险项（横幅 + 风险列共用）。</summary>
public sealed class SecurityRiskItem
{
    public static readonly TimeSpan NeverLoginThreshold = TimeSpan.FromDays(30);

    public SecurityRiskItem(User target, string text, bool isDanger)
    {
        Target = target;
        Text = text;
        IsDanger = isDanger;
    }

    public User Target { get; }
    public string Text { get; }
    public bool IsDanger { get; }
    public string DisplayLabel => Target.DisplayLabel;
}
