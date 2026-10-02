using Kanban.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kanban.Collector.Core.Models;

public enum PlcBrand
{
    Mitsubishi = 1,
    Siemens = 2,
    ModbusTcp = 3,
    Omron = 4,
    Keyence = 5,
    Inovance = 6,
    AllenBradley = 7,
}

/// <summary>汇川 PLC 系列。AM 为中型机，H3U/H5U 为小型机，地址规则不同。</summary>
public enum InovancePlcSeries
{
    H5U = 0,
    H3U = 1,
    AM = 2,
}

public enum PlcDataFormat
{
    ABCD = 0,
    BADC = 1,
    CDAB = 2,
    DCBA = 3,
}

/// <summary>Siemens S7 专属连接与批读选项。</summary>
public partial class SiemensPlcOptions : ObservableObject
{
    private string _model = "S1200";

    public string Model
    {
        get => _model;
    set => SetProperty(ref _model, value);
    }
    private byte _rack;

    public byte Rack
    {
        get => _rack;
    set => SetProperty(ref _rack, value);
    }
    private byte _slot = 1;

    public byte Slot
    {
        get => _slot;
    set => SetProperty(ref _slot, value);
    }
    private PlcDataFormat _dataFormat = PlcDataFormat.ABCD;

    public PlcDataFormat DataFormat
    {
        get => _dataFormat;
    set => SetProperty(ref _dataFormat, value);
    }
    private int _batchInt32Limit = 55;

    public int BatchInt32Limit
    {
        get => _batchInt32Limit;
    set => SetProperty(ref _batchInt32Limit, value);
    }

    public SiemensPlcOptions CreateSnapshot() => new()
    {
        Model = Model,
        Rack = Rack,
        Slot = Slot,
        DataFormat = DataFormat,
        BatchInt32Limit = BatchInt32Limit,
    };
}

/// <summary>Modbus TCP 专属站号、寻址与数据格式选项。</summary>
public partial class ModbusTcpPlcOptions : ObservableObject
{
    private byte _unitId = 1;

    public byte UnitId
    {
        get => _unitId;
    set => SetProperty(ref _unitId, value);
    }
    private bool _addressStartWithZero = true;

    public bool AddressStartWithZero
    {
        get => _addressStartWithZero;
    set => SetProperty(ref _addressStartWithZero, value);
    }
    private int _registerFunction = 3;

    public int RegisterFunction
    {
        get => _registerFunction;
    set => SetProperty(ref _registerFunction, value);
    }
    private int _bitFunction = 1;

    public int BitFunction
    {
        get => _bitFunction;
    set => SetProperty(ref _bitFunction, value);
    }
    private PlcDataFormat _dataFormat = PlcDataFormat.ABCD;

    public PlcDataFormat DataFormat
    {
        get => _dataFormat;
    set => SetProperty(ref _dataFormat, value);
    }
    private int _batchInt32Limit = 62;

    public int BatchInt32Limit
    {
        get => _batchInt32Limit;
    set => SetProperty(ref _batchInt32Limit, value);
    }

    public ModbusTcpPlcOptions CreateSnapshot() => new()
    {
        UnitId = UnitId,
        AddressStartWithZero = AddressStartWithZero,
        RegisterFunction = RegisterFunction,
        BitFunction = BitFunction,
        DataFormat = DataFormat,
        BatchInt32Limit = BatchInt32Limit,
    };
}

/// <summary>Omron FINS 专属选项。</summary>
public partial class OmronFinsPlcOptions : ObservableObject
{
    private int _readSplits = 500;

    public int ReadSplits
    {
        get => _readSplits;
    set => SetProperty(ref _readSplits, value);
    }

    public OmronFinsPlcOptions CreateSnapshot() => new() { ReadSplits = ReadSplits };
}

/// <summary>汇川 Modbus TCP 专属系列、站号与字节序。</summary>
public partial class InovancePlcOptions : ObservableObject
{
    private InovancePlcSeries _series = InovancePlcSeries.H5U;

    public InovancePlcSeries Series
    {
        get => _series;
        set => SetProperty(ref _series, value);
    }
    private byte _station = 1;

    public byte Station
    {
        get => _station;
        set => SetProperty(ref _station, value);
    }
    private PlcDataFormat _dataFormat = PlcDataFormat.CDAB;

    public PlcDataFormat DataFormat
    {
        get => _dataFormat;
        set => SetProperty(ref _dataFormat, value);
    }
    private int _batchInt32Limit = 60;

    public int BatchInt32Limit
    {
        get => _batchInt32Limit;
        set => SetProperty(ref _batchInt32Limit, value);
    }

    public InovancePlcOptions CreateSnapshot() => new()
    {
        Series = Series,
        Station = Station,
        DataFormat = DataFormat,
        BatchInt32Limit = BatchInt32Limit,
    };
}

/// <summary>罗克韦尔 EtherNet/IP 专属槽位与连接方式。</summary>
public partial class AllenBradleyPlcOptions : ObservableObject
{
    private byte _slot;

    public byte Slot
    {
        get => _slot;
        set => SetProperty(ref _slot, value);
    }
    private bool _useConnectedCip;

    public bool UseConnectedCip
    {
        get => _useConnectedCip;
        set => SetProperty(ref _useConnectedCip, value);
    }

    public AllenBradleyPlcOptions CreateSnapshot() => new()
    {
        Slot = Slot,
        UseConnectedCip = UseConnectedCip,
    };
}

/// <summary>
/// PLC 连接配置。公共连接参数位于根级，品牌专属参数分别存放在 Siemens、ModbusTcp、Omron、Inovance、AllenBradley 中。
/// 旧扁平属性保留为 JsonIgnore 兼容代理（供现有 ViewModel 与测试代码渐进迁移），
/// JSON 序列化由 <see cref="PlcConfigJsonConverter"/> 接管：写入只输出嵌套结构，
/// 读取同时兼容嵌套结构与旧版扁平字段（旧配置无需预迁移即可加载）。
/// </summary>
[JsonConverter(typeof(PlcConfigJsonConverter))]
public partial class PlcConfig : ObservableObject
{
    public const string DefaultProtocolKey = "plc";

    private int _lastAutomaticPort = GetDefaultPort(PlcBrand.Mitsubishi);

    private string _protocolKey = DefaultProtocolKey;

    public string ProtocolKey
    {
        get => _protocolKey;
    set
    {
        if (SetProperty(ref _protocolKey, value))
            OnProtocolKeyChanged(value);
    }
    }
    private PlcBrand _brand = PlcBrand.Mitsubishi;

    public PlcBrand Brand
    {
        get => _brand;
    set
    {
        if (SetProperty(ref _brand, value))
            OnBrandChanged(value);
    }
    }
    private string _ipAddress = "127.0.0.1";

    public string IpAddress
    {
        get => _ipAddress;
    set => SetProperty(ref _ipAddress, value);
    }
    private int _port = GetDefaultPort(PlcBrand.Mitsubishi);

    public int Port
    {
        get => _port;
    set => SetProperty(ref _port, value);
    }
    private int _timeoutMs = 5000;

    public int TimeoutMs
    {
        get => _timeoutMs;
    set => SetProperty(ref _timeoutMs, value);
    }

    private SiemensPlcOptions _siemens = new();

    public SiemensPlcOptions Siemens
    {
        get => _siemens;
    set => SetProperty(ref _siemens, value);
    }
    private ModbusTcpPlcOptions _modbusTcp = new();

    public ModbusTcpPlcOptions ModbusTcp
    {
        get => _modbusTcp;
    set => SetProperty(ref _modbusTcp, value);
    }
    private OmronFinsPlcOptions _omron = new();

    public OmronFinsPlcOptions Omron
    {
        get => _omron;
    set => SetProperty(ref _omron, value);
    }
    private InovancePlcOptions _inovance = new();

    public InovancePlcOptions Inovance
    {
        get => _inovance;
        set => SetProperty(ref _inovance, value);
    }
    private AllenBradleyPlcOptions _allenBradley = new();

    public AllenBradleyPlcOptions AllenBradley
    {
        get => _allenBradley;
        set => SetProperty(ref _allenBradley, value);
    }

    // 兼容代理：运行时代码可渐进迁移到嵌套 Options；新 settings.json 不再写重复的扁平字段。
    [JsonIgnore]
    public string SiemensModel { get => Siemens.Model; set => SetOption(Siemens.Model, value, v => Siemens.Model = v); }
    [JsonIgnore]
    public byte SiemensRack { get => Siemens.Rack; set => SetOption(Siemens.Rack, value, v => Siemens.Rack = v); }
    [JsonIgnore]
    public byte SiemensSlot { get => Siemens.Slot; set => SetOption(Siemens.Slot, value, v => Siemens.Slot = v); }
    [JsonIgnore]
    public PlcDataFormat SiemensDataFormat { get => Siemens.DataFormat; set => SetOption(Siemens.DataFormat, value, v => Siemens.DataFormat = v); }
    [JsonIgnore]
    public int SiemensBatchInt32Limit { get => Siemens.BatchInt32Limit; set => SetOption(Siemens.BatchInt32Limit, value, v => Siemens.BatchInt32Limit = v); }

    [JsonIgnore]
    public byte ModbusUnitId { get => ModbusTcp.UnitId; set => SetOption(ModbusTcp.UnitId, value, v => ModbusTcp.UnitId = v); }
    [JsonIgnore]
    public bool ModbusAddressStartWithZero { get => ModbusTcp.AddressStartWithZero; set => SetOption(ModbusTcp.AddressStartWithZero, value, v => ModbusTcp.AddressStartWithZero = v); }
    [JsonIgnore]
    public int ModbusRegisterFunction { get => ModbusTcp.RegisterFunction; set => SetOption(ModbusTcp.RegisterFunction, value, v => ModbusTcp.RegisterFunction = v); }
    [JsonIgnore]
    public int ModbusBitFunction { get => ModbusTcp.BitFunction; set => SetOption(ModbusTcp.BitFunction, value, v => ModbusTcp.BitFunction = v); }
    [JsonIgnore]
    public PlcDataFormat ModbusDataFormat { get => ModbusTcp.DataFormat; set => SetOption(ModbusTcp.DataFormat, value, v => ModbusTcp.DataFormat = v); }
    [JsonIgnore]
    public int ModbusBatchInt32Limit { get => ModbusTcp.BatchInt32Limit; set => SetOption(ModbusTcp.BatchInt32Limit, value, v => ModbusTcp.BatchInt32Limit = v); }

    [JsonIgnore]
    public int OmronReadSplits { get => Omron.ReadSplits; set => SetOption(Omron.ReadSplits, value, v => Omron.ReadSplits = v); }

    [JsonIgnore]
    public InovancePlcSeries InovanceSeries { get => Inovance.Series; set => SetOption(Inovance.Series, value, v => Inovance.Series = v); }
    [JsonIgnore]
    public byte InovanceStation { get => Inovance.Station; set => SetOption(Inovance.Station, value, v => Inovance.Station = v); }
    [JsonIgnore]
    public PlcDataFormat InovanceDataFormat { get => Inovance.DataFormat; set => SetOption(Inovance.DataFormat, value, v => Inovance.DataFormat = v); }
    [JsonIgnore]
    public int InovanceBatchInt32Limit { get => Inovance.BatchInt32Limit; set => SetOption(Inovance.BatchInt32Limit, value, v => Inovance.BatchInt32Limit = v); }

    [JsonIgnore]
    public byte AllenBradleySlot { get => AllenBradley.Slot; set => SetOption(AllenBradley.Slot, value, v => AllenBradley.Slot = v); }
    [JsonIgnore]
    public bool AllenBradleyUseConnectedCip { get => AllenBradley.UseConnectedCip; set => SetOption(AllenBradley.UseConnectedCip, value, v => AllenBradley.UseConnectedCip = v); }

    public static int GetDefaultPort(PlcBrand brand) => brand switch
    {
        PlcBrand.Mitsubishi => 4999,
        PlcBrand.Siemens => 102,
        PlcBrand.ModbusTcp => 502,
        PlcBrand.Omron => 9600,
        PlcBrand.Keyence => 5000,
        PlcBrand.Inovance => 502,
        PlcBrand.AllenBradley => 44818,
        _ => 4999,
    };

    public PlcConfig CreateSnapshot() => new()
    {
        ProtocolKey = ProtocolKey,
        Brand = Brand,
        IpAddress = IpAddress,
        Port = Port,
        TimeoutMs = TimeoutMs,
        Siemens = Siemens.CreateSnapshot(),
        ModbusTcp = ModbusTcp.CreateSnapshot(),
        Omron = Omron.CreateSnapshot(),
        Inovance = Inovance.CreateSnapshot(),
        AllenBradley = AllenBradley.CreateSnapshot(),
    };

    /// <summary>
    /// 全字段配置签名：驱动重建客户端与"配置是否变化需热切换"判断的单一事实源。
    /// 直接复用 <see cref="PlcConfigJsonConverter"/> 的 Write（全字段含嵌套品牌 Options 已在转换器单一维护），
    /// 新增品牌参数只需改转换器一处，签名自动覆盖——消除"签名/转换器/快照三处手工同步"的遗漏风险。
    /// </summary>
    public string GetConfigurationSignature()
        => JsonSerializer.Serialize(this);

    private void OnBrandChanged(PlcBrand value)
    {
        if (Port == _lastAutomaticPort)
            Port = GetDefaultPort(value);
        _lastAutomaticPort = GetDefaultPort(value);
    }

    private void OnProtocolKeyChanged(string value)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? DefaultProtocolKey
            : value.Trim().ToLowerInvariant();
        if (!string.Equals(value, normalized, StringComparison.Ordinal))
            ProtocolKey = normalized;
    }

    private void SetOption<T>(T current, T value, Action<T> setter, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return;
        setter(value);
        OnPropertyChanged(propertyName);
    }
}

/// <summary>
/// PlcConfig 的 JSON 转换器（过渡期实现）：
/// - 写入：仅输出公共参数 + 嵌套 Options，不写扁平兼容字段，保证新配置文件为整洁结构；
/// - 读取：优先填充嵌套 Options，再按旧扁平字段覆盖（兼容旧 settings.json，无需预迁移）；
/// 新增品牌参数时需同步补充本转换器字段与 <see cref="CreateSnapshot"/>。
/// </summary>
public sealed class PlcConfigJsonConverter : System.Text.Json.Serialization.JsonConverter<PlcConfig>
{
    public override PlcConfig Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(ref reader) as System.Text.Json.Nodes.JsonObject
            ?? throw new System.Text.Json.JsonException("PlcConfig 必须是 JSON 对象。");
        var config = new PlcConfig();

        if (TryReadInt(root, "Brand", out var brand) && Enum.IsDefined(typeof(PlcBrand), brand))
            config.Brand = (PlcBrand)brand;
        if (root["ProtocolKey"] is System.Text.Json.Nodes.JsonValue protocolValue
            && protocolValue.TryGetValue<string>(out var protocolKey))
            config.ProtocolKey = protocolKey;
        if (root["IpAddress"] is System.Text.Json.Nodes.JsonValue ipValue && ipValue.TryGetValue<string>(out var ip))
            config.IpAddress = ip;
        if (TryReadInt(root, "Port", out var port))
            config.Port = port;
        if (TryReadInt(root, "TimeoutMs", out var timeout))
            config.TimeoutMs = timeout;

        if (root["Siemens"] is System.Text.Json.Nodes.JsonObject siemens)
        {
            if (siemens["Model"] is System.Text.Json.Nodes.JsonValue sv && sv.TryGetValue<string>(out var model))
                config.Siemens.Model = model;
            if (TryReadInt(siemens, "Rack", out var rack))
                config.Siemens.Rack = (byte)rack;
            if (TryReadInt(siemens, "Slot", out var slot))
                config.Siemens.Slot = (byte)slot;
            if (TryReadInt(siemens, "DataFormat", out var sFormat) && Enum.IsDefined(typeof(PlcDataFormat), sFormat))
                config.Siemens.DataFormat = (PlcDataFormat)sFormat;
            if (TryReadInt(siemens, "BatchInt32Limit", out var sBatch))
                config.Siemens.BatchInt32Limit = sBatch;
        }
        if (root["ModbusTcp"] is System.Text.Json.Nodes.JsonObject modbus)
        {
            if (TryReadInt(modbus, "UnitId", out var unitId))
                config.ModbusTcp.UnitId = (byte)unitId;
            if (modbus["AddressStartWithZero"] is System.Text.Json.Nodes.JsonValue azw && azw.TryGetValue<bool>(out var startWithZero))
                config.ModbusTcp.AddressStartWithZero = startWithZero;
            if (TryReadInt(modbus, "RegisterFunction", out var regFunction))
                config.ModbusTcp.RegisterFunction = regFunction;
            if (TryReadInt(modbus, "BitFunction", out var bitFunction))
                config.ModbusTcp.BitFunction = bitFunction;
            if (TryReadInt(modbus, "DataFormat", out var mFormat) && Enum.IsDefined(typeof(PlcDataFormat), mFormat))
                config.ModbusTcp.DataFormat = (PlcDataFormat)mFormat;
            if (TryReadInt(modbus, "BatchInt32Limit", out var mBatch))
                config.ModbusTcp.BatchInt32Limit = mBatch;
        }
        if (root["Omron"] is System.Text.Json.Nodes.JsonObject omron && TryReadInt(omron, "ReadSplits", out var readSplits))
            config.Omron.ReadSplits = readSplits;
        if (root["Inovance"] is System.Text.Json.Nodes.JsonObject inovance)
        {
            if (TryReadInt(inovance, "Series", out var series) && Enum.IsDefined(typeof(InovancePlcSeries), series))
                config.Inovance.Series = (InovancePlcSeries)series;
            if (TryReadInt(inovance, "Station", out var station))
                config.Inovance.Station = (byte)station;
            if (TryReadInt(inovance, "DataFormat", out var iFormat) && Enum.IsDefined(typeof(PlcDataFormat), iFormat))
                config.Inovance.DataFormat = (PlcDataFormat)iFormat;
            if (TryReadInt(inovance, "BatchInt32Limit", out var iBatch))
                config.Inovance.BatchInt32Limit = iBatch;
        }
        if (root["AllenBradley"] is System.Text.Json.Nodes.JsonObject allenBradley)
        {
            if (TryReadInt(allenBradley, "Slot", out var abSlot))
                config.AllenBradley.Slot = (byte)abSlot;
            if (allenBradley["UseConnectedCip"] is System.Text.Json.Nodes.JsonValue cip && cip.TryGetValue<bool>(out var useConnected))
                config.AllenBradley.UseConnectedCip = useConnected;
        }

        // 旧版扁平字段兼容：嵌套对象优先，扁平字段仅在其未写入嵌套值时覆盖（旧文件只有扁平字段）。
        ApplyLegacyFlatFields(config, root);

        return config;
    }

    public override void Write(Utf8JsonWriter writer, PlcConfig value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("ProtocolKey", value.ProtocolKey);
        writer.WriteNumber("Brand", (int)value.Brand);
        writer.WriteString("IpAddress", value.IpAddress);
        writer.WriteNumber("Port", value.Port);
        writer.WriteNumber("TimeoutMs", value.TimeoutMs);

        writer.WritePropertyName("Siemens");
        writer.WriteStartObject();
        writer.WriteString("Model", value.Siemens.Model);
        writer.WriteNumber("Rack", value.Siemens.Rack);
        writer.WriteNumber("Slot", value.Siemens.Slot);
        writer.WriteNumber("DataFormat", (int)value.Siemens.DataFormat);
        writer.WriteNumber("BatchInt32Limit", value.Siemens.BatchInt32Limit);
        writer.WriteEndObject();

        writer.WritePropertyName("ModbusTcp");
        writer.WriteStartObject();
        writer.WriteNumber("UnitId", value.ModbusTcp.UnitId);
        writer.WriteBoolean("AddressStartWithZero", value.ModbusTcp.AddressStartWithZero);
        writer.WriteNumber("RegisterFunction", value.ModbusTcp.RegisterFunction);
        writer.WriteNumber("BitFunction", value.ModbusTcp.BitFunction);
        writer.WriteNumber("DataFormat", (int)value.ModbusTcp.DataFormat);
        writer.WriteNumber("BatchInt32Limit", value.ModbusTcp.BatchInt32Limit);
        writer.WriteEndObject();

        writer.WritePropertyName("Omron");
        writer.WriteStartObject();
        writer.WriteNumber("ReadSplits", value.Omron.ReadSplits);
        writer.WriteEndObject();

        writer.WritePropertyName("Inovance");
        writer.WriteStartObject();
        writer.WriteNumber("Series", (int)value.Inovance.Series);
        writer.WriteNumber("Station", value.Inovance.Station);
        writer.WriteNumber("DataFormat", (int)value.Inovance.DataFormat);
        writer.WriteNumber("BatchInt32Limit", value.Inovance.BatchInt32Limit);
        writer.WriteEndObject();

        writer.WritePropertyName("AllenBradley");
        writer.WriteStartObject();
        writer.WriteNumber("Slot", value.AllenBradley.Slot);
        writer.WriteBoolean("UseConnectedCip", value.AllenBradley.UseConnectedCip);
        writer.WriteEndObject();

        writer.WriteEndObject();
    }

    private static void ApplyLegacyFlatFields(PlcConfig config, System.Text.Json.Nodes.JsonObject root)
    {
        if (root["SiemensModel"] is System.Text.Json.Nodes.JsonValue model && model.TryGetValue<string>(out var modelValue))
            config.Siemens.Model = modelValue;
        if (TryReadInt(root, "SiemensRack", out var rack))
            config.Siemens.Rack = (byte)rack;
        if (TryReadInt(root, "SiemensSlot", out var slot))
            config.Siemens.Slot = (byte)slot;
        if (TryReadInt(root, "SiemensDataFormat", out var sFormat) && Enum.IsDefined(typeof(PlcDataFormat), sFormat))
            config.Siemens.DataFormat = (PlcDataFormat)sFormat;
        if (TryReadInt(root, "SiemensBatchInt32Limit", out var sBatch))
            config.Siemens.BatchInt32Limit = sBatch;

        if (TryReadInt(root, "ModbusUnitId", out var unitId))
            config.ModbusTcp.UnitId = (byte)unitId;
        if (root["ModbusAddressStartWithZero"] is System.Text.Json.Nodes.JsonValue azw && azw.TryGetValue<bool>(out var startWithZero))
            config.ModbusTcp.AddressStartWithZero = startWithZero;
        if (TryReadInt(root, "ModbusRegisterFunction", out var regFunction))
            config.ModbusTcp.RegisterFunction = regFunction;
        if (TryReadInt(root, "ModbusBitFunction", out var bitFunction))
            config.ModbusTcp.BitFunction = bitFunction;
        if (TryReadInt(root, "ModbusDataFormat", out var mFormat) && Enum.IsDefined(typeof(PlcDataFormat), mFormat))
            config.ModbusTcp.DataFormat = (PlcDataFormat)mFormat;
        if (TryReadInt(root, "ModbusBatchInt32Limit", out var mBatch))
            config.ModbusTcp.BatchInt32Limit = mBatch;

        if (TryReadInt(root, "OmronReadSplits", out var readSplits))
            config.Omron.ReadSplits = readSplits;
    }

    private static bool TryReadInt(System.Text.Json.Nodes.JsonObject obj, string propertyName, out int value)
    {
        if (obj[propertyName] is System.Text.Json.Nodes.JsonValue jsonValue && jsonValue.TryGetValue<int>(out value))
            return true;
        value = 0;
        return false;
    }
}
