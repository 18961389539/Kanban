using LicenseManager.Crypto;
using LicenseManager.Models;

namespace LicenseManager.Services;

/// <summary>
/// 授权门禁：MainAPP 启动时调用，决定是否允许进入主程序。
/// </summary>
/// <remarks>
/// 检查顺序：
/// 1. 已激活？→ 验证签名、过期、机器码绑定
/// 2. 未激活 → 检查试用期状态
/// 3. 试用期失效 → 返回 TrialExpired 或 TrialManipulated
///
/// 激活码爆破防护：连续错误 5 次后锁定 5 分钟。
/// 调用方根据返回值决定是否弹出激活对话框。
/// </remarks>
public class LicenseGate
{
    private readonly LicenseStore _store;
    private readonly TrialTracker _trialTracker;
    private readonly ActivationAttemptTracker _attemptTracker;

    public LicenseGate(LicenseStore store, TrialTracker trialTracker, ActivationAttemptTracker attemptTracker)
    {
        _store = store;
        _trialTracker = trialTracker;
        _attemptTracker = attemptTracker;
        _attemptTracker.Load();
    }

    /// <summary>当前授权状态</summary>
    public LicenseStatus CurrentStatus { get; private set; }

    /// <summary>当前激活信息（已激活时非 null）</summary>
    public LicenseInfo? CurrentLicense { get; private set; }

    /// <summary>当前试用状态</summary>
    public TrialState? CurrentTrial => _trialTracker.CurrentState;

    /// <summary>试用剩余天数</summary>
    public int? RemainingTrialDays => _trialTracker.RemainingDays;

    /// <summary>是否处于激活锁定状态（连续错误次数过多）</summary>
    public bool IsActivationLockedOut => _attemptTracker.IsLockedOut;

    /// <summary>激活锁定剩余秒数</summary>
    public int ActivationLockoutRemainingSeconds => _attemptTracker.RemainingLockoutSeconds;

    /// <summary>当前激活错误次数</summary>
    public int CurrentActivationAttempts => _attemptTracker.CurrentAttempts;

    /// <summary>当前机器码（8 字符）</summary>
    public string MachineCode { get; private set; } = string.Empty;

    /// <summary>当前机器码哈希（Base32 编码 8 字符）</summary>
    public string MachineCodeHash { get; private set; } = string.Empty;

    /// <summary>
    /// 启动时检查授权状态。
    /// </summary>
    public LicenseStatus CheckStatus()
    {
        return CheckStatusCore(updateTrialState: true);
    }

    /// <summary>
    /// 只读刷新当前状态：不写 trial.dat / 注册表，仅依据现有 license/trial 数据重算 CurrentStatus。
    /// 用于设置页定时刷新，避免 60 秒轮询时反复递增试用启动计数。
    /// </summary>
    public LicenseStatus RefreshStatusReadOnly()
    {
        return CheckStatusCore(updateTrialState: false);
    }

    private LicenseStatus CheckStatusCore(bool updateTrialState)
    {
        EnsureMachineCodeInitialized();

        var license = _store.LoadLicense();
        if (license != null)
            return EvaluateStoredLicense(license);

        CurrentLicense = null;
        CurrentStatus = updateTrialState
            ? _trialTracker.CheckStatus()
            : _trialTracker.GetReadOnlyStatus();
        return CurrentStatus;
    }

    private void EnsureMachineCodeInitialized()
    {
        if (!string.IsNullOrEmpty(MachineCode)) return;

        var hashBytes = HardwareFingerprint.GetMachineCodeHash();
        MachineCodeHash = Base32.Encode(hashBytes);
        MachineCode = MachineCodeHash;
    }

    private LicenseStatus EvaluateStoredLicense(LicenseInfo license)
    {
        // ProductKey 中的授权字段经过签名保护，不能直接信任 license.dat 中的副本。
        var revalidated = ProductKeyCodec.TryDecode(license.ProductKey, MachineCodeHash);
        if (revalidated == null)
        {
            CurrentLicense = license;
            CurrentStatus = LicenseStatus.MachineMismatch;
            return CurrentStatus;
        }

        revalidated.ActivatedAt = license.ActivatedAt;
        CurrentLicense = revalidated;
        CurrentStatus = revalidated.IsExpired ? LicenseStatus.Expired : LicenseStatus.Active;
        return CurrentStatus;
    }

    /// <summary>
    /// 尝试用输入的激活码激活。
    /// 成功返回 true，失败返回 false 并通过 out 参数给出错误信息。
    /// 连续错误 5 次后锁定 5 分钟，锁定期间拒绝任何尝试。
    /// </summary>
    public bool TryActivate(string productKey, out string errorMessage)
    {
        errorMessage = string.Empty;

        // 爆破防护：锁定期间拒绝激活
        if (_attemptTracker.IsLockedOut)
        {
            var remaining = _attemptTracker.RemainingLockoutSeconds;
            errorMessage = $"错误次数过多，已锁定。请 {remaining} 秒后重试。";
            return false;
        }

        var info = ProductKeyCodec.TryDecode(productKey, MachineCodeHash);
        if (info == null)
        {
            _attemptTracker.RecordFailure();
            var remainingAttempts = ActivationAttemptTracker.MaxAttempts - _attemptTracker.CurrentAttempts;
            if (_attemptTracker.IsLockedOut)
            {
                errorMessage = $"错误次数过多，已锁定 {ActivationAttemptTracker.LockoutMinutes} 分钟。";
            }
            else if (remainingAttempts > 0)
            {
                errorMessage = $"激活码无效或与当前机器不匹配，剩余尝试次数：{remainingAttempts}。";
            }
            else
            {
                errorMessage = "激活码无效或与当前机器不匹配。";
            }
            return false;
        }

        if (info.IsExpired)
        {
            _attemptTracker.RecordFailure();
            errorMessage = $"该激活码已于 {info.ExpireDate:yyyy-MM-dd} 过期。";
            return false;
        }

        _store.SaveLicense(info);
        _attemptTracker.RecordSuccess();
        CurrentLicense = info;
        CurrentStatus = LicenseStatus.Active;
        return true;
    }

    /// <summary>重置授权：删除激活信息和试用期状态（用于调试或重新开始）。</summary>
    public void Reset()
    {
        _store.ClearLicense();
        _store.ClearTrial();
        CurrentLicense = null;
        CurrentStatus = LicenseStatus.Unlicensed;
    }
}
