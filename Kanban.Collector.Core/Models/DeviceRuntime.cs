using Kanban.ComponentModel;
using Kanban.Collector.Core.Services;
using OfflineCauseKind = Kanban.Contracts.Enums.OfflineCause;

namespace Kanban.Collector.Core.Models;

/// <summary>
/// 设备运行时状态（纯内存，不持久化）。
/// 从 Device 拆分出来，分离配置与运行时职责。
/// </summary>
public partial class DeviceRuntime : ObservableObject
{
    /// <summary>
    /// 关联设备的 Id（不可变）
    /// </summary>
    public string DeviceId { get; }

    /// <summary>
    /// 目标周期快照（从 Device.TargetCycle 同步，用于性能率计算）。
    /// 可被 UI 直接读取用于展示，避免从两个来源读 TargetCycle 造成不一致。
    /// </summary>
    public int TargetCycle
    {
        get => _targetCycle;
        private set => _targetCycle = value;
    }
    private int _targetCycle;

    // ──────────── PLC 原始值 ────────────

    private int _okProduction;

    public int OkProduction
    {
        get => _okProduction;
    set => SetProperty(ref _okProduction, value);
    }

    private int _ngProduction;

    public int NgProduction
    {
        get => _ngProduction;
    set => SetProperty(ref _ngProduction, value);
    }

    private int _statusWord;

    public int StatusWord
    {
        get => _statusWord;
    set => SetProperty(ref _statusWord, value);
    }

    private OfflineCauseKind _offlineCause;

    /// <summary>
    /// 当前离线原因。StatusWord 非 0 时为 <see cref="OfflineCauseKind.None"/>。
    /// </summary>
    public OfflineCauseKind OfflineCause
    {
        get => _offlineCause;
    set => SetProperty(ref _offlineCause, value);
    }

    // ──────────── 会话累计值 ────────────

    private int _totalOkProduction;

    public int TotalOkProduction
    {
        get => _totalOkProduction;
    set => SetProperty(ref _totalOkProduction, value, [nameof(QualityRate), nameof(PerformanceRate), nameof(Oee)]);
    }

    private int _totalNgProduction;

    public int TotalNgProduction
    {
        get => _totalNgProduction;
    set => SetProperty(ref _totalNgProduction, value, [nameof(QualityRate), nameof(PerformanceRate), nameof(Oee)]);
    }

    private double _runTime;

    public double RunTime
    {
        get => _runTime;
    set => SetProperty(ref _runTime, value, [nameof(AvailabilityRate), nameof(PerformanceRate), nameof(Oee)]);
    }

    private double _alarmTime;

    public double AlarmTime
    {
        get => _alarmTime;
    set => SetProperty(ref _alarmTime, value, [nameof(AvailabilityRate), nameof(Oee)]);
    }

    private double _pausedTime;

    public double PausedTime
    {
        get => _pausedTime;
    set => SetProperty(ref _pausedTime, value);
    }

    private double _offlineTime;

    /// <summary>离线累计时长（秒）。不计入 OEE 可用率/性能率，仅作统计展示。</summary>
    public double OfflineTime
    {
        get => _offlineTime;
    set => SetProperty(ref _offlineTime, value);
    }

    // ──────────── OEE 计算属性 ────────────
    // 单源约定：OEE 公式只在 OeeCalculator 实现一处（含内存注释引用 memory/project_memory.md）。
    // 此处与快照发布器（SnapshotPublisher 读 runtime.Oee）、日报/复盘均委托 OeeCalculator，
    // 禁止内联重复公式，否则改公式需改多处。

    public double QualityRate => OeeCalculator.CalculateQualityRate(TotalOkProduction, TotalNgProduction);

    public double PerformanceRate => OeeCalculator.CalculatePerformanceRate(TotalOkProduction, TotalNgProduction, _targetCycle, RunTime);

    public double AvailabilityRate => OeeCalculator.CalculateAvailabilityRate(RunTime, AlarmTime);

    public double Oee => OeeCalculator.CalculateOee(QualityRate, PerformanceRate, AvailabilityRate);

    // ──────────── 构造与同步 ────────────

    public DeviceRuntime(Device device)
    {
        DeviceId = device.Id;
        _targetCycle = device.TargetCycle;
    }

    /// <summary>
    /// 用户修改 Device.TargetCycle 后调用，同步到运行时计算
    /// </summary>
    public void SyncTargetCycle(int targetCycle)
    {
        _targetCycle = targetCycle;
        OnPropertyChanged(nameof(PerformanceRate));
        OnPropertyChanged(nameof(Oee));
    }

    /// <summary>
    /// 重置班次累计数据
    /// </summary>
    public void ResetShift()
    {
        RunTime = 0;
        AlarmTime = 0;
        PausedTime = 0;
        OfflineTime = 0;
        TotalOkProduction = 0;
        TotalNgProduction = 0;
    }

    /// <summary>
    /// Remote 模式：由 KanbanDataClient 收到 Collector 快照后灌入运行时状态。
    /// 保持内部属性 private set，仅允许此显式入口变更，避免 UI 层绕过 OEE 通知逻辑。
    /// </summary>
    public void UpdateFromCollector(
        int okProduction, int ngProduction, int statusWord,
        int totalOkProduction, int totalNgProduction,
        double runTime, double alarmTime, double pausedTime, double offlineTime,
        OfflineCauseKind offlineCause = OfflineCauseKind.None)
    {
        OkProduction = okProduction;
        NgProduction = ngProduction;
        StatusWord = statusWord;
        OfflineCause = statusWord is (int)DeviceStatus.Running or (int)DeviceStatus.Alarm or (int)DeviceStatus.Paused
            ? OfflineCauseKind.None
            : offlineCause;
        TotalOkProduction = totalOkProduction;
        TotalNgProduction = totalNgProduction;
        RunTime = runTime;
        AlarmTime = alarmTime;
        PausedTime = pausedTime;
        OfflineTime = offlineTime;
    }

    /// <summary>采集侧写入状态字时同步离线原因（运行/报警/待机清成 None）。</summary>
    public void ApplyLiveStatus(int statusWord, OfflineCauseKind causeWhenOffline)
    {
        StatusWord = statusWord;
        OfflineCause = statusWord is (int)DeviceStatus.Running or (int)DeviceStatus.Alarm or (int)DeviceStatus.Paused
            ? OfflineCauseKind.None
            : causeWhenOffline;
    }
}
