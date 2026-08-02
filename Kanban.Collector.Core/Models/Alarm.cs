using CommunityToolkit.Mvvm.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace Kanban.Core.Models;

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
    /// <summary>
    /// 报警唯一标识（用作业务主键）
    /// </summary>
    [ObservableProperty]
    private string _id = Guid.NewGuid().ToString("N");

    /// <summary>
    /// 所属设备 Id（外键，EF Core 关联 Device.Alarms）
    /// </summary>
    [ObservableProperty]
    private string _deviceId = string.Empty;

    /// <summary>
    /// 报警名称
    /// </summary>
    [ObservableProperty]
    private string _name = string.Empty;

    /// <summary>
    /// 报警PLC地址（如 M100）
    /// </summary>
    [ObservableProperty]
    private string _plcAddress = string.Empty;

    /// <summary>
    /// PlcAddress 变化时重新生成确定性 Id（DeviceId_PlcAddress），
    /// 使删除后重新添加同名同地址报警能续接历史数据。
    /// 仅当 DeviceId 和 PlcAddress 均非空时生效，否则保留随机 GUID。
    /// </summary>
    partial void OnPlcAddressChanged(string value)
    {
        if (!string.IsNullOrEmpty(DeviceId) && !string.IsNullOrEmpty(value))
            Id = $"{DeviceId}_{value}";
    }

    /// <summary>
    /// 报警描述（展示在看板上的提示信息）
    /// </summary>
    [ObservableProperty]
    private string _description = string.Empty;

    /// <summary>
    /// 报警级别
    /// </summary>
    [ObservableProperty]
    private AlarmLevel _level;

    /// <summary>
    /// 报警开始时间（运行时状态，不持久化）。
    /// 使用 [property: ...] 语法确保特性应用到源生成器生成的属性而非字段。
    /// </summary>
    [ObservableProperty]
    [property: JsonIgnore]
    [property: NotMapped]
    private DateTime _startTime;

    /// <summary>
    /// 报警结束时间（运行时状态，不持久化）。
    /// 使用 [property: ...] 语法确保特性应用到源生成器生成的属性而非字段。
    /// </summary>
    [ObservableProperty]
    [property: JsonIgnore]
    [property: NotMapped]
    private DateTime _endTime;

    /// <summary>
    /// 报警持续时间（由开始时间和结束时间计算，只读计算属性，不持久化）
    /// </summary>
    [JsonIgnore]
    [NotMapped]
    public TimeSpan Duration =>
        EndTime >= StartTime ? EndTime - StartTime : TimeSpan.Zero;
}
