using Kanban.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace Kanban.Collector.Core.Models;

/// <summary>
/// 报警级别
/// </summary>
public enum AlarmLevel
{
    /// <summary>
    /// 低级
    /// </summary>
    Low,

    /// <summary>
    /// 中级
    /// </summary>
    Medium,

    /// <summary>
    /// 高级
    /// </summary>
    High
}

/// <summary>
/// 报警项配置类，定义一个报警的属性
/// </summary>
public partial class Alarm : ObservableObject
{
    private string _id = Guid.NewGuid().ToString("N");

    /// <summary>
    /// 报警唯一标识（用作业务主键）
    /// </summary>
    public string Id
    {
        get => _id;
    set => SetProperty(ref _id, value);
    }

    private string _deviceId = string.Empty;

    /// <summary>
    /// 所属设备 Id（外键，EF Core 关联 Device.Alarms）
    /// </summary>
    public string DeviceId
    {
        get => _deviceId;
    set => SetProperty(ref _deviceId, value);
    }

    private string _name = string.Empty;

    /// <summary>
    /// 报警名称
    /// </summary>
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

    /// <summary>
    /// 报警PLC地址（如 M100）
    /// </summary>
    public string PlcAddress
    {
        get => _plcAddress;
    set => SetProperty(ref _plcAddress, value);
    }

    /// <summary>
    /// 地址编辑不改变业务主键。历史告警事件按 Id 关联，主键必须在对象生命周期内稳定。
    /// 缺失 Id 的旧配置由仓储在迁移边界生成。
    /// </summary>

    private string _description = string.Empty;

    /// <summary>
    /// 报警描述（展示在看板上的提示信息）
    /// </summary>
    public string Description
    {
        get => _description;
    set => SetProperty(ref _description, value);
    }

    private AlarmLevel _level;

    /// <summary>
    /// 报警级别
    /// </summary>
    public AlarmLevel Level
    {
        get => _level;
    set => SetProperty(ref _level, value);
    }

    private DateTime _startTime;

    /// <summary>
    /// 报警开始时间（运行时状态，不持久化）。
    /// 使用 [property: ...] 语法确保特性应用到源生成器生成的属性而非字段。
    /// </summary>
    [JsonIgnore]
    [NotMapped]
    public DateTime StartTime
    {
        get => _startTime;
    set => SetProperty(ref _startTime, value);
    }

    private DateTime _endTime;

    /// <summary>
    /// 报警结束时间（运行时状态，不持久化）。
    /// 使用 [property: ...] 语法确保特性应用到源生成器生成的属性而非字段。
    /// </summary>
    [JsonIgnore]
    [NotMapped]
    public DateTime EndTime
    {
        get => _endTime;
    set => SetProperty(ref _endTime, value);
    }

    /// <summary>
    /// 报警持续时间（由开始时间和结束时间计算，只读计算属性，不持久化）
    /// </summary>
    [JsonIgnore]
    [NotMapped]
    public TimeSpan Duration =>
        EndTime >= StartTime ? EndTime - StartTime : TimeSpan.Zero;
}
