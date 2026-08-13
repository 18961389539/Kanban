namespace Kanban.Contracts.Dtos;

/// <summary>
/// 采集服务设置同步包（Remote 模式：MainAPP 设置页保存时，把**采集相关**参数推给 Collector 落盘并热生效）。
/// 只含 Collector 采集进程需要的字段（轮询/批读/班次/PLC 连接），不含 UI 设置（主题/字号/日报等）。
/// 可空字段 = 部分更新语义（只更新有值的字段；Collector 端合并后整体落盘 settings.json）。
/// </summary>
public sealed record CollectorSettingsDto
{
    /// <summary>PLC 数据采集轮询间隔（毫秒）。</summary>
    public int? PollingIntervalMs { get; init; }

    /// <summary>历史数据写入间隔（扫描次数）。</summary>
    public int? HistoryWriteIntervalScans { get; init; }

    /// <summary>PLC 连续批量读取的最大逻辑值数量。</summary>
    public int? PlcBatchReadMaxLength { get; init; }

    /// <summary>批量读取允许跨过的最大连续逻辑地址空洞数。</summary>
    public int? PlcBatchReadMaxGapSlots { get; init; }

    /// <summary>PLC 品牌（PlcBrand 枚举值）。</summary>
    public int? PlcBrand { get; init; }

    /// <summary>PLC IP 地址。</summary>
    public string? PlcIpAddress { get; init; }

    /// <summary>PLC 端口。</summary>
    public int? PlcPort { get; init; }

    /// <summary>PLC 连接超时（毫秒）。</summary>
    public int? PlcTimeoutMs { get; init; }

    /// <summary>Siemens S7 品牌参数（非空时部分更新，仅应用非空字段）。</summary>
    public SiemensSettingsDto? Siemens { get; init; }

    /// <summary>Modbus TCP 品牌参数（非空时部分更新，仅应用非空字段）。</summary>
    public ModbusTcpSettingsDto? ModbusTcp { get; init; }

    /// <summary>Omron FINS 品牌参数（非空时部分更新，仅应用非空字段）。</summary>
    public OmronFinsSettingsDto? Omron { get; init; }

    /// <summary>班次配置（非空时整体替换）。</summary>
    public List<ShiftConfigDto>? Shifts { get; init; }
}

/// <summary>Siemens S7 专属参数的跨进程传输形态。</summary>
public sealed record SiemensSettingsDto
{
    public string? Model { get; init; }
    public byte? Rack { get; init; }
    public byte? Slot { get; init; }
    /// <summary>PlcDataFormat 枚举值。</summary>
    public int? DataFormat { get; init; }
    public int? BatchInt32Limit { get; init; }
}

/// <summary>Modbus TCP 专属参数的跨进程传输形态。</summary>
public sealed record ModbusTcpSettingsDto
{
    public byte? UnitId { get; init; }
    public bool? AddressStartWithZero { get; init; }
    public int? RegisterFunction { get; init; }
    public int? BitFunction { get; init; }
    /// <summary>PlcDataFormat 枚举值。</summary>
    public int? DataFormat { get; init; }
    public int? BatchInt32Limit { get; init; }
}

/// <summary>Omron FINS 专属参数的跨进程传输形态。</summary>
public sealed record OmronFinsSettingsDto
{
    public int? ReadSplits { get; init; }
}

/// <summary>班次配置的跨进程传输形态（Collector 端还原为 Kanban.Core.Models.ShiftConfig）。</summary>
public sealed record ShiftConfigDto
{
    public string Name { get; init; } = "";
    public TimeSpan StartTime { get; init; }
    public TimeSpan EndTime { get; init; }
}
