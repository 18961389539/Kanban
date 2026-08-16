using System.IO;
using LicenseManager.Models;
using LicenseManager.Services;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// 试用期状态机单元测试：覆盖首次启动、试用期内、过期、时间回拨、系统启动时间倒退、注册表兜底。
/// </summary>
/// <remarks>
/// 测试使用可注入的时间和启动时间提供器，避免依赖系统时钟。
/// LicenseStore 注入临时目录，TrialRegistryBackup 用内存 stub 替代真实注册表。
/// </remarks>
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class TrialTrackerTests : IDisposable
{
    private readonly string _tempDir;

    public TrialTrackerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"LicenseManagerTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private LicenseStore CreateStore() => new(_tempDir);
    private TrialRegistryBackupStub CreateRegistryBackup() => new();

    // ──────────── 首次启动 ────────────

    [Fact]
    public void CheckStatus_NoTrialFile_InitializesTrialAndReturnsTrial()
    {
        var now = DateTime.UtcNow;
        var store = CreateStore();
        var backup = CreateRegistryBackup();
        var tracker = new TrialTracker(store, backup, () => now);

        var status = tracker.CheckStatus();

        Assert.Equal(LicenseStatus.Trial, status);
        Assert.NotNull(tracker.CurrentState);
        Assert.Equal(now, tracker.CurrentState!.FirstLaunchUtc);
        Assert.Equal(now, tracker.CurrentState.LastLaunchUtc);
        Assert.Equal(1, tracker.CurrentState.LaunchCount);
    }

    // ──────────── 试用期内 ────────────

    [Fact]
    public void CheckStatus_WithinTrial_ReturnsTrialAndUpdatesState()
    {
        var startTime = DateTime.UtcNow;
        var nextTime = startTime.AddHours(1);

        var store = CreateStore();
        var backup = CreateRegistryBackup();
        var tracker1 = new TrialTracker(store, backup, () => startTime);
        tracker1.CheckStatus();  // 首次启动

        // 第二次启动（1 小时后）
        var tracker2 = new TrialTracker(store, backup, () => nextTime);
        var status = tracker2.CheckStatus();

        Assert.Equal(LicenseStatus.Trial, status);
        Assert.Equal(2, tracker2.CurrentState!.LaunchCount);
        Assert.Equal(nextTime, tracker2.CurrentState.LastLaunchUtc);
    }

    [Fact]
    public void RemainingDays_WithinTrial_ReturnsCorrectDays()
    {
        var startTime = DateTime.UtcNow;
        var tenDaysLater = startTime.AddDays(10);

        var store = CreateStore();
        var backup = CreateRegistryBackup();
        var tracker1 = new TrialTracker(store, backup, () => startTime);
        tracker1.CheckStatus();

        var tracker2 = new TrialTracker(store, backup, () => tenDaysLater);
        tracker2.CheckStatus();

        // 30 - 10 = 20 天
        Assert.Equal(20, tracker2.RemainingDays);
    }

    // ──────────── 试用期过期 ────────────

    [Fact]
    public void CheckStatus_After30Days_ReturnsTrialExpired()
    {
        var startTime = DateTime.UtcNow;
        var thirtyOneDaysLater = startTime.AddDays(31);

        var store = CreateStore();
        var backup = CreateRegistryBackup();
        var tracker1 = new TrialTracker(store, backup, () => startTime);
        tracker1.CheckStatus();

        var tracker2 = new TrialTracker(store, backup, () => thirtyOneDaysLater);
        var status = tracker2.CheckStatus();

        Assert.Equal(LicenseStatus.TrialExpired, status);
    }

    [Fact]
    public void CheckStatus_Exactly30Days_ReturnsTrial()
    {
        var startTime = DateTime.UtcNow;
        // 30 天减 1 秒（确保还在试用期内）
        var thirtyDaysLater = startTime.AddDays(30).AddSeconds(-1);

        var store = CreateStore();
        var backup = CreateRegistryBackup();
        var tracker1 = new TrialTracker(store, backup, () => startTime);
        tracker1.CheckStatus();

        var tracker2 = new TrialTracker(store, backup, () => thirtyDaysLater);
        var status = tracker2.CheckStatus();

        Assert.Equal(LicenseStatus.Trial, status);
    }

    // ──────────── 时间回拨检测 ────────────

    [Fact]
    public void CheckStatus_TimeMovedBackward_ReturnsTrialManipulated()
    {
        var startTime = DateTime.UtcNow;
        var earlierTime = startTime.AddSeconds(-30);  // 回拨 30 秒，超过容忍阈值

        var store = CreateStore();
        var backup = CreateRegistryBackup();
        var tracker1 = new TrialTracker(store, backup, () => startTime);
        tracker1.CheckStatus();

        var tracker2 = new TrialTracker(store, backup, () => earlierTime);
        var status = tracker2.CheckStatus();

        Assert.Equal(LicenseStatus.TrialManipulated, status);
    }

    [Fact]
    public void CheckStatus_SmallTimeDrift_WithinTolerance_ReturnsTrial()
    {
        var startTime = DateTime.UtcNow;
        var slightDrift = startTime.AddSeconds(-3);  // 回拨 3 秒，在 5 秒容忍范围内

        var store = CreateStore();
        var backup = CreateRegistryBackup();
        var tracker1 = new TrialTracker(store, backup, () => startTime);
        tracker1.CheckStatus();

        var tracker2 = new TrialTracker(store, backup, () => slightDrift);
        var status = tracker2.CheckStatus();

        Assert.Equal(LicenseStatus.Trial, status);
    }

    // ──────────── 持久化 ────────────

    [Fact]
    public void CheckStatus_PersistsStateAcrossInstances()
    {
        var startTime = DateTime.UtcNow;
        var store = CreateStore();
        var backup = CreateRegistryBackup();

        var tracker1 = new TrialTracker(store, backup, () => startTime);
        tracker1.CheckStatus();

        // 新实例应加载已保存的状态
        var tracker2 = new TrialTracker(store, backup, () => startTime.AddHours(1));
        var status = tracker2.CheckStatus();

        Assert.Equal(LicenseStatus.Trial, status);
        Assert.Equal(2, tracker2.CurrentState!.LaunchCount);  // 累计启动次数
    }

    [Fact]
    public void CheckStatus_TamperedTrialFile_HMACFails_ReturnsTrial()
    {
        var startTime = DateTime.UtcNow;
        var store = CreateStore();
        var backup = CreateRegistryBackup();

        var tracker1 = new TrialTracker(store, backup, () => startTime);
        tracker1.CheckStatus();

        // 篡改 trial.dat 文件内容（破坏 HMAC 签名）
        var trialPath = Path.Combine(_tempDir, "trial.dat");
        var content = File.ReadAllText(trialPath);
        var parts = content.Split('|');
        var tamperedJson = parts[0].Replace("\"LaunchCount\":1", "\"LaunchCount\":99");
        File.WriteAllText(trialPath, tamperedJson + "|" + parts[1]);

        // HMAC 验签失败 → LoadTrial 返回 null
        // 注册表有完整备份 → 用注册表状态重建 trial.dat（R-2：LaunchCount 从备份恢复并递增）
        var tracker2 = new TrialTracker(store, backup, () => startTime.AddHours(1));
        var status = tracker2.CheckStatus();

        Assert.Equal(LicenseStatus.Trial, status);
        Assert.Equal(2, tracker2.CurrentState!.LaunchCount);  // 注册表备份 LaunchCount=1，重建后 +1
        Assert.Equal(startTime, tracker2.CurrentState.FirstLaunchUtc);  // 保留首次启动时间
    }

    // ──────────── 注册表兜底 ────────────

    [Fact]
    public void CheckStatus_TrialFileDeleted_ButRegistryHasBackup_UsesBackupFirstLaunch()
    {
        var startTime = DateTime.UtcNow;
        var tenDaysLater = startTime.AddDays(10);

        var store = CreateStore();
        var backup = CreateRegistryBackup();

        // 首次启动：写入 trial.dat 和注册表
        var tracker1 = new TrialTracker(store, backup, () => startTime);
        tracker1.CheckStatus();

        // 删除 trial.dat 模拟用户删除
        var trialPath = Path.Combine(_tempDir, "trial.dat");
        File.Delete(trialPath);

        // 10 天后启动：应从注册表恢复 FirstLaunchUtc，剩余 20 天
        var tracker2 = new TrialTracker(store, backup, () => tenDaysLater);
        var status = tracker2.CheckStatus();

        Assert.Equal(LicenseStatus.Trial, status);
        Assert.Equal(startTime, tracker2.CurrentState!.FirstLaunchUtc);  // 保留原始首次启动时间
        Assert.Equal(20, tracker2.RemainingDays);
    }

    [Fact]
    public void CheckStatus_TrialFileDeleted_AndRegistryShowsExpired_ReturnsTrialExpired()
    {
        var startTime = DateTime.UtcNow;
        var thirtyOneDaysLater = startTime.AddDays(31);

        var store = CreateStore();
        var backup = CreateRegistryBackup();

        // 首次启动
        var tracker1 = new TrialTracker(store, backup, () => startTime);
        tracker1.CheckStatus();

        // 删除 trial.dat
        File.Delete(Path.Combine(_tempDir, "trial.dat"));

        // 31 天后启动：注册表显示已过期
        var tracker2 = new TrialTracker(store, backup, () => thirtyOneDaysLater);
        var status = tracker2.CheckStatus();

        Assert.Equal(LicenseStatus.TrialExpired, status);
    }

    [Fact]
    public void CheckStatus_NoTrialFile_NoRegistryBackup_TreatedAsFreshInstall()
    {
        var now = DateTime.UtcNow;
        var store = CreateStore();
        var backup = CreateRegistryBackup();  // 空 stub

        var tracker = new TrialTracker(store, backup, () => now);
        var status = tracker.CheckStatus();

        Assert.Equal(LicenseStatus.Trial, status);
        Assert.Equal(now, tracker.CurrentState!.FirstLaunchUtc);  // 用当前时间初始化
        Assert.Equal(30, tracker.RemainingDays);  // 全新试用
    }
}
