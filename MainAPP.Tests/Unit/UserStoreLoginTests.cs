using System.IO;
using Kanban.Core.Models;
using Kanban.Core.Services;
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
        store.Update("admin", UserRole.Admin, "管理员", isActive: false);

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
}
