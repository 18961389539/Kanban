using Kanban.ComponentModel;
using Kanban.Contracts.Enums;
using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace Kanban.Collector.Core.Models;

/// <summary>
/// 配方（按机型归属的配方库条目，持久化到 recipes.json）。
/// 运行时下发/校验逻辑见 RecipeApplier / RecipeValidator。
/// </summary>
public partial class Recipe : ObservableObject
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

    /// <summary>归属机型（设备类型）。空字符串 = 通用配方。</summary>
    public string MachineType
    {
        get => _machineType;
    set => SetProperty(ref _machineType, value);
    }

    private string _remark = string.Empty;

    public string Remark
    {
        get => _remark;
    set => SetProperty(ref _remark, value);
    }

    private DateTime _createdAt = DateTime.Now;

    public DateTime CreatedAt
    {
        get => _createdAt;
    set => SetProperty(ref _createdAt, value);
    }

    private DateTime _updatedAt = DateTime.Now;

    public DateTime UpdatedAt
    {
        get => _updatedAt;
    set => SetProperty(ref _updatedAt, value);
    }

    /// <summary>
    /// 加载时校验失败的原因。非空表示配方仍保留在库中，但禁止下发。
    /// 不落盘：下次加载会按当前规则重新校验。
    /// </summary>
    [JsonIgnore]
    public List<string> LoadErrors { get; } = new();

    /// <summary>是否允许下发（加载校验通过）。</summary>
    [JsonIgnore]
    public bool CanApply => LoadErrors.Count == 0;

    /// <summary>加载校验错误摘要（卡片展示；空 = 可下发）。</summary>
    [JsonIgnore]
    public string LoadErrorSummary => LoadErrors.Count == 0 ? "" : string.Join("；", LoadErrors);

    /// <summary>配方参数项（private set 防止外部替换集合导致事件订阅丢失）。</summary>
    [JsonInclude]
    public ObservableCollection<RecipeItem> Items { get; private set; } = new();

    /// <summary>
    /// 深拷贝（"复制配方"用）：新 Id + 时间戳，Items 逐项深拷贝，不共享集合与参数项实例。
    /// 复制品按新配方落库，不会覆盖原配方。
    /// </summary>
    public Recipe Clone() => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = Name,
        MachineType = MachineType,
        Remark = Remark,
        CreatedAt = DateTime.Now,
        UpdatedAt = DateTime.Now,
        Items = new ObservableCollection<RecipeItem>(Items.Select(i => i.Clone())),
    };
}

/// <summary>
/// 配方参数项：参数名 + PLC 地址 + 类型 + 值 + 校验范围。
/// Value 统一字符串存储，按 <see cref="DataType"/> 解析后写 PLC。
/// </summary>
public partial class RecipeItem : ObservableObject
{
    private string _paramName = string.Empty;

    public string ParamName
    {
        get => _paramName;
    set => SetProperty(ref _paramName, value);
    }

    private string _plcAddress = string.Empty;

    public string PlcAddress
    {
        get => _plcAddress;
    set => SetProperty(ref _plcAddress, value);
    }

    private PlcDataType _dataType = PlcDataType.Int32;

    public PlcDataType DataType
    {
        get => _dataType;
    set => SetProperty(ref _dataType, value);
    }

    private string _value = string.Empty;

    public string Value
    {
        get => _value;
    set => SetProperty(ref _value, value);
    }

    private double? _min;

    public double? Min
    {
        get => _min;
    set => SetProperty(ref _min, value);
    }

    private double? _max;

    public double? Max
    {
        get => _max;
    set => SetProperty(ref _max, value);
    }

    private string _unit = string.Empty;

    public string Unit
    {
        get => _unit;
    set => SetProperty(ref _unit, value);
    }

    /// <summary>深拷贝（编辑区使用副本，避免未保存的编辑污染配方库内存对象）。</summary>
    public RecipeItem Clone() => new()
    {
        ParamName = ParamName,
        PlcAddress = PlcAddress,
        DataType = DataType,
        Value = Value,
        Min = Min,
        Max = Max,
        Unit = Unit,
    };
}
