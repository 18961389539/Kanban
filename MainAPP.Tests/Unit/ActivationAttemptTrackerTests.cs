using System.IO;
using LicenseManager.Crypto;
using LicenseManager.Services;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// 激活尝试跟踪器单元测试：覆盖错误计数、锁定、解锁、持久化。
/// </summary>
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
[Collection("LicenseEnvironment")]
public class ActivationAttemptTrackerTests : IDisposable
{
    private readonly string _tempDir;

    public ActivationAttemptTrackerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ActivationTrackerTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private ActivationAttemptTracker CreateTracker(Func<DateTime>? utcNowProvider = null)
        => new(_tempDir, utcNowProvider ?? (() => DateTime.UtcNow));

    // ──────────── 错误计数 ────────────

    [Fact]
    public void RecordFailure_IncrementsAttempts()
    {
        var tracker = CreateTracker();
        tracker.Load();

        Assert.Equal(0, tracker.CurrentAttempts);

        tracker.RecordFailure();
        Assert.Equal(1, tracker.CurrentAttempts);

        tracker.RecordFailure();
        Assert.Equal(2, tracker.CurrentAttempts);
    }

    [Fact]
    public void RecordSuccess_ResetsAttempts()
    {
        var tracker = CreateTracker();
        tracker.Load();
        tracker.RecordFailure();
        tracker.RecordFailure();
        tracker.RecordFailure();

        tracker.RecordSuccess();

        Assert.Equal(0, tracker.CurrentAttempts);
        Assert.False(tracker.IsLockedOut);
    }

    // ──────────── 锁定机制 ────────────

    [Fact]
    public void RecordFailure_ReachingMaxAttempts_LocksOut()
    {
        var now = DateTime.UtcNow;
        var tracker = CreateTracker(() => now);
        tracker.Load();

        // 错误 4 次还未锁定
        for (var i = 0; i < 4; i++) tracker.RecordFailure();
        Assert.False(tracker.IsLockedOut);

        // 第 5 次错误 → 锁定
        tracker.RecordFailure();
        Assert.True(tracker.IsLockedOut);
        Assert.NotNull(tracker.LockoutUntil);
        Assert.Equal(now.AddMinutes(ActivationAttemptTracker.LockoutMinutes), tracker.LockoutUntil);
    }

    [Fact]
    public void IsLockedOut_AfterLockoutPeriod_ReturnsFalse()
    {
        var now = DateTime.UtcNow;
        var tracker = CreateTracker(() => now);
        tracker.Load();

        // 触发锁定
        for (var i = 0; i < ActivationAttemptTracker.MaxAttempts; i++) tracker.RecordFailure();
        Assert.True(tracker.IsLockedOut);

        // 时间推进到锁定过期后
        var futureTime = now.AddMinutes(ActivationAttemptTracker.LockoutMinutes + 1);
        // 需要重新构造 tracker 来用新时间
        var tracker2 = CreateTracker(() => futureTime);
        tracker2.Load();
        Assert.False(tracker2.IsLockedOut);
    }

    [Fact]
    public void RemainingLockoutSeconds_ReturnsCorrectValue()
    {
        var now = DateTime.UtcNow;
        var tracker = CreateTracker(() => now);
        tracker.Load();

        // 触发锁定（5 分钟）
        for (var i = 0; i < ActivationAttemptTracker.MaxAttempts; i++) tracker.RecordFailure();

        // 推进 2 分钟
        var twoMinutesLater = now.AddMinutes(2);
        var tracker2 = CreateTracker(() => twoMinutesLater);
        tracker2.Load();

        // 剩余应约为 3 分钟（180 秒），允许 ±5 秒误差
        var remaining = tracker2.RemainingLockoutSeconds;
        Assert.InRange(remaining, 175, 185);
    }

    // ──────────── 持久化 ────────────

    [Fact]
    public void RecordFailure_PersistsAcrossInstances()
    {
        var tracker1 = CreateTracker();
        tracker1.Load();
        tracker1.RecordFailure();
        tracker1.RecordFailure();

        // 新实例应加载已保存的计数
        var tracker2 = CreateTracker();
        tracker2.Load();
        Assert.Equal(2, tracker2.CurrentAttempts);
    }

    [Fact]
    public void RecordFailure_WithoutHmacKey_UsesCurrentUserDpapi()
    {
        var previousKey = Environment.GetEnvironmentVariable(EmbeddedKey.EnvKeyName);
        try
        {
            Environment.SetEnvironmentVariable(EmbeddedKey.EnvKeyName, null);
            var tracker1 = CreateTracker();
            tracker1.Load();
            tracker1.RecordFailure();

            var attemptPath = Path.Combine(_tempDir, "activation_attempts.dat");
            Assert.StartsWith("dpapi|", File.ReadAllText(attemptPath), StringComparison.Ordinal);

            var tracker2 = CreateTracker();
            tracker2.Load();
            Assert.Equal(1, tracker2.CurrentAttempts);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EmbeddedKey.EnvKeyName, previousKey);
        }
    }

    [Fact]
    public void RecordSuccess_PersistsResetAcrossInstances()
    {
        var tracker1 = CreateTracker();
        tracker1.Load();
        tracker1.RecordFailure();
        tracker1.RecordFailure();
        tracker1.RecordSuccess();

        var tracker2 = CreateTracker();
        tracker2.Load();
        Assert.Equal(0, tracker2.CurrentAttempts);
    }

    [Fact]
    public void Reset_ClearsFileAndCounter()
    {
        var tracker1 = CreateTracker();
        tracker1.Load();
        tracker1.RecordFailure();
        tracker1.RecordFailure();
        tracker1.Reset();

        var tracker2 = CreateTracker();
        tracker2.Load();
        Assert.Equal(0, tracker2.CurrentAttempts);
        Assert.False(tracker2.IsLockedOut);
    }

    // ──────────── 篡改检测 ────────────

    [Fact]
    public void Load_TamperedFile_HMACFails_ResetsToZero()
    {
        var tracker1 = CreateTracker();
        tracker1.Load();
        tracker1.RecordFailure();
        tracker1.RecordFailure();

        // 篡改 attempts 文件
        var attemptPath = Path.Combine(_tempDir, "activation_attempts.dat");
        var content = File.ReadAllText(attemptPath);
        var parts = content.Split('|');
        var tamperedJson = parts[0].Replace("\"Attempts\":2", "\"Attempts\":99");
        File.WriteAllText(attemptPath, tamperedJson + "|" + parts[1]);

        var tracker2 = CreateTracker();
        tracker2.Load();
        Assert.Equal(0, tracker2.CurrentAttempts);  // 篡改后重置
    }
}
