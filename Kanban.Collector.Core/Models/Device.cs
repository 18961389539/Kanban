using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace Kanban.Core.Models;

/// <summary>
/// 设备配置类（持久化到数据库）。
/// 运行时状态已拆分到 DeviceRuntime。
/// </summary>
public partial class Device : ObservableObject
{
    [ObservableProperty]
    private string _id = Guid.NewGuid().ToString("N");

    [ObservableProperty]
    private string _name = string.Empty;

    // ──────────── PLC 地址配置 ────────────

    [ObservableProperty]
    private string _okCountAddress = string.Empty;

    [ObservableProperty]
    private string _ngCountAddress = string.Empty;

    [ObservableProperty]
    private string _statusCountAddress = string.Empty;

    [ObservableProperty]
    private string _productionResetAddress = string.Empty;

    // ──────────── 配方配置 ────────────

    [ObservableProperty]
    private string _recipeName = string.Empty;

    [ObservableProperty]
    private int _recipeValue;

    [ObservableProperty]
    private string _recipeAddress = string.Empty;

    // ──────────── 生产节拍 ────────────

    /// <summary>
    /// 目标周期（单位：个/每小时）
    /// </summary>
    [ObservableProperty]
    private int _targetCycle;

    // ──────────── 子集合 ────────────

    /// <summary>
    /// 报警列表（M 位边沿检测，private set 防止外部替换集合导致事件订阅丢失）
    /// </summary>
    [JsonInclude]
    public ObservableCollection<Alarm> Alarms { get; private set; } = new();

    /// <summary>
    /// 缺陷列表（private set 防止外部替换集合导致事件订阅丢失）
    /// </summary>
    [JsonInclude]
    public ObservableCollection<Defect> Defects { get; private set; } = new();

    /// <summary>
    /// 计数报警列表（数值阈值判断，private set 防止外部替换集合导致事件订阅丢失）
    /// </summary>
    [JsonInclude]
    public ObservableCollection<CountAlarm> CountAlarms { get; private set; } = new();
}
