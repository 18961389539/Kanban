using System.IO;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using MainAPP.ViewModels;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// 登录链路单元测试（审查修复 2026-08-13 补 0 覆盖盲区）：
/// PasswordHasher（哈希/验签/防篡改）、UserStore（默认账号/认证/增删改/迁移补账号）、
/// LoginViewModel（登录成功/失败/免密/未选用户分支）。
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public class UserStoreLoginTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppSettings _appSettings;

    public UserStoreLoginTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "UserStoreTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _appSettings = new AppSettings { ConfigDirectory = _tempDir };
        AuditLog.ResetForTest(); // 登录 VM 会写审计；未初始化时为 Noop
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private UserStore CreateLoadedStore()
    {
        var store = new UserStore(_appSettings);
        store.Load();
        return store;
    }

    // ──────────── PasswordHasher ────────────

    [Fact]
    public void Hash_Verify_Roundtrip_AndRejectsWrongPassword()
    {
        var hash = PasswordHasher.Hash("secret");
        Assert.True(PasswordHasher.Verify("secret", hash));
        Assert.False(PasswordHasher.Verify("Secret", hash));
        Assert.False(PasswordHasher.Verify("", hash));
    }

    [Fact]
    public void Verify_MalformedOrEmptyOrTampered_Fails()
    {
        Assert.False(PasswordHasher.Verify("x", ""));
        Assert.False(PasswordHasher.Verify("x", "no-colon"));
        Assert.False(PasswordHasher.Verify("x", "not-base64:also-not"));
        // 篡改盐后验签失败
        var hash = PasswordHasher.Hash("secret");
        var parts = hash.Split(':');
        var tampered = parts[0] + ":AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
        Assert.False(PasswordHasher.Verify("secret", tampered));
    }

    // ──────────── UserStore ────────────

    [Fact]
    public void Load_FirstRun_CreatesDefaultAccounts()
    {
        var store = CreateLoadedStore();

        var all = store.GetAll();
        Assert.Equal(3, all.Count);
        Assert.Contains(all, u => u.Username == "admin" && u.Role == UserRole.Admin);
        Assert.Contains(all, u => u.Username == "engineer" && u.Role == UserRole.Engineer);
        Assert.Contains(all, u => u.Username == "operator" && u.Role == UserRole.Operator && u.PasswordHash == "");
        Assert.True(File.Exists(store.FilePath));
    }

    [Fact]
    public void Authenticate_CorrectPassword_Succeeds_AndUpdatesLastLoginAt()
    {
        var store = CreateLoadedStore();

        var user = store.Authenticate("admin", "gly");
        Assert.NotNull(user);
        Assert.True(user!.LastLoginAt > DateTime.UtcNow.AddMinutes(-1));

        Assert.Null(store.Authenticate("admin", "wrong"));
        Assert.Null(store.Authenticate("ADMIN", "wrong")); // 用户名大小写不敏感
    }

    [Fact]
    public void Authenticate_PasswordlessOperator_AnyPasswordPasses()
    {
        var store = CreateLoadedStore();

        Assert.NotNull(store.Authenticate("operator", ""));
        Assert.NotNull(store.Authenticate("operator", "whatever"));
    }

    [Fact]
    public void Authenticate_InactiveUser_Rejected()
    {
        var store = CreateLoadedStore();
        // 默认库仅一个 Admin：先加第二个 Admin 解除"最后管理员"保护，再禁用 admin
        store.Add(new User { Username = "admin2", Role = UserRole.Admin, PasswordHash = PasswordHasher.Hash("x"), IsActive = true });
        Assert.True(store.Update("admin", UserRole.Admin, "管理员", isActive: false));

        Assert.Null(store.Authenticate("admin", "gly"));
    }

    [Fact]
    public void Add_Update_ResetPassword_Remove_Lifecycle()
    {
        var store = CreateLoadedStore();

        Assert.True(store.Add(new User { Username = "u1", Role = UserRole.Operator, PasswordHash = PasswordHasher.Hash("p1"), IsActive = true }));
        Assert.False(store.Add(new User { Username = "U1", Role = UserRole.Operator, PasswordHash = "", IsActive = true })); // 重名拒绝
        Assert.NotNull(store.Authenticate("u1", "p1"));

        Assert.True(store.Update("u1", UserRole.Engineer, "工程师1", true));
        Assert.Equal(UserRole.Engineer, store.Find("u1")!.Role);

        Assert.True(store.ResetPassword("u1", "newpass"));
        Assert.Null(store.Authenticate("u1", "p1"));
        Assert.NotNull(store.Authenticate("u1", "newpass"));

        Assert.True(store.Remove("u1"));
        Assert.Null(store.Find("u1"));
    }

    [Fact]
    public void Remove_LastAdmin_Refused()
    {
        var store = CreateLoadedStore();

        Assert.False(store.Remove("admin")); // 唯一 admin 不可删
        Assert.NotNull(store.Find("admin"));

        // 加第二个 admin 后可删第一个
        store.Add(new User { Username = "admin2", Role = UserRole.Admin, PasswordHash = PasswordHasher.Hash("x"), IsActive = true });
        Assert.True(store.Remove("admin"));
    }

    [Fact]
    public void Load_MissingOperator_MigrationAddsIt()
    {
        // 模拟旧版本 users.json 无 operator 账号
        File.WriteAllText(_appSettings.GetFilePath("users.json"),
            "[{\"Username\":\"admin\",\"DisplayName\":\"管理员\",\"Role\":2,\"PasswordHash\":\"AA==:AA==\",\"IsActive\":true,\"CreatedAt\":\"2026-01-01T00:00:00Z\"}]");

        var store = CreateLoadedStore();

        Assert.Contains(store.GetAll(), u => u.Username == "operator" && u.PasswordHash == "");
        Assert.Single(store.GetAll(), u => u.Username == "admin");
    }

    // ──────────── LoginViewModel ────────────

    private static UserSession CreateSessionWithOperator()
    {
        var session = new UserSession();
        session.Login(new User { Username = "operator", DisplayName = "操作员", Role = UserRole.Operator });
        return session;
    }

    [Fact]
    public void Login_NoSelection_ShowsError()
    {
        var vm = new LoginViewModel(CreateLoadedStore(), new UserSession());

        vm.LoginCommand.Execute("gly");

        Assert.False(vm.LoginSucceeded);
        Assert.False(string.IsNullOrEmpty(vm.ErrorMessage));
    }

    [Fact]
    public void Login_CorrectPassword_SetsSessionAndSucceeded()
    {
        var store = CreateLoadedStore();
        var session = new UserSession();
        var vm = new LoginViewModel(store, session);
        vm.SelectedUser = store.Find("admin");

        Assert.True(vm.LoginCommand.CanExecute(null));
        vm.LoginCommand.Execute("gly");

        Assert.True(vm.LoginSucceeded);
        Assert.Equal("admin", session.CurrentUser?.Username);
        Assert.Equal(string.Empty, vm.ErrorMessage);
    }

    [Fact]
    public void Login_WrongPassword_ShowsError_AndDoesNotSetSession()
    {
        var store = CreateLoadedStore();
        var session = new UserSession();
        var vm = new LoginViewModel(store, session);
        vm.SelectedUser = store.Find("admin");

        vm.LoginCommand.Execute("wrong");

        Assert.False(vm.LoginSucceeded);
        Assert.False(string.IsNullOrEmpty(vm.ErrorMessage));
        Assert.False(session.IsLoggedIn);
    }

    [Fact]
    public void Login_PasswordlessOperator_EmptyPasswordSucceeds()
    {
        var store = CreateLoadedStore();
        var session = new UserSession();
        var vm = new LoginViewModel(store, session);
        vm.SelectedUser = store.Find("operator");

        vm.LoginCommand.Execute(null); // 免密账号允许空密码

        Assert.True(vm.LoginSucceeded);
        Assert.Equal("operator", session.CurrentUser?.Username);
    }

    [Fact]
    public void AvailableUsers_OnlyActiveUsersListed()
    {
        var store = CreateLoadedStore();
        store.Update("engineer", UserRole.Engineer, "工程师", isActive: false);

        var vm = new LoginViewModel(store, new UserSession());

        Assert.DoesNotContain(vm.AvailableUsers, u => u.Username == "engineer");
        Assert.Contains(vm.AvailableUsers, u => u.Username == "admin");
        Assert.Contains(vm.AvailableUsers, u => u.Username == "operator");
    }

    // ──────────── 审查修复：最后管理员保护 + VM 编辑副本 ────────────

    [Fact]
    public void Update_LastActiveAdmin_CannotBeDisabledOrDemoted()
    {
        var store = CreateLoadedStore(); // 默认仅一个启用 Admin

        // 禁用/降级唯一管理员均被拒绝
        Assert.False(store.Update("admin", UserRole.Admin, "管理员", isActive: false));
        Assert.False(store.Update("admin", UserRole.Operator, "管理员", true));
        Assert.True(store.Find("admin")!.IsActive);
        Assert.Equal(UserRole.Admin, store.Find("admin")!.Role);

        // 增加第二个 Admin 后解除保护
        store.Add(new User { Username = "admin2", Role = UserRole.Admin, PasswordHash = PasswordHasher.Hash("x"), IsActive = true });
        Assert.True(store.Update("admin", UserRole.Operator, "管理员", true));
    }

    [Fact]
    public void Update_NonAdminTarget_NoProtection()
    {
        var store = CreateLoadedStore();

        Assert.True(store.Update("engineer", UserRole.Operator, "工程师", isActive: false));
        Assert.Equal(UserRole.Operator, store.Find("engineer")!.Role);
        Assert.False(store.Find("engineer")!.IsActive);
    }

    [Fact]
    public void Editor_UsesCopy_ListNotPollutedByUnsavedInput()
    {
        var store = CreateLoadedStore();
        var dialog = new FakeDialogService();
        var vm = new UserManagerViewModel(store, dialog, new UserSession());
        vm.SelectedUser = store.Find("engineer");
        var originalDisplayName = vm.SelectedUser!.DisplayName;

        vm.EditDisplayName = "临时修改（未保存）";

        // 列表/存储对象不被编辑区输入污染
        Assert.Equal(originalDisplayName, vm.SelectedUser.DisplayName);
        Assert.True(vm.HasUserEdits);
        Assert.True(vm.UpdateUserCommand.CanExecute(null));
    }

    [Fact]
    public void SaveEditor_CommitsCopy_AndClearsDirtyFlag()
    {
        var store = CreateLoadedStore();
        var dialog = new FakeDialogService();
        var vm = new UserManagerViewModel(store, dialog, new UserSession());
        vm.SelectedUser = store.Find("engineer");

        vm.EditDisplayName = "新显示名";
        vm.UpdateUserCommand.Execute(null);

        Assert.Equal("新显示名", store.Find("engineer")!.DisplayName);
        Assert.False(vm.HasUserEdits);
        Assert.False(vm.UpdateUserCommand.CanExecute(null)); // 无修改时保存不可用
    }

    [Fact]
    public void CannotChangeOwnRole_OrDisableSelf()
    {
        var store = CreateLoadedStore();
        var dialog = new FakeDialogService();
        var session = new UserSession();
        session.Login(store.Find("admin")!);
        var vm = new UserManagerViewModel(store, dialog, session);
        vm.SelectedUser = store.Find("admin");

        vm.EditRole = UserRole.Operator; // 尝试把自己降级
        vm.UpdateUserCommand.Execute(null);

        Assert.Equal(UserRole.Admin, store.Find("admin")!.Role); // 未被修改
        Assert.Contains(dialog.Warning, w => w.Contains("自己") || w.Contains("yourself"));
    }

    [Fact]
    public void CannotDisableOrDemoteLastAdmin_FromEditor()
    {
        var store = CreateLoadedStore();
        var dialog = new FakeDialogService();
        var session = new UserSession();
        session.Login(new User { Username = "admin2", DisplayName = "管理员2", Role = UserRole.Admin });
        var vm = new UserManagerViewModel(store, dialog, session);
        vm.SelectedUser = store.Find("admin");

        vm.EditIsActive = false; // 尝试禁用唯一启用 Admin（当前登录的是 admin2，非目标）
        vm.UpdateUserCommand.Execute(null);

        Assert.True(store.Find("admin")!.IsActive); // 被拦截
        Assert.Contains(dialog.Warning, w => w.Contains("管理员") || w.Contains("administrator"));
    }

    // ──────────── 登录失败锁定 ────────────

    [Fact]
    public void Authenticate_FiveFailures_LocksAccount_AndRejectsWhileLocked()
    {
        var store = CreateLoadedStore();

        for (var i = 0; i < 5; i++)
            Assert.Null(store.Authenticate("admin", "wrong"));

        // 锁定中：即使正确密码也拒绝
        Assert.Null(store.Authenticate("admin", "gly"));
        Assert.NotNull(store.GetLockRemaining("admin"));

        // 未锁定账号无剩余时间
        Assert.Null(store.GetLockRemaining("engineer"));
    }

    [Fact]
    public void Unlock_ClearsLockAndFailedAttempts()
    {
        var store = CreateLoadedStore();
        for (var i = 0; i < 5; i++)
            Assert.Null(store.Authenticate("admin", "wrong"));
        Assert.NotNull(store.GetLockRemaining("admin"));

        Assert.True(store.Unlock("admin"));

        Assert.Null(store.GetLockRemaining("admin"));
        Assert.NotNull(store.Authenticate("admin", "gly"));
    }

    [Fact]
    public void ResetPassword_ClearsLock()
    {
        var store = CreateLoadedStore();
        for (var i = 0; i < 5; i++)
            Assert.Null(store.Authenticate("admin", "wrong"));
        Assert.NotNull(store.GetLockRemaining("admin"));

        store.ResetPassword("admin", "newpass8");

        Assert.Null(store.GetLockRemaining("admin"));
        Assert.NotNull(store.Authenticate("admin", "newpass8"));
    }

    [Fact]
    public void LastLoginAt_PersistedAfterReload()
    {
        // 回归：LastLoginAt 此前被 [JsonIgnore] 不落盘，最后登录列/从未登录检测无数据基础
        var store = CreateLoadedStore();
        store.Authenticate("admin", "gly");

        var reloaded = new UserStore(_appSettings);
        reloaded.Load();

        Assert.NotNull(reloaded.Find("admin")!.LastLoginAt);
    }

    // ──────────── 列表筛选 / 搜索（方案2） ────────────

    [Fact]
    public void SearchKeyword_FiltersByUsernameOrDisplayName()
    {
        var store = CreateLoadedStore();
        var vm = new UserManagerViewModel(store, new FakeDialogService(), new UserSession());
        var filtered = () => vm.FilteredUsers.Cast<User>().ToList();

        Assert.Equal(3, filtered().Count);
        vm.SearchKeyword = "admin";
        Assert.Single(filtered());
        Assert.Equal("admin", filtered()[0].Username);

        vm.SearchKeyword = "工程"; // 匹配显示名
        Assert.Single(filtered());
        Assert.Equal("engineer", filtered()[0].Username);

        vm.SearchKeyword = "";
        Assert.Equal(3, filtered().Count);
        Assert.Equal(3, vm.FilteredCount);
    }

    [Fact]
    public void RoleFilter_FiltersByRole()
    {
        var store = CreateLoadedStore();
        var vm = new UserManagerViewModel(store, new FakeDialogService(), new UserSession());
        var filtered = () => vm.FilteredUsers.Cast<User>().ToList();

        var adminChip = vm.RoleFilters.First(f => f.Role == UserRole.Admin);
        vm.SelectRoleFilterCommand.Execute(adminChip);
        Assert.Single(filtered());
        Assert.Equal("admin", filtered()[0].Username);
        Assert.True(adminChip.IsSelected);

        var allChip = vm.RoleFilters[0];
        vm.SelectRoleFilterCommand.Execute(allChip);
        Assert.Equal(3, filtered().Count);
    }

    // ──────────── 安全体检（方案5） ────────────

    [Fact]
    public void SecurityRisks_DetectsDefaultPassword_Passwordless_NeverLogin()
    {
        var store = CreateLoadedStore();
        var vm = new UserManagerViewModel(store, new FakeDialogService(), new UserSession());

        // admin（默认口令 gly）、operator（免密）应有风险；从未登录判定需创建超 30 天
        var adminRisk = vm.SecurityRisks.FirstOrDefault(r => r.Target.Username == "admin");
        Assert.NotNull(adminRisk);
        Assert.True(adminRisk!.IsDanger);

        Assert.NotNull(vm.SecurityRisks.FirstOrDefault(r => r.Target.Username == "operator"));

        // 修改密码后风险消失（新密码非默认口令）
        store.ResetPassword("admin", "secure-pass-1");
        var vm2 = new UserManagerViewModel(store, new FakeDialogService(), new UserSession());
        Assert.DoesNotContain(vm2.SecurityRisks, r => r.Target.Username == "admin");
    }

    // ──────────── 密码策略（方案3） ────────────

    [Fact]
    public void AddUser_ShortPassword_Rejected()
    {
        var store = CreateLoadedStore();
        var dialog = new FakeDialogService();
        var vm = new UserManagerViewModel(store, dialog, new UserSession());
        vm.NewUsername = "u1";
        vm.NewPassword = "123";
        vm.NewPasswordConfirm = "123";

        vm.AddUserCommand.Execute("123");

        Assert.Null(store.Find("u1"));
        Assert.NotEmpty(dialog.Warning);
    }

    [Fact]
    public void AddUser_PasswordMismatch_Rejected()
    {
        var store = CreateLoadedStore();
        var dialog = new FakeDialogService();
        var vm = new UserManagerViewModel(store, dialog, new UserSession());
        vm.NewUsername = "u1";
        vm.NewPassword = "password1";
        vm.NewPasswordConfirm = "password2";

        vm.AddUserCommand.Execute("password1");

        Assert.Null(store.Find("u1"));
    }

    [Fact]
    public void AddUser_WithMustChangePassword_FlagPersisted()
    {
        var store = CreateLoadedStore();
        var dialog = new FakeDialogService();
        var vm = new UserManagerViewModel(store, dialog, new UserSession());
        vm.NewUsername = "u1";
        vm.NewPassword = "password1";
        vm.NewPasswordConfirm = "password1";
        vm.NewMustChangePassword = true;

        vm.AddUserCommand.Execute("password1");

        Assert.True(store.Find("u1")!.MustChangePassword);
    }

    [Fact]
    public void PasswordPolicy_StrengthLevels()
    {
        Assert.Equal(0, PasswordPolicy.EvaluateStrength("123"));
        Assert.Equal(0, PasswordPolicy.EvaluateStrength("12345678")); // 纯数字不足
        Assert.Equal(1, PasswordPolicy.EvaluateStrength("pass1234"));
        Assert.Equal(2, PasswordPolicy.EvaluateStrength("Passw0rd!2026"));
        Assert.False(PasswordPolicy.IsLongEnough("1234567"));
        Assert.True(PasswordPolicy.IsLongEnough("12345678"));
    }
}
