using CommunityToolkit.Mvvm.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace Kanban.Collector.Core.Models;

/// <summary>
/// 缺陷严重等级
/// </summary>
public enum DefectSeverity
{
    /// <summary>
    /// 轻微
    /// </summary>
    Minor,

    /// <summary>
    /// 一般
    /// </summary>
    Major,

    /// <summary>
    /// 严重
    /// </summary>
    Critical
}

/// <summary>
/// 缺陷类别
/// </summary>
public enum DefectCategory
{
    /// <summary>
    /// 外观
    /// </summary>
    Appearance,

    /// <summary>
    /// 尺寸
    /// </summary>
    Dimension,

    /// <summary>
    /// 功能
    /// </summary>
    Function,

    /// <summary>
    /// 包装
    /// </summary>
    Packaging,

    /// <summary>
    /// 其他
    /// </summary>
    Other
}

/// <summary>
/// 缺陷数量类，记录某类缺陷的名称、数量、PLC地址、严重等级和类别
/// </summary>
public partial class Defect : ObservableObject
{
    /// <summary>
    /// 缺陷唯一标识（用作业务主键）
    /// </summary>
    [ObservableProperty]
    private string _id = Guid.NewGuid().ToString("N");

    /// <summary>
    /// 所属设备 Id（外键，EF Core 关联 Device.Defects）
    /// </summary>
    [ObservableProperty]
    private string _deviceId = string.Empty;

    /// <summary>
    /// 缺陷名称
    /// </summary>
    [ObservableProperty]
    private string _name = string.Empty;

    /// <summary>
    /// 缺陷数量（运行时状态，不持久化）。
    /// 使用 [property: ...] 语法确保特性应用到源生成器生成的属性而非字段。
    /// </summary>
    [ObservableProperty]
    [property: JsonIgnore]
    [property: NotMapped]
    private int _count;

    /// <summary>
    /// 数量地址（如 D200）
    /// </summary>
    [ObservableProperty]
    private string _plcAddress = string.Empty;

    /// <summary>
    /// 严重等级
    /// </summary>
    [ObservableProperty]
    private DefectSeverity _severity;

    /// <summary>
    /// 缺陷类别
    /// </summary>
    [ObservableProperty]
    private DefectCategory _category;
}
