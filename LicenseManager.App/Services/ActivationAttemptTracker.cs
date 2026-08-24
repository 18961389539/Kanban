using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LicenseManager.Crypto;

namespace LicenseManager.Services;

/// <summary>
/// 激活尝试跟踪器：限制激活码错误次数，防止暴力破解。
/// </summary>
/// <remarks>
/// 规则：
/// - 连续错误 5 次后锁定，首次锁定 5 分钟
/// - 锁定时间指数递增：5min → 10min → 20min → 40min → ...（上限 24 小时）
/// - 锁定期间任何激活尝试都被拒绝
/// - 锁定过期后重置错误计数，但保留 LockoutCount 以递增下次锁定时间
/// - 激活成功后重置所有计数
/// - 计数持久化到本地文件（有 HMAC 时签名；新电脑无 HMAC 时使用当前用户 DPAPI）
/// </remarks>
public class ActivationAttemptTracker
{
    private const string ProtectedPrefix = "dpapi|";

    /// <summary>最大错误次数（达到后锁定）</summary>
    public const int MaxAttempts = 5;

    /// <summary>基础锁定时长（分钟），首次锁定使用此值</summary>
    public const int LockoutMinutes = 5;

    /// <summary>锁定时间上限（分钟），约 24 小时</summary>
    private const int MaxLockoutMinutes = 1440;

    private readonly string _attemptFilePath;
    private readonly Func<DateTime> _utcNowProvider;

    public ActivationAttemptTracker(string? storageDir = null, Func<DateTime>? utcNowProvider = null)
    {
        var dir = storageDir
            ?? Environment.GetEnvironmentVariable("KANBAN_DATA_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Kanban");
        Directory.CreateDirectory(dir);
        _attemptFilePath = Path.Combine(dir, "activation_attempts.dat");
        _utcNowProvider = utcNowProvider ?? (() => DateTime.UtcNow);
    }

    /// <summary>当前错误次数</summary>
    public int CurrentAttempts { get; private set; }

    /// <summary>锁定到期时间（UTC），未锁定为 null</summary>
    public DateTime? LockoutUntil { get; private set; }

    /// <summary>连续锁定次数（每次锁定 +1，激活成功重置为 0）。用于计算指数递增的锁定时间。</summary>
    public int LockoutCount { get; private set; }

    /// <summary>是否处于锁定状态</summary>
    public bool IsLockedOut
    {
        get
        {
            var now = _utcNowProvider();
            return LockoutUntil.HasValue && now < LockoutUntil.Value;
        }
    }

    /// <summary>剩余锁定秒数（未锁定时为 0）</summary>
    public int RemainingLockoutSeconds
    {
        get
        {
            if (!IsLockedOut) return 0;
            return (int)(LockoutUntil!.Value - _utcNowProvider()).TotalSeconds;
        }
    }

    /// <summary>加载持久化的尝试记录</summary>
    public void Load()
    {
        if (!File.Exists(_attemptFilePath))
        {
            CurrentAttempts = 0;
            LockoutUntil = null;
            return;
        }

        try
        {
            var content = File.ReadAllText(_attemptFilePath);

            if (content.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
            {
                var protectedBytes = Convert.FromBase64String(content[ProtectedPrefix.Length..]);
                var jsonBytes = ProtectedData.Unprotect(
                    protectedBytes,
                    optionalEntropy: null,
                    scope: DataProtectionScope.CurrentUser);
                LoadRecord(JsonSerializer.Deserialize<AttemptRecord>(jsonBytes));
                return;
            }

            var parts = content.Split('|');
            if (parts.Length != 2 || !EmbeddedKey.TryGetHmacKey(out _))
            {
                CurrentAttempts = 0;
                LockoutUntil = null;
                LockoutCount = 0;
                return;
            }

            var signature = Convert.FromBase64String(parts[1]);
            var data = Encoding.UTF8.GetBytes(parts[0]);
            var expectedSig = HmacValidator.ComputeTag(data);
            if (!HmacValidator.ConstantTimeEquals(signature, expectedSig))
            {
                // 签名无效 → 重置
                CurrentAttempts = 0;
                LockoutUntil = null;
                return;
            }

            LoadRecord(JsonSerializer.Deserialize<AttemptRecord>(parts[0]));
        }
        catch
        {
            CurrentAttempts = 0;
            LockoutUntil = null;
        }
    }

    /// <summary>记录一次失败尝试。达到上限时自动锁定，锁定时间指数递增。</summary>
    public void RecordFailure()
    {
        // 锁定期间不应调用（LicenseGate 会先检查 IsLockedOut），但防御性处理
        if (IsLockedOut) return;

        // 锁定过期后重置错误计数（给用户新的尝试机会，但保留 LockoutCount 以递增下次锁定时间）
        if (LockoutUntil.HasValue && _utcNowProvider() >= LockoutUntil.Value)
        {
            CurrentAttempts = 0;
            LockoutUntil = null;
        }

        CurrentAttempts++;
        if (CurrentAttempts >= MaxAttempts)
        {
            LockoutCount++;
            // 指数递增：5min → 10min → 20min → 40min → ...，上限 24 小时
            var lockoutMinutes = (int)Math.Min(LockoutMinutes * Math.Pow(2, LockoutCount - 1), MaxLockoutMinutes);
            LockoutUntil = _utcNowProvider().AddMinutes(lockoutMinutes);
        }
        Save();
    }

    /// <summary>记录一次成功尝试，重置所有计数。</summary>
    public void RecordSuccess()
    {
        CurrentAttempts = 0;
        LockoutUntil = null;
        LockoutCount = 0;
        Save();
    }

    /// <summary>重置计数（用于管理员工具或测试）</summary>
    public void Reset()
    {
        CurrentAttempts = 0;
        LockoutUntil = null;
        LockoutCount = 0;
        if (File.Exists(_attemptFilePath)) File.Delete(_attemptFilePath);
    }

    private void Save()
    {
        var record = new AttemptRecord
        {
            Attempts = CurrentAttempts,
            LockoutUntil = LockoutUntil,
            LockoutCount = LockoutCount,
        };
        var json = JsonSerializer.Serialize(record);
        var data = Encoding.UTF8.GetBytes(json);
        string content;
        if (EmbeddedKey.TryGetHmacKey(out _))
        {
            var signature = HmacValidator.ComputeTag(data);
            content = json + "|" + Convert.ToBase64String(signature);
        }
        else
        {
            var protectedBytes = ProtectedData.Protect(
                data,
                optionalEntropy: null,
                scope: DataProtectionScope.CurrentUser);
            content = ProtectedPrefix + Convert.ToBase64String(protectedBytes);
        }

        // 原子写入：防断电时 attempts.dat 被截断为半写状态导致 HMAC 验签失败（误重置计数）
        var tmp = _attemptFilePath + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, _attemptFilePath, overwrite: true);
    }

    private void LoadRecord(AttemptRecord? record)
    {
        if (record == null) return;
        CurrentAttempts = record.Attempts;
        LockoutUntil = record.LockoutUntil;
        LockoutCount = record.LockoutCount;
    }

    private class AttemptRecord
    {
        public int Attempts { get; set; }
        public DateTime? LockoutUntil { get; set; }
        public int LockoutCount { get; set; }
    }
}
