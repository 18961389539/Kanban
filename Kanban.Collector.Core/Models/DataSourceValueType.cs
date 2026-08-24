namespace Kanban.Collector.Core.Models;

/// <summary>数据源值项的数据类型。缺省值 Int32 保持旧配置兼容。</summary>
public enum DataSourceValueType
{
    Int32 = 0,
    Float32 = 1,
    Bool = 2,
    String = 3,
}

/// <summary>采集值的类型化运行时载体。</summary>
public readonly record struct DataSourceRuntimeValue(
    DataSourceValueType Type,
    int Int32Value = 0,
    float Float32Value = 0,
    bool BoolValue = false,
    string? StringValue = null,
    bool IsValid = true)
{
    public string DisplayText => Type switch
    {
        DataSourceValueType.Float32 => Float32Value.ToString("G9", System.Globalization.CultureInfo.InvariantCulture),
        DataSourceValueType.Bool => BoolValue ? "True" : "False",
        DataSourceValueType.String => StringValue ?? string.Empty,
        _ => Int32Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    public object? BoxedValue => Type switch
    {
        DataSourceValueType.Float32 => Float32Value,
        DataSourceValueType.Bool => BoolValue,
        DataSourceValueType.String => StringValue ?? string.Empty,
        _ => Int32Value,
    };
}
