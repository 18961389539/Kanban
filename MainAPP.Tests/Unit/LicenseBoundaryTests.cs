using System.IO;
using LicenseManager.Crypto;
using LicenseManager.Models;
using LicenseManager.Services;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// License 边界测试：对应审查发现的**绕过/容错**路径（正常路径已由 LicenseGateTests 覆盖）。
/// - HMAC 密钥必须来自 KANBAN_HMAC_KEY 环境变量（修复 #2：已移除内嵌回退密钥）
/// - 损坏/篡改的 license.dat → 不崩溃、不误判 Active
/// 这些是"攻击面"回归：测试在，篡改路径就不会在重构中悄悄改变行为。
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "License")]
[Collection("LicenseEnvironment")]
public class LicenseBoundaryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly LicenseStore _store;
    private readonly TrialTracker _trialTracker;
    private readonly ActivationAttemptTracker _attemptTracker;
    private readonly string _machineCodeHash;
    private readonly string _previousHmacEnv;

    public LicenseBoundaryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LicenseBoundaryTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _store = new LicenseStore(_tempDir);
        _trialTracker = new TrialTracker(_store, new TrialRegistryBackupStub());
        _attemptTracker = new ActivationAttemptTracker(_tempDir, () => DateTime.UtcNow);
        _machineCodeHash = Base32.Encode(HardwareFingerprint.GetMachineCodeHash());
        _previousHmacEnv = Environment.GetEnvironmentVariable(EmbeddedKey.EnvKeyName);
    }

    public void Dispose()
    {
        // 还原环境变量，避免影响同集合其他测试
        if (_previousHmacEnv == null) Environment.SetEnvironmentVariable(EmbeddedKey.EnvKeyName, null);
        else Environment.SetEnvironmentVariable(EmbeddedKey.EnvKeyName, _previousHmacEnv);
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private LicenseGate CreateGate() => new(_store, _trialTracker, _attemptTracker);

    [Fact]
    public void HmacKey_IsLoadedFromEnvironmentVariable_NotEmbedded()
    {
        // 修复 #2 后：密钥不再内嵌回退，必须来自 KANBAN_HMAC_KEY（由 TestModuleInitializer 注入）。
        var key = EmbeddedKey.HmacKey;
        Assert.Equal(32, key.Length);
        Assert.Contains(EmbeddedKey.EnvKeyName, EmbeddedKey.KeySource);
    }

    [Fact]
    public void CorruptedLicenseFile_DoesNotCrash()
    {
        // 已知问题 ⑥：损坏文件路径应容错（备份/回退），不因解密异常导致启动崩溃
        File.WriteAllText(Path.Combine(_tempDir, "license.dat"), "这不是合法加密数据，纯属垃圾字节 12345");

        var gate = CreateGate();
        var status = gate.CheckStatus(); // 不应抛异常

        Assert.NotEqual(LicenseStatus.Active, status);
    }

    [Fact]
    public void TamperedLicenseData_NotActive()
    {
        // 篡改激活文件：先激活成功，再改动文件字节 → 必须降级为非 Active（不能静默通过）
        var gate = CreateGate();
        gate.CheckStatus(); // 初始化机器码（与 LicenseGateTests 的激活用例一致）
        var key = ProductKeyCodec.Encode(HardwareFingerprint.GetMachineCodeHash(), expireDate: null);
        var activated = gate.TryActivate(key, out _);
        Assert.True(activated, "前置条件：合法永久激活码应激活成功");

        // 篡改激活文件：先激活成功，再改动密文中间字节 → 必须降级为非 Active（不能静默通过）。
        // 注：DPAPI 密文对"尾部追加"容忍（Unprotect 仍成功），中部字节翻转会破坏密文 → 检测。
        var licensePath = Path.Combine(_tempDir, "license.dat");
        var bytes = File.ReadAllBytes(licensePath);
        bytes[bytes.Length / 2] ^= 0xFF; // 翻转中部一个字节
        File.WriteAllBytes(licensePath, bytes);

        var status = gate.CheckStatus();
        Assert.NotEqual(LicenseStatus.Active, status);
    }
}
