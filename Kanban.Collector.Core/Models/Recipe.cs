using CommunityToolkit.Mvvm.ComponentModel;
using Kanban.Contracts.Enums;
using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace Kanban.Core.Models;

/// <summary>
/// 配方（按机型归属的配方库条目，持久化到 recipes.json）。
/// 运行时下发/校验逻辑见 RecipeApplier / RecipeValidator。
/// </summary>
public partial class Recipe : ObservableObject
{
    [ObservableProperty]
    private string _id = Guid.NewGuid().ToString("N");

    [ObservableProperty]
    private string _name = string.Empty;

    /// <summary>归属机型（设备类型）。空字符串 = 通用配方。</summary>
    [ObservableProperty]
    private string _machineType = string.Empty;

    [ObservableProperty]
    private string _remark = string.Empty;

    [ObservableProperty]
    private DateTime _createdAt = DateTime.Now;

    [ObservableProperty]
    private DateTime _updatedAt = DateTime.Now;

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
    [ObservableProperty]
    private string _paramName = string.Empty;

    [ObservableProperty]
    private string _plcAddress = string.Empty;

    [ObservableProperty]
    private PlcDataType _dataType = PlcDataType.Int32;

    [ObservableProperty]
    private string _value = string.Empty;

    [ObservableProperty]
    private double? _min;

    [ObservableProperty]
    private double? _max;

    [ObservableProperty]
    private string _unit = string.Empty;

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
