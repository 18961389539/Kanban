using System.IO;
using LicenseManager.Crypto;
using LicenseManager.Models;
using LicenseManager.Services;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// LicenseStore 持久化单元测试：覆盖 License/Trial 状态的保存、加载、清除、篡改检测、原子写入。
/// </summary>
/// <remarks>
/// LicenseInfo 用 DPAPI 加密（绑定当前 Windows 用户），测试机器上当前用户可正常加解密。
/// TrialState 用 HMAC 签名保护，可直接验证篡改行为。
/// </remarks>
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","License")]
[Collection("LicenseEnvironment")]
public class LicenseStoreTests : IDisposable
{
    private readonly string _tempDir;

    public LicenseStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"LicenseStoreTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private LicenseStore CreateStore() => new(_tempDir);

    private static LicenseInfo CreateSampleLicense(bool permanent = true, DateTime? expireDate = null)
    {
        return new LicenseInfo
        {
            MachineCodeHash = "ABCDEFGH",
            ExpireDate = permanent ? null : expireDate,
            ActivatedAt = new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc),
            ProductKey = "ABCDE-FGHIJ-KLMNO-PQRST-UVWXY",
        };
    }

    private static TrialState CreateSampleTrial()
    {
        return new TrialState
        {
            FirstLaunchUtc = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            LastLaunchUtc = new DateTime(2026, 7, 26, 0, 0, 0, DateTimeKind.Utc),
            LaunchCount = 5,
        };
    }

    // ──────────── License 持久化 ────────────

    [Fact]
    public void LoadLicense_NoFile_ReturnsNull()
    {
        var store = CreateStore();

        var loaded = store.LoadLicense();

        Assert.Null(loaded);
    }

    [Fact]
    public void SaveLicense_ThenLoad_RoundTrip()
    {
        var store = CreateStore();
        var license = CreateSampleLicense(permanent: true);

        store.SaveLicense(license);
        var loaded = store.LoadLicense();

        Assert.NotNull(loaded);
        Assert.Equal(license.MachineCodeHash, loaded!.MachineCodeHash);
        Assert.Equal(license.ExpireDate, loaded.ExpireDate);
        Assert.Equal(license.ActivatedAt, loaded.ActivatedAt);
        Assert.Equal(license.ProductKey, loaded.ProductKey);
        Assert.True(loaded.IsPermanent);
    }

    [Fact]
    public void SaveLicense_WithExpiry_PreservesExpireDate()
    {
        var store = CreateStore();
        var expire = new DateTime(2027, 12, 31, 0, 0, 0, DateTimeKind.Utc);
        var license = CreateSampleLicense(permanent: false, expireDate: expire);

        store.SaveLicense(license);
        var loaded = store.LoadLicense();

        Assert.NotNull(loaded);
        Assert.NotNull(loaded!.ExpireDate);
        Assert.Equal(expire, loaded.ExpireDate!.Value.ToUniversalTime());
        Assert.False(loaded.IsPermanent);
    }

    [Fact]
    public void SaveLicense_OverwritesPrevious()
    {
        var store = CreateStore();
        var license1 = CreateSampleLicense(permanent: true);
        license1.MachineCodeHash = "AAAAAAAA";
        var license2 = CreateSampleLicense(permanent: true);
        license2.MachineCodeHash = "BBBBBBBB";

        store.SaveLicense(license1);
        store.SaveLicense(license2);
        var loaded = store.LoadLicense();

        Assert.NotNull(loaded);
        Assert.Equal("BBBBBBBB", loaded!.MachineCodeHash);
    }

    [Fact]
    public void ClearLicense_RemovesFile()
    {
        var store = CreateStore();
        store.SaveLicense(CreateSampleLicense());
        var licensePath = Path.Combine(_tempDir, "license.dat");
        Assert.True(File.Exists(licensePath));

        store.ClearLicense();

        Assert.False(File.Exists(licensePath));
        Assert.Null(store.LoadLicense());
    }

    [Fact]
    public void ClearLicense_NoFile_DoesNotThrow()
    {
        var store = CreateStore();

        var exception = Record.Exception(() => store.ClearLicense());

        Assert.Null(exception);
    }

    [Fact]
    public void LoadLicense_CorruptedFile_ReturnsNull()
    {
        var store = CreateStore();
        // 写入非 DPAPI 加密的随机字节 → Unprotect 抛异常 → catch 返回 null
        var licensePath = Path.Combine(_tempDir, "license.dat");
        File.WriteAllBytes(licensePath, new byte[] { 0x00, 0x01, 0x02, 0x03 });

        var loaded = store.LoadLicense();

        Assert.Null(loaded);
    }

    // ──────────── Trial 持久化 ────────────

    [Fact]
    public void LoadTrial_NoFile_ReturnsNull()
    {
        var store = CreateStore();

        var loaded = store.LoadTrial();

        Assert.Null(loaded);
    }

    [Fact]
    public void SaveTrial_ThenLoad_RoundTrip()
    {
        var store = CreateStore();
        var trial = CreateSampleTrial();

        store.SaveTrial(trial);
        var loaded = store.LoadTrial();

        Assert.NotNull(loaded);
        Assert.Equal(trial.FirstLaunchUtc, loaded!.FirstLaunchUtc);
        Assert.Equal(trial.LastLaunchUtc, loaded.LastLaunchUtc);
        Assert.Equal(trial.LaunchCount, loaded.LaunchCount);
    }

    [Fact]
    public void SaveTrial_WithoutHmacKey_UsesCurrentUserDpapi()
    {
        var previousKey = Environment.GetEnvironmentVariable(EmbeddedKey.EnvKeyName);
        try
        {
            Environment.SetEnvironmentVariable(EmbeddedKey.EnvKeyName, null);
            var store = CreateStore();
            var trial = CreateSampleTrial();

            store.SaveTrial(trial);

            var content = File.ReadAllText(Path.Combine(_tempDir, "trial.dat"));
            Assert.StartsWith("dpapi|", content, StringComparison.Ordinal);
            var loaded = store.LoadTrial();
            Assert.NotNull(loaded);
            Assert.Equal(trial.FirstLaunchUtc, loaded!.FirstLaunchUtc);
            Assert.Equal(trial.LaunchCount, loaded.LaunchCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EmbeddedKey.EnvKeyName, previousKey);
        }
    }

    [Fact]
    public void SaveTrial_OverwritesPrevious()
    {
        var store = CreateStore();
        var trial1 = CreateSampleTrial();
        trial1.LaunchCount = 1;
        var trial2 = CreateSampleTrial();
        trial2.LaunchCount = 99;

        store.SaveTrial(trial1);
        store.SaveTrial(trial2);
        var loaded = store.LoadTrial();

        Assert.NotNull(loaded);
        Assert.Equal(99, loaded!.LaunchCount);
    }

    [Fact]
    public void ClearTrial_RemovesFile()
    {
        var store = CreateStore();
        store.SaveTrial(CreateSampleTrial());
        var trialPath = Path.Combine(_tempDir, "trial.dat");
        Assert.True(File.Exists(trialPath));

        store.ClearTrial();

        Assert.False(File.Exists(trialPath));
        Assert.Null(store.LoadTrial());
    }

    [Fact]
    public void ClearTrial_NoFile_DoesNotThrow()
    {
        var store = CreateStore();

        var exception = Record.Exception(() => store.ClearTrial());

        Assert.Null(exception);
    }

    [Fact]
    public void LoadTrial_TamperedPayload_HMACFails_ReturnsNull()
    {
        var store = CreateStore();
        store.SaveTrial(CreateSampleTrial());

        // 篡改 JSON 内容但保留旧签名 → HMAC 验签失败
        var trialPath = Path.Combine(_tempDir, "trial.dat");
        var content = File.ReadAllText(trialPath);
        var parts = content.Split('|');
        var tamperedJson = parts[0].Replace("\"LaunchCount\":5", "\"LaunchCount\":999");
        File.WriteAllText(trialPath, tamperedJson + "|" + parts[1]);

        var loaded = store.LoadTrial();

        Assert.Null(loaded);
    }

    [Fact]
    public void LoadTrial_TamperedSignature_ReturnsNull()
    {
        var store = CreateStore();
        store.SaveTrial(CreateSampleTrial());

        // 篡改签名 → 验签失败
        var trialPath = Path.Combine(_tempDir, "trial.dat");
        var content = File.ReadAllText(trialPath);
        var parts = content.Split('|');
        // 修改签名的第一个字符（Base64 编码）
        var sig = parts[1];
        var tamperedSig = (sig[0] == 'A' ? 'B' : 'A') + sig[1..];
        File.WriteAllText(trialPath, parts[0] + "|" + tamperedSig);

        var loaded = store.LoadTrial();

        Assert.Null(loaded);
    }

    [Fact]
    public void LoadTrial_MalformedContent_ReturnsNull()
    {
        var store = CreateStore();
        var trialPath = Path.Combine(_tempDir, "trial.dat");
        // 写入不含分隔符的内容
        File.WriteAllText(trialPath, "garbage content without separator");

        var loaded = store.LoadTrial();

        Assert.Null(loaded);
    }

    // ──────────── 原子写入 ────────────

    [Fact]
    public void SaveLicense_DoesNotLeaveTempFile()
    {
        var store = CreateStore();
        store.SaveLicense(CreateSampleLicense());

        // 原子写入完成后，.tmp 文件应已被重命名（不再存在）
        var tempPath = Path.Combine(_tempDir, "license.dat.tmp");
        Assert.False(File.Exists(tempPath));
        Assert.True(File.Exists(Path.Combine(_tempDir, "license.dat")));
    }

    [Fact]
    public void SaveTrial_DoesNotLeaveTempFile()
    {
        var store = CreateStore();
        store.SaveTrial(CreateSampleTrial());

        var tempPath = Path.Combine(_tempDir, "trial.dat.tmp");
        Assert.False(File.Exists(tempPath));
        Assert.True(File.Exists(Path.Combine(_tempDir, "trial.dat")));
    }

    [Fact]
    public void SaveLicense_ProducesEncryptedFile()
    {
        var store = CreateStore();
        var license = CreateSampleLicense();

        store.SaveLicense(license);

        // 加密后的文件不应直接包含明文 JSON（DPAPI 加密）
        var encrypted = File.ReadAllBytes(Path.Combine(_tempDir, "license.dat"));
        var encryptedText = System.Text.Encoding.UTF8.GetString(encrypted);
        Assert.DoesNotContain(license.MachineCodeHash, encryptedText);
        Assert.DoesNotContain(license.ProductKey, encryptedText);
    }

    [Fact]
    public void SaveTrial_ProducesHmacSignedFile()
    {
        var store = CreateStore();
        var trial = CreateSampleTrial();

        store.SaveTrial(trial);

        // 文件格式：json|base64-signature
        var content = File.ReadAllText(Path.Combine(_tempDir, "trial.dat"));
        var parts = content.Split('|');
        Assert.Equal(2, parts.Length);
        Assert.NotEmpty(parts[0]);  // JSON 内容
        Assert.NotEmpty(parts[1]);  // 签名

        // JSON 中应包含原始数据
        Assert.Contains("\"LaunchCount\":5", parts[0]);

        // 签名应为合法 Base64
        var sigBytes = Convert.FromBase64String(parts[1]);
        Assert.NotEmpty(sigBytes);
    }

    // ──────────── 自定义存储目录 ────────────

    [Fact]
    public void Constructor_CustomStorageDir_CreatesDirectory()
    {
        var customDir = Path.Combine(_tempDir, "Custom", "Sub", "Dir");
        Assert.False(Directory.Exists(customDir));

        var store = new LicenseStore(customDir);

        Assert.True(Directory.Exists(customDir));
        store.SaveLicense(CreateSampleLicense());
        Assert.True(File.Exists(Path.Combine(customDir, "license.dat")));
    }
}
