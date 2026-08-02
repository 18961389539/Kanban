using Microsoft.Win32;
using LicenseManager.Crypto;
using LicenseManager.Models;

namespace LicenseManager.Services;

/// <summary>
/// 试用期注册表备份：在 HKLM\SOFTWARE\Kanban 下记录完整试用状态，
/// 作为 trial.dat 被删除时的兜底，防止用户通过删除文件重置试用期。
/// </summary>
/// <remarks>
/// R-2 增强：备份完整 TrialState（FirstLaunchUtc + LastLaunchUtc + LastSystemUptimeMs + LaunchCount），
///           trial.dat 被删除后可恢复完整状态，保留时间回拨检测能力。
/// R-3 增强：HKLM 写入失败（非管理员）时回退到 HKCU，至少有当前用户级备份。
///
/// 注册表值用 HMAC 签名保护，防止用户直接修改。
/// 旧版本只备份 FirstLaunchUtc（TrialFirstLaunch 值），新版本备份完整 JSON（TrialStateJson 值）。
/// 读取时优先读新格式，回退到旧格式以保持兼容。
/// </remarks>
public class TrialRegistryBackup
{
    private const string RegistryKeyPath = @"SOFTWARE\Kanban";
    private const string StateValueName = "TrialStateJson";
    private const string SignatureValueName = "TrialSignature";

    // 旧版字段名（向后兼容读取）
    private const string LegacyFirstLaunchValueName = "TrialFirstLaunch";
    private const string LegacySignatureValueName = "TrialSignature";

    /// <summary>读取注册表中的试用状态备份。无记录或验签失败返回 null。</summary>
    public virtual TrialState? LoadBackupState()
    {
        // 1. 尝试 HKLM 读取新格式（完整 TrialState JSON）
        var state = TryLoadStateFromHive(RegistryHive.LocalMachine);
        if (state != null) return state;

        // 2. HKLM 失败，尝试 HKCU（R-3：非管理员运行时备份的位置）
        state = TryLoadStateFromHive(RegistryHive.CurrentUser);
        if (state != null) return state;

        // 3. 新格式不存在，尝试旧格式（仅 FirstLaunchUtc，向后兼容）
        var legacyFirstLaunch = TryLoadLegacyFirstLaunch();
        if (legacyFirstLaunch.HasValue)
        {
            return new TrialState
            {
                FirstLaunchUtc = legacyFirstLaunch.Value,
                LastLaunchUtc = legacyFirstLaunch.Value,
            };
        }

        return null;
    }

    /// <summary>写入完整试用状态到注册表。HKLM 失败时回退 HKCU。</summary>
    /// <returns>是否写入成功</returns>
    public virtual bool TrySaveBackupState(TrialState state)
    {
        // 1. 优先 HKLM（跨用户生效，需管理员）
        if (TrySaveStateToHive(RegistryHive.LocalMachine, state)) return true;

        // 2. 回退 HKCU（仅当前用户，非管理员可写）
        return TrySaveStateToHive(RegistryHive.CurrentUser, state);
    }

    // ──────────── 新格式：完整 TrialState JSON ────────────

    private static TrialState? TryLoadStateFromHive(RegistryHive hive)
    {
        try
        {
            using var key = OpenSubKey(hive, RegistryKeyPath);
            if (key == null) return null;

            var json = key.GetValue(StateValueName) as string;
            var signatureStr = key.GetValue(SignatureValueName) as string;
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(signatureStr)) return null;

            // 验证 HMAC 签名
            var data = System.Text.Encoding.UTF8.GetBytes(json);
            var expectedSig = HmacValidator.ComputeTag(data);
            var actualSig = Convert.FromBase64String(signatureStr);
            if (!HmacValidator.ConstantTimeEquals(expectedSig, actualSig)) return null;

            return System.Text.Json.JsonSerializer.Deserialize<TrialState>(json);
        }
        catch
        {
            return null;
        }
    }

    private static bool TrySaveStateToHive(RegistryHive hive, TrialState state)
    {
        try
        {
            using var key = CreateSubKey(hive, RegistryKeyPath, writable: true);
            if (key == null) return false;

            var json = System.Text.Json.JsonSerializer.Serialize(state);
            var data = System.Text.Encoding.UTF8.GetBytes(json);
            var signature = HmacValidator.ComputeTag(data);

            key.SetValue(StateValueName, json, RegistryValueKind.String);
            key.SetValue(SignatureValueName, Convert.ToBase64String(signature), RegistryValueKind.String);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ──────────── 旧格式兼容：仅 FirstLaunchUtc ────────────

    private static DateTime? TryLoadLegacyFirstLaunch()
    {
        try
        {
            // 先 HKLM 再 HKCU
            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            {
                using var key = OpenSubKey(hive, RegistryKeyPath);
                if (key == null) continue;

                var timestampStr = key.GetValue(LegacyFirstLaunchValueName) as string;
                var signatureStr = key.GetValue(LegacySignatureValueName) as string;
                if (string.IsNullOrEmpty(timestampStr) || string.IsNullOrEmpty(signatureStr)) continue;

                var data = System.Text.Encoding.UTF8.GetBytes(timestampStr);
                var expectedSig = HmacValidator.ComputeTag(data);
                var actualSig = Convert.FromBase64String(signatureStr);
                if (!HmacValidator.ConstantTimeEquals(expectedSig, actualSig)) continue;

                return DateTime.Parse(timestampStr, null, System.Globalization.DateTimeStyles.RoundtripKind);
            }
        }
        catch
        {
        }
        return null;
    }

    // ──────────── 注册表操作辅助 ────────────

    private static RegistryKey? OpenSubKey(RegistryHive hive, string path)
    {
        return hive switch
        {
            RegistryHive.LocalMachine => Registry.LocalMachine.OpenSubKey(path),
            RegistryHive.CurrentUser => Registry.CurrentUser.OpenSubKey(path),
            _ => null,
        };
    }

    private static RegistryKey? CreateSubKey(RegistryHive hive, string path, bool writable)
    {
        return hive switch
        {
            RegistryHive.LocalMachine => Registry.LocalMachine.CreateSubKey(path, writable),
            RegistryHive.CurrentUser => Registry.CurrentUser.CreateSubKey(path, writable),
            _ => null,
        };
    }

    private enum RegistryHive
    {
        LocalMachine,
        CurrentUser,
    }
}
