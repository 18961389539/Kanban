using Kanban.ComponentModel;
using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace Kanban.Collector.Core.Models;

/// <summary>
/// 设备配置类（持久化到数据库）。
/// 运行时状态已拆分到 DeviceRuntime。
/// </summary>
public partial class Device : ObservableObject
{
    private string _id = Guid.NewGuid().ToString("N");

    public string Id
    {
        get => _id;
    set => SetProperty(ref _id, value);
    }

    private string _name = string.Empty;

    public string Name
    {
        get => _name;
    set => SetProperty(ref _name, value);
    }

    private string _machineType = string.Empty;

    /// <summary>设备机型/类型（配方按机型归属的关联键）。空字符串 = 通用。</summary>
    public string MachineType
    {
        get => _machineType;
    set => SetProperty(ref _machineType, value);
    }

    private string _connectionProfileId = ConnectionProfile.DefaultId;

    /// <summary>设备使用的连接档案；缺失的旧设备迁移到默认档案。</summary>
    public string ConnectionProfileId
    {
        get => _connectionProfileId;
    set => SetProperty(ref _connectionProfileId, value);
    }

    // ──────────── PLC 地址配置 ────────────

    private string _okCountAddress = string.Empty;

    public string OkCountAddress
    {
        get => _okCountAddress;
    set => SetProperty(ref _okCountAddress, value);
    }

    private string _ngCountAddress = string.Empty;

    public string NgCountAddress
    {
        get => _ngCountAddress;
    set => SetProperty(ref _ngCountAddress, value);
    }

    private string _statusCountAddress = string.Empty;

    public string StatusCountAddress
    {
        get => _statusCountAddress;
    set => SetProperty(ref _statusCountAddress, value);
    }

    private string _productionResetAddress = string.Empty;

    public string ProductionResetAddress
    {
        get => _productionResetAddress;
    set => SetProperty(ref _productionResetAddress, value);
    }

    // ──────────── 配方配置 ────────────

    private string _recipeName = string.Empty;

    public string RecipeName
    {
        get => _recipeName;
    set => SetProperty(ref _recipeName, value);
    }

    private int _recipeValue;

    public int RecipeValue
    {
        get => _recipeValue;
    set => SetProperty(ref _recipeValue, value);
    }

    private string _recipeAddress = string.Empty;

    public string RecipeAddress
    {
        get => _recipeAddress;
    set => SetProperty(ref _recipeAddress, value);
    }

    // ──────────── 生产节拍 ────────────

    private int _targetCycle;

    /// <summary>
    /// 目标产能（件/小时）。属性名 <c>TargetCycle</c> 为历史兼容，不是秒/件。
    /// 秒/件展示用 <c>3600 / TargetCycle</c>。
    /// </summary>
    public int TargetCycle
    {
        get => _targetCycle;
    set => SetProperty(ref _targetCycle, value);
    }

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
    /// 计数器报警列表（数值阈值判断，private set 防止外部替换集合导致事件订阅丢失）。
    /// JSON 字段名保留历史 "CountAlarms"：兼容旧版 devices.json 配置（改名仅限代码/UI，落盘格式不变）。
    /// </summary>
    [JsonInclude]
    [JsonPropertyName("CountAlarms")]
    public ObservableCollection<CounterAlarm> CounterAlarms { get; private set; } = new();

    /// <summary>
    /// 数据采集源列表（同一 PLC 上通过寄存器读取的温湿度/能耗等新维度数据。
    /// 电平触发或定时采集，越限/偏离告警走 alarm_events，不参与设备状态机。
    /// private set 防止外部替换集合导致事件订阅丢失）。
    /// </summary>
    [JsonInclude]
    public ObservableCollection<DataSource> Sources { get; private set; } = new();
}
