using CommunityToolkit.Mvvm.ComponentModel;
using Kanban.Collector.Core.Services;

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

    [ObservableProperty]
    private int _okProduction;

    [ObservableProperty]
    private int _ngProduction;

    [ObservableProperty]
    private int _statusWord;

    // ──────────── 会话累计值 ────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QualityRate))]
    [NotifyPropertyChangedFor(nameof(PerformanceRate))]
    [NotifyPropertyChangedFor(nameof(Oee))]
    private int _totalOkProduction;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QualityRate))]
    [NotifyPropertyChangedFor(nameof(PerformanceRate))]
    [NotifyPropertyChangedFor(nameof(Oee))]
    private int _totalNgProduction;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvailabilityRate))]
    [NotifyPropertyChangedFor(nameof(Oee))]
    private double _runTime;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvailabilityRate))]
    [NotifyPropertyChangedFor(nameof(Oee))]
    private double _alarmTime;

    [ObservableProperty]
    private double _pausedTime;

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
        double runTime, double alarmTime, double pausedTime)
    {
        OkProduction = okProduction;
        NgProduction = ngProduction;
        StatusWord = statusWord;
        TotalOkProduction = totalOkProduction;
        TotalNgProduction = totalNgProduction;
        RunTime = runTime;
        AlarmTime = alarmTime;
        PausedTime = pausedTime;
    }
}
