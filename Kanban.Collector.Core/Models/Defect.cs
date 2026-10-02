using Kanban.ComponentModel;
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
    private string _id = Guid.NewGuid().ToString("N");

    /// <summary>
    /// 缺陷唯一标识（用作业务主键）
    /// </summary>
    public string Id
    {
        get => _id;
    set => SetProperty(ref _id, value);
    }

    private string _deviceId = string.Empty;

    /// <summary>
    /// 所属设备 Id（外键，EF Core 关联 Device.Defects）
    /// </summary>
    public string DeviceId
    {
        get => _deviceId;
    set => SetProperty(ref _deviceId, value);
    }

    private string _name = string.Empty;

    /// <summary>
    /// 缺陷名称
    /// </summary>
    public string Name
    {
        get => _name;
    set => SetProperty(ref _name, value);
    }

    private string? _nameEn;

    /// <summary>缺陷名称（英文，多语言显示用；为空回退 <see cref="Name"/>）。</summary>
    public string? NameEn
    {
        get => _nameEn;
    set => SetProperty(ref _nameEn, value);
    }

    private string? _nameJa;

    /// <summary>缺陷名称（日文，多语言显示用；为空回退 <see cref="Name"/>）。</summary>
    public string? NameJa
    {
        get => _nameJa;
    set => SetProperty(ref _nameJa, value);
    }

    private string? _namePt;

    /// <summary>缺陷名称（葡萄牙文，多语言显示用；为空回退 <see cref="Name"/>）。</summary>
    public string? NamePt
    {
        get => _namePt;
    set => SetProperty(ref _namePt, value);
    }

    private int _count;

    /// <summary>
    /// 缺陷数量（运行时状态，不持久化）。
    /// 使用 [property: ...] 语法确保特性应用到源生成器生成的属性而非字段。
    /// </summary>
    [JsonIgnore]
    [NotMapped]
    public int Count
    {
        get => _count;
    set => SetProperty(ref _count, value);
    }

    private string _plcAddress = string.Empty;

    /// <summary>
    /// 数量地址（如 D200）
    /// </summary>
    public string PlcAddress
    {
        get => _plcAddress;
    set => SetProperty(ref _plcAddress, value);
    }

    private DefectSeverity _severity;

    /// <summary>
    /// 严重等级
    /// </summary>
    public DefectSeverity Severity
    {
        get => _severity;
    set => SetProperty(ref _severity, value);
    }

    private DefectCategory _category;

    /// <summary>
    /// 缺陷类别
    /// </summary>
    public DefectCategory Category
    {
        get => _category;
    set => SetProperty(ref _category, value);
    }
}
