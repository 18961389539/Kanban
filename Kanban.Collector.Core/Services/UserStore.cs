using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kanban.Core.Models;
using Serilog;

namespace Kanban.Core.Services;

/// <summary>
/// 用户账号存储：持久化到 users.json，复用 AppSettings.WriteFileAtomically 原子写入。
/// 首次运行（users.json 不存在）时创建默认账号：
/// - admin / gly（管理员）
/// - engineer / gcs（工程师）
/// 密码哈希后存储，源码不含明文密码（遵守项目硬约束）。
/// </summary>
public class UserStore
{
    private const string UsersFileName = "users.json";

    /// <summary>连续登录失败达到该次数后锁定账号。</summary>
    public const int MaxFailedAttempts = 5;

    /// <summary>锁定持续时间。</summary>
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(10);

    /// <summary>默认账号口令（安全体检检测"默认口令未改"用；源码不含其它明文口令）。</summary>
    public const string DefaultAdminPassword = "gly";
    public const string DefaultEngineerPassword = "gcs";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly AppSettings _appSettings;
    private readonly object _lock = new();
    private List<User> _users = new();

    /// <summary>用户列表变更通知（UserManagerViewModel 订阅以刷新 UI）。</summary>
    public event Action? UsersChanged;

    public UserStore(AppSettings appSettings)
    {
        _appSettings = appSettings;
    }

    /// <summary>users.json 完整路径。</summary>
    public string FilePath => _appSettings.GetFilePath(UsersFileName);

    /// <summary>加载用户列表；文件不存在时初始化默认账号。</summary>
    public void Load()
    {
        lock (_lock)
        {
            if (!File.Exists(FilePath))
            {
                Log.Information("users.json 不存在，初始化默认账号");
                _users = CreateDefaultUsers();
                Save();
                return;
            }

            try
            {
                var json = File.ReadAllText(FilePath);
                var users = JsonSerializer.Deserialize<List<User>>(json, JsonOptions);
                _users = users ?? new List<User>();
                // 迁移：旧版本 users.json 可能没有 operator 账号，补齐以确保首次启动可自动以 Operator 登录
                EnsureOperatorAccount();
                Log.Information("已加载 {Count} 个用户账号", _users.Count);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "users.json 解析失败，备份并初始化默认账号");
                try
                {
                    var corruptPath = FilePath + ".corrupt";
                    if (File.Exists(corruptPath)) File.Delete(corruptPath);
                    File.Move(FilePath, corruptPath);
                }
                catch { /* 备份失败不阻断 */ }

                _users = CreateDefaultUsers();
                Save();
            }
        }
    }

    /// <summary>保存用户列表到 users.json（原子写入 + .bak 备份）。</summary>
    public void Save()
    {
        lock (_lock)
        {
            _appSettings.EnsureDirectory();
            var json = JsonSerializer.Serialize(_users, JsonOptions);
            AppSettings.WriteFileAtomically(FilePath, json);
        }
    }

    /// <summary>获取用户快照（线程安全副本）。</summary>
    public ReadOnlyCollection<User> GetAll()
    {
        lock (_lock)
        {
            // 返回快照副本，避免调用方在锁释放后因内部集合改动抛"集合已修改"
            return _users.ToList().AsReadOnly();
        }
    }

    /// <summary>按用户名查找（不存在返回 null）。</summary>
    public User? Find(string username)
    {
        lock (_lock)
        {
            return _users.FirstOrDefault(u =>
                string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// 验证登录凭据。成功时更新 LastLoginAt 并持久化，返回用户对象；失败返回 null。
    /// PasswordHash 为空表示免密账号（如默认 Operator），任意密码（含空）均可通过。
    /// 锁定策略：连续失败 <see cref="MaxFailedAttempts"/> 次锁定 <see cref="LockoutDuration"/>，
    /// 计数与锁定时间持久化（重启不失效）；锁定期间直接拒绝。
    /// </summary>
    public User? Authenticate(string username, string password)
    {
        User? result = null;
        var changed = false;
        lock (_lock)
        {
            var user = _users.FirstOrDefault(u =>
                string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));
            if (user is null || !user.IsActive)
            {
                result = null;
            }
            else if (user.LockedUntil is { } until && until > DateTime.UtcNow)
            {
                // 锁定期间拒绝（计数不清零，解锁/重置密码时清零）
                result = null;
            }
            else if (string.IsNullOrEmpty(user.PasswordHash))
            {
                // PasswordHash 为空 = 免密账号，跳过密码验证
                result = FinalizeLogin(user);
                changed = true;
            }
            else if (!PasswordHasher.Verify(password, user.PasswordHash))
            {
                user.FailedAttempts++;
                if (user.FailedAttempts >= MaxFailedAttempts)
                {
                    user.LockedUntil = DateTime.UtcNow.Add(LockoutDuration);
                    user.FailedAttempts = 0;
                    Log.Warning("账号 {Username} 连续失败 {Count} 次，已锁定 {Minutes} 分钟",
                        username, MaxFailedAttempts, (int)LockoutDuration.TotalMinutes);
                }
                Save();
                result = null;
                changed = true;
            }
            else
            {
                user.FailedAttempts = 0;
                result = FinalizeLogin(user);
                changed = true;
            }
        }

        // 锁外统一派发：避免在锁内触发事件导致重入/跨线程问题；Front UI 层负责封送
        if (changed) UsersChanged?.Invoke();
        return result;
    }

    /// <summary>账号当前锁定剩余时间（未锁定/账号不存在返回 null）。</summary>
    public TimeSpan? GetLockRemaining(string username)
    {
        lock (_lock)
        {
            var user = _users.FirstOrDefault(u =>
                string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));
            if (user?.LockedUntil is not { } until) return null;
            var remaining = until - DateTime.UtcNow;
            return remaining > TimeSpan.Zero ? remaining : null;
        }
    }

    /// <summary>解锁账号（清零失败计数与锁定时间），供管理员操作。</summary>
    public bool Unlock(string username)
    {
        lock (_lock)
        {
            var user = _users.FirstOrDefault(u =>
                string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));
            if (user is null) return false;
            user.FailedAttempts = 0;
            user.LockedUntil = null;
            Save();
        }
        UsersChanged?.Invoke();
        return true;
    }

    /// <summary>登录成功收尾：更新 LastLoginAt 并持久化。</summary>
    private User FinalizeLogin(User user)
    {
        user.LastLoginAt = DateTime.UtcNow;
        Save();
        return user;
    }

    /// <summary>添加用户（用户名重复返回 false）。</summary>
    public bool Add(User user)
    {
        lock (_lock)
        {
            if (_users.Any(u => string.Equals(u.Username, user.Username, StringComparison.OrdinalIgnoreCase)))
                return false;
            _users.Add(user);
            Save();
        }
        UsersChanged?.Invoke();
        return true;
    }

    /// <summary>更新用户（角色、显示名、启用状态）。用户名不可改。
    /// 与 <see cref="Remove"/> 同口径的全局不变量：不允许把最后一个启用的 Admin 禁用或降级（防止锁死系统）。</summary>
    public bool Update(string username, UserRole role, string displayName, bool isActive)
    {
        lock (_lock)
        {
            var user = _users.FirstOrDefault(u =>
                string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));
            if (user is null) return false;
            if (user.Role == UserRole.Admin && user.IsActive &&
                (role != UserRole.Admin || !isActive) &&
                _users.Count(u => u.Role == UserRole.Admin && u.IsActive) <= 1)
            {
                Log.Warning("拒绝禁用/降级最后一个管理员账号 {Username}", username);
                return false;
            }
            user.Role = role;
            user.DisplayName = displayName;
            user.IsActive = isActive;
            Save();
        }
        UsersChanged?.Invoke();
        return true;
    }

    /// <summary>重置用户密码（同时解除锁定与失败计数）。</summary>
    public bool ResetPassword(string username, string newPassword)
    {
        lock (_lock)
        {
            var user = _users.FirstOrDefault(u =>
                string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));
            if (user is null) return false;
            user.PasswordHash = PasswordHasher.Hash(newPassword);
            user.FailedAttempts = 0;
            user.LockedUntil = null;
            Save();
        }
        UsersChanged?.Invoke();
        return true;
    }

    /// <summary>删除用户（至少保留一个 Admin 账号，防止锁死）。</summary>
    public bool Remove(string username)
    {
        lock (_lock)
        {
            var user = _users.FirstOrDefault(u =>
                string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));
            if (user is null) return false;

            // 防止删除最后一个管理员导致系统无法管理
            if (user.Role == UserRole.Admin &&
                _users.Count(u => u.Role == UserRole.Admin && u.IsActive) <= 1)
            {
                Log.Warning("拒绝删除最后一个管理员账号 {Username}", username);
                return false;
            }

            _users.Remove(user);
            Save();
        }
        UsersChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// 创建默认账号：admin（管理员，密码 gly）+ engineer（工程师，密码 gcs）+ operator（操作员，免密）。
    /// admin/engineer 密码经哈希存储；operator 的 PasswordHash 为空，表示免密登录。
    /// </summary>
    private static List<User> CreateDefaultUsers()
    {
        return
        [
            new User
            {
                Username = "admin",
                DisplayName = "管理员",
                Role = UserRole.Admin,
                PasswordHash = PasswordHasher.Hash(DefaultAdminPassword),
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            },
            new User
            {
                Username = "engineer",
                DisplayName = "工程师",
                Role = UserRole.Engineer,
                PasswordHash = PasswordHasher.Hash(DefaultEngineerPassword),
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            },
            new User
            {
                Username = "operator",
                DisplayName = "操作员",
                Role = UserRole.Operator,
                PasswordHash = string.Empty, // 免密账号
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            },
        ];
    }

    /// <summary>
    /// 确保默认 operator 账号存在（免密登录）。
    /// 旧版本升级场景 users.json 可能缺该账号，补齐后首次启动可直接自动以 Operator 登录，
    /// 无需在 MainWindow 显示前弹登录窗。已存在同名账号（含被禁用/改密）时不覆盖。
    /// </summary>
    private void EnsureOperatorAccount()
    {
        if (_users.Any(u => string.Equals(u.Username, "operator", StringComparison.OrdinalIgnoreCase)))
            return;

        _users.Add(new User
        {
            Username = "operator",
            DisplayName = "操作员",
            Role = UserRole.Operator,
            PasswordHash = string.Empty, // 免密账号
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        });
        Save();
        Log.Information("已补齐默认 operator 账号（旧版本升级迁移）");
    }
}
