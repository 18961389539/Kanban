using System.IO;
using LicenseManager.Crypto;
using LicenseManager.Models;
using LicenseManager.Services;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// LicenseGate 单元测试：覆盖授权门禁的全状态转移与爆破防护。
///
/// 测试策略：
/// - 使用真实 LicenseStore + 临时目录（DPAPI 加密绑定当前用户，测试机器上可加解密）
/// - 使用真实 TrialTracker + TrialRegistryBackupStub（注册表操作走 Stub）
/// - 使用真实 ActivationAttemptTracker + 临时目录
/// - 通过 ProductKeyCodec.Encode 生成绑定当前机器的有效激活码
///
/// 覆盖范围：
/// - CheckStatus：无激活文件 → Trial；已激活且有效 → Active；已激活但过期 → Expired；机器码不匹配 → MachineMismatch
/// - TryActivate：有效激活码 → 成功；过期激活码 → 失败；错误激活码 → 失败 + 计数；连续 5 次错误 → 锁定
/// - Reset：清除激活与试用状态
/// </summary>
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","License")]
[Collection("LicenseEnvironment")]
public class LicenseGateTests : IDisposable
{
    private readonly string _tempDir;
    private readonly LicenseStore _store;
    private readonly TrialTracker _trialTracker;
    private readonly ActivationAttemptTracker _attemptTracker;
    private readonly string _machineCodeHash;

    public LicenseGateTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LicenseGateTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _store = new LicenseStore(_tempDir);
        var registryBackup = new TrialRegistryBackupStub();
        _trialTracker = new TrialTracker(_store, registryBackup, () => DateTime.UtcNow);
        _attemptTracker = new ActivationAttemptTracker(_tempDir, () => DateTime.UtcNow);
        // 获取当前机器码哈希（Base32 编码 8 字符），用于生成有效激活码
        _machineCodeHash = Base32.Encode(HardwareFingerprint.GetMachineCodeHash());
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private LicenseGate CreateGate() => new(_store, _trialTracker, _attemptTracker);

    // ──────────── CheckStatus ────────────

    [Fact]
    public void CheckStatus_NoLicenseFile_ReturnsTrial()
    {
        var gate = CreateGate();
        var status = gate.CheckStatus();
        // 首次启动应进入试用期
        Assert.Equal(LicenseStatus.Trial, status);
    }

    [Fact]
    public void CheckStatus_NoLicenseFile_WithoutHmacKey_ReturnsTrial()
    {
        var previousKey = Environment.GetEnvironmentVariable(EmbeddedKey.EnvKeyName);
        try
        {
            Environment.SetEnvironmentVariable(EmbeddedKey.EnvKeyName, null);
            var gate = CreateGate();

            var status = gate.CheckStatus();

            Assert.Equal(LicenseStatus.Trial, status);
            Assert.StartsWith("dpapi|", File.ReadAllText(Path.Combine(_tempDir, "trial.dat")), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EmbeddedKey.EnvKeyName, previousKey);
        }
    }

    [Fact]
    public void CheckStatus_WithValidPermanentLicense_ReturnsActive()
    {
        var productKey = ProductKeyCodec.Encode(HardwareFingerprint.GetMachineCodeHash(), expireDate: null);
        var license = ProductKeyCodec.TryDecode(productKey, _machineCodeHash);
        Assert.NotNull(license);
        _store.SaveLicense(license!);

        var gate = CreateGate();
        var status = gate.CheckStatus();
        Assert.Equal(LicenseStatus.Active, status);
        Assert.NotNull(gate.CurrentLicense);
        Assert.True(gate.CurrentLicense!.IsPermanent);
    }

    [Fact]
    public void CheckStatus_WithExpiredLicense_ReturnsExpired()
    {
        var expireDate = DateTime.UtcNow.AddDays(-10);
        var productKey = ProductKeyCodec.Encode(HardwareFingerprint.GetMachineCodeHash(), expireDate);
        var license = ProductKeyCodec.TryDecode(productKey, _machineCodeHash);
        Assert.NotNull(license);
        _store.SaveLicense(license!);

        var gate = CreateGate();
        var status = gate.CheckStatus();
        Assert.Equal(LicenseStatus.Expired, status);
    }

    [Fact]
    public void CheckStatus_WithFutureLicense_ReturnsActive()
    {
        var expireDate = DateTime.UtcNow.AddDays(365);
        var productKey = ProductKeyCodec.Encode(HardwareFingerprint.GetMachineCodeHash(), expireDate);
        var license = ProductKeyCodec.TryDecode(productKey, _machineCodeHash);
        Assert.NotNull(license);
        _store.SaveLicense(license!);

        var gate = CreateGate();
        var status = gate.CheckStatus();
        Assert.Equal(LicenseStatus.Active, status);
        Assert.False(gate.CurrentLicense!.IsPermanent);
    }

    [Fact]
    public void RefreshStatusReadOnly_WithValidLicense_ReturnsActive()
    {
        var productKey = ProductKeyCodec.Encode(HardwareFingerprint.GetMachineCodeHash(), expireDate: null);
        var license = ProductKeyCodec.TryDecode(productKey, _machineCodeHash);
        Assert.NotNull(license);
        _store.SaveLicense(license!);

        var gate = CreateGate();
        var status = gate.RefreshStatusReadOnly();

        Assert.Equal(LicenseStatus.Active, status);
        Assert.NotNull(gate.CurrentLicense);
    }

    [Fact]
    public void CheckStatus_WithMismatchedMachineCode_ReturnsMachineMismatch()
    {
        // 用错误的机器码哈希生成激活码（模拟复制 license.dat 到其他机器）
        var wrongHash = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };
        var productKey = ProductKeyCodec.Encode(wrongHash, expireDate: null);
        var license = ProductKeyCodec.TryDecode(productKey, Base32.Encode(wrongHash));
        Assert.NotNull(license);
        _store.SaveLicense(license!);

        var gate = CreateGate();
        var status = gate.CheckStatus();
        // 当前机器码与 license 中绑定的机器码不匹配
        Assert.Equal(LicenseStatus.MachineMismatch, status);
    }

    // ──────────── TryActivate ────────────

    [Fact]
    public void TryActivate_WithValidPermanentKey_ReturnsTrue()
    {
        var gate = CreateGate();
        gate.CheckStatus();  // 初始化机器码
        var productKey = ProductKeyCodec.Encode(HardwareFingerprint.GetMachineCodeHash(), expireDate: null);

        var result = gate.TryActivate(productKey, out var error);
        Assert.True(result);
        Assert.Empty(error);
        Assert.Equal(LicenseStatus.Active, gate.CurrentStatus);
    }

    [Fact]
    public void TryActivate_WithExpiredKey_ReturnsFalse()
    {
        var gate = CreateGate();
        gate.CheckStatus();
        var expireDate = DateTime.UtcNow.AddDays(-1);
        var productKey = ProductKeyCodec.Encode(HardwareFingerprint.GetMachineCodeHash(), expireDate);

        var result = gate.TryActivate(productKey, out var error);
        Assert.False(result);
        Assert.Contains("过期", error);
    }

    [Fact]
    public void TryActivate_WithInvalidKey_ReturnsFalseAndIncrementsAttempts()
    {
        var gate = CreateGate();
        gate.CheckStatus();

        var result = gate.TryActivate("AAAAA-BBBBB-CCCCC-DDDDD-EEEEE", out var error);
        Assert.False(result);
        Assert.Contains("无效", error);
        Assert.Equal(1, gate.CurrentActivationAttempts);
    }

    [Fact]
    public void TryActivate_AfterFiveFailures_LocksOut()
    {
        var gate = CreateGate();
        gate.CheckStatus();
        var invalidKey = "AAAAA-BBBBB-CCCCC-DDDDD-EEEEE";

        // 连续 5 次错误
        for (var i = 0; i < 5; i++)
        {
            gate.TryActivate(invalidKey, out _);
        }

        // 第 6 次应被锁定
        var result = gate.TryActivate(invalidKey, out var error);
        Assert.False(result);
        Assert.True(gate.IsActivationLockedOut);
        Assert.Contains("锁定", error);
    }

    [Fact]
    public void TryActivate_WithValidKeyAfterSomeFailures_ResetsAttempts()
    {
        var gate = CreateGate();
        gate.CheckStatus();
        var invalidKey = "AAAAA-BBBBB-CCCCC-DDDDD-EEEEE";
        var validKey = ProductKeyCodec.Encode(HardwareFingerprint.GetMachineCodeHash(), expireDate: null);

        // 2 次错误
        gate.TryActivate(invalidKey, out _);
        gate.TryActivate(invalidKey, out _);
        Assert.Equal(2, gate.CurrentActivationAttempts);

        // 成功激活后重置计数
        var result = gate.TryActivate(validKey, out var error);
        Assert.True(result);
        Assert.Equal(0, gate.CurrentActivationAttempts);
    }

    // ──────────── Reset ────────────

    [Fact]
    public void Reset_ClearsLicenseAndTrial()
    {
        var gate = CreateGate();
        gate.CheckStatus();
        var validKey = ProductKeyCodec.Encode(HardwareFingerprint.GetMachineCodeHash(), expireDate: null);
        gate.TryActivate(validKey, out _);
        Assert.Equal(LicenseStatus.Active, gate.CurrentStatus);

        gate.Reset();
        Assert.Null(gate.CurrentLicense);
        Assert.Equal(LicenseStatus.Unlicensed, gate.CurrentStatus);
    }

    // ──────────── MachineCode 属性 ────────────

    [Fact]
    public void CheckStatus_PopulatesMachineCode()
    {
        var gate = CreateGate();
        Assert.Empty(gate.MachineCode);

        gate.CheckStatus();

        Assert.NotEmpty(gate.MachineCode);
        Assert.Equal(8, gate.MachineCode.Length);  // Base32 编码 5 字节 = 8 字符
        Assert.Equal(gate.MachineCode, gate.MachineCodeHash);
    }
}
