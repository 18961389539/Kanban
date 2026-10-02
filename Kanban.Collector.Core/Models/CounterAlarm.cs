using Kanban.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace Kanban.Collector.Core.Models;

/// <summary>
/// 计数报警：从 PLC 读取数值，超过阈值时触发报警。
/// 区别于 Alarm（M 位边沿检测），CounterAlarm 基于数值阈值判断。
/// </summary>
public partial class CounterAlarm : ObservableObject
{
    private string _id = Guid.NewGuid().ToString("N");

    /// <summary>唯一标识</summary>
    public string Id
    {
        get => _id;
    set => SetProperty(ref _id, value);
    }

    private string _deviceId = string.Empty;

    /// <summary>所属设备 Id</summary>
    public string DeviceId
    {
        get => _deviceId;
    set => SetProperty(ref _deviceId, value);
    }

    private string _name = string.Empty;

    /// <summary>报警名称</summary>
    public string Name
    {
        get => _name;
    set => SetProperty(ref _name, value);
    }

    private string? _nameEn;

    /// <summary>报警名称（英文，多语言显示用；为空回退 <see cref="Name"/>）。</summary>
    public string? NameEn
    {
        get => _nameEn;
    set => SetProperty(ref _nameEn, value);
    }

    private string? _nameJa;

    /// <summary>报警名称（日文，多语言显示用；为空回退 <see cref="Name"/>）。</summary>
    public string? NameJa
    {
        get => _nameJa;
    set => SetProperty(ref _nameJa, value);
    }

    private string? _namePt;

    /// <summary>报警名称（葡萄牙文，多语言显示用；为空回退 <see cref="Name"/>）。</summary>
    public string? NamePt
    {
        get => _namePt;
    set => SetProperty(ref _namePt, value);
    }

    private string _plcAddress = string.Empty;

    /// <summary>PLC 地址（D 字地址，如 D300）</summary>
    public string PlcAddress
    {
        get => _plcAddress;
    set => SetProperty(ref _plcAddress, value);
    }

    private int _maxValue;

    /// <summary>阈值上限，当前值超过此值时触发报警</summary>
    public int MaxValue
    {
        get => _maxValue;
    set => SetProperty(ref _maxValue, value);
    }

    // ──────────── 运行时状态（不持久化） ────────────

    private int _currentValue;

    /// <summary>
    /// PLC 当前值（运行时从 PLC 读取，不持久化）。
    /// 使用 [property: ...] 语法确保特性应用到源生成器生成的属性而非字段。
    /// </summary>
    [JsonIgnore]
    [NotMapped]
    public int CurrentValue
    {
        get => _currentValue;
    set => SetProperty(ref _currentValue, value, [nameof(IsTriggered)]);
    }

    /// <summary>
    /// 是否触发（CurrentValue > MaxValue，只读计算属性，不持久化）。
    /// MaxValue &lt;= 0 视为未配置阈值，不触发（避免新加未配置报警因 PLC 当前值非 0 立即误报）。
    /// </summary>
    [JsonIgnore]
    [NotMapped]
    public bool IsTriggered => MaxValue > 0 && CurrentValue > MaxValue;

    private DateTime _startTime;

    /// <summary>
    /// 本次触发开始时刻（运行时状态，不持久化）。
    /// 边沿进入触发时写入；恢复或停用后清零。
    /// </summary>
    [JsonIgnore]
    [NotMapped]
    public DateTime StartTime
    {
        get => _startTime;
    set => SetProperty(ref _startTime, value);
    }

    // ──────────── 可选辅助属性 ────────────

    private bool _enabled = true;

    /// <summary>是否启用（停用后采集循环跳过此报警）</summary>
    public bool Enabled
    {
        get => _enabled;
    set => SetProperty(ref _enabled, value);
    }

    private string _description = string.Empty;

    /// <summary>报警描述</summary>
    public string Description
    {
        get => _description;
    set => SetProperty(ref _description, value);
    }

    private string _unit = string.Empty;

    /// <summary>单位（如 个、次、mm，仅用于 UI 显示）</summary>
    public string Unit
    {
        get => _unit;
    set => SetProperty(ref _unit, value);
    }
}
