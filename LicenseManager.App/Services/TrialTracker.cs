using LicenseManager.Models;

namespace LicenseManager.Services;

/// <summary>
/// 试用期状态机：管理 30 天试用期的状态转换与时间回拨检测。
/// </summary>
/// <remarks>
/// 试用期规则：
/// - 首次启动时记录 FirstLaunchUtc，30 天后过期
/// - 每次启动对比当前时间与 LastLaunchUtc，若早于上次（容忍 5 秒漂移）→ 判定篡改
/// - 同时对比系统启动时间（Environment.TickCount64），系统启动时间倒退 → 判定篡改
/// - 篡改后试用期立即失效，必须激活才能继续使用
///
/// trial.dat 被删除时，从注册表备份读取 FirstLaunchUtc（防止重置试用期）。
/// 注册表无记录则视为全新安装，重新初始化试用期。
///
/// 时间源抽象为 Func 以支持单元测试注入固定时间。
/// </remarks>
public class TrialTracker
{
    /// <summary>试用期总天数</summary>
    public const int TrialDays = 30;

    /// <summary>容忍的时钟漂移（秒）：避免 NTP 同步导致误判</summary>
    private const int ClockDriftToleranceSec = 5;

    private readonly LicenseStore _store;
    private readonly TrialRegistryBackup _registryBackup;
    private readonly Func<DateTime> _utcNowProvider;
    private readonly Func<long> _uptimeProvider;

    /// <summary>默认构造：使用系统真实时间和启动时间。</summary>
    public TrialTracker(LicenseStore store, TrialRegistryBackup registryBackup)
        : this(store, registryBackup, () => DateTime.UtcNow, () => Environment.TickCount64)
    {
    }

    /// <summary>测试构造：注入时间和启动时间提供器。</summary>
    internal TrialTracker(
        LicenseStore store,
        TrialRegistryBackup registryBackup,
        Func<DateTime> utcNowProvider,
        Func<long> uptimeProvider)
    {
        _store = store;
        _registryBackup = registryBackup;
        _utcNowProvider = utcNowProvider;
        _uptimeProvider = uptimeProvider;
    }

    /// <summary>当前试用状态</summary>
    public TrialState? CurrentState { get; private set; }

    /// <summary>试用剩余天数（已激活或未启动试用时为 null）</summary>
    public int? RemainingDays
    {
        get
        {
            if (CurrentState == null) return null;
            var elapsed = _utcNowProvider() - CurrentState.FirstLaunchUtc;
            return Math.Max(0, TrialDays - (int)elapsed.TotalDays);
        }
    }

    /// <summary>
    /// 检查试用状态：首次启动初始化试用期，已存在则校验时间回拨。
    /// 返回值决定是否允许进入主程序。
    /// </summary>
    public LicenseStatus CheckStatus()
    {
        var state = _store.LoadTrial();
        var now = _utcNowProvider();
        var uptime = _uptimeProvider();

        // 首次启动：trial.dat 不存在
        if (state == null)
        {
            // 检查注册表备份：若存在完整状态，说明用户删除了 trial.dat 试图重置试用期
            var backupState = _registryBackup.LoadBackupState();
            if (backupState != null)
            {
                // trial.dat 被删除，但注册表有完整备份 → 用注册表状态重建 trial.dat
                // R-2：恢复完整状态（含 LastLaunchUtc），保留时间回拨检测能力
                backupState.LastLaunchUtc = now;
                backupState.LastSystemUptimeMs = uptime;
                backupState.LaunchCount++;
                _store.SaveTrial(backupState);

                // 检查注册表记录的首次启动是否已过期
                var backupElapsedDays = (now - backupState.FirstLaunchUtc).TotalDays;
                if (backupElapsedDays > TrialDays)
                {
                    CurrentState = backupState;
                    return LicenseStatus.TrialExpired;
                }

                CurrentState = backupState;
                return LicenseStatus.Trial;
            }

            // 全新安装：初始化试用期，并写入注册表备份（HKLM 失败回退 HKCU）
            state = new TrialState
            {
                FirstLaunchUtc = now,
                LastLaunchUtc = now,
                LastSystemUptimeMs = uptime,
                LaunchCount = 1,
            };
            _store.SaveTrial(state);
            _registryBackup.TrySaveBackupState(state);
            CurrentState = state;
            return LicenseStatus.Trial;
        }

        // 时间回拨检测
        var timeDrift = (now - state.LastLaunchUtc).TotalSeconds;
        if (timeDrift < -ClockDriftToleranceSec)
        {
            // 当前时间早于上次启动时间 → 判定篡改
            CurrentState = state;
            return LicenseStatus.TrialManipulated;
        }

        // S-3: 当前时间早于首次启动时间 → 判定篡改
        // 攻击者可能将时间调到首次启动之前（试用期从未开始），试图绕过回拨检测。
        // 首次启动时间是注册表备份的兜底值，回拨到此前说明系统时间被严重篡改。
        if (now < state.FirstLaunchUtc.AddSeconds(-ClockDriftToleranceSec))
        {
            CurrentState = state;
            return LicenseStatus.TrialManipulated;
        }

        if (uptime < state.LastSystemUptimeMs - 1000)  // 容忍 1 秒精度误差
        {
            // 系统启动时间倒退 → 判定篡改
            CurrentState = state;
            return LicenseStatus.TrialManipulated;
        }

        // 试用期是否已过
        var elapsedDays = (now - state.FirstLaunchUtc).TotalDays;
        if (elapsedDays > TrialDays)
        {
            CurrentState = state;
            return LicenseStatus.TrialExpired;
        }

        // 更新状态
        state.LastLaunchUtc = now;
        state.LastSystemUptimeMs = uptime;
        state.LaunchCount++;
        _store.SaveTrial(state);
        // R-2：同步更新注册表备份，确保 trial.dat 被删除时可恢复完整最新状态
        _registryBackup.TrySaveBackupState(state);
        CurrentState = state;
        return LicenseStatus.Trial;
    }
}
