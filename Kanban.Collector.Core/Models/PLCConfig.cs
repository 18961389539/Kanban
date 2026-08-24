using CommunityToolkit.Mvvm.ComponentModel;
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
    [ObservableProperty] private string _model = "S1200";
    [ObservableProperty] private byte _rack;
    [ObservableProperty] private byte _slot = 1;
    [ObservableProperty] private PlcDataFormat _dataFormat = PlcDataFormat.ABCD;
    [ObservableProperty] private int _batchInt32Limit = 55;

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
    [ObservableProperty] private byte _unitId = 1;
    [ObservableProperty] private bool _addressStartWithZero = true;
    [ObservableProperty] private int _registerFunction = 3;
    [ObservableProperty] private int _bitFunction = 1;
    [ObservableProperty] private PlcDataFormat _dataFormat = PlcDataFormat.ABCD;
    [ObservableProperty] private int _batchInt32Limit = 62;

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
    [ObservableProperty] private int _readSplits = 500;

    public OmronFinsPlcOptions CreateSnapshot() => new() { ReadSplits = ReadSplits };
}

/// <summary>
/// PLC 连接配置。公共连接参数位于根级，品牌专属参数分别存放在 Siemens、ModbusTcp、Omron 中。
/// 旧扁平属性保留为 JsonIgnore 兼容代理（供现有 ViewModel 与测试代码渐进迁移），
/// JSON 序列化由 <see cref="PlcConfigJsonConverter"/> 接管：写入只输出嵌套结构，
/// 读取同时兼容嵌套结构与旧版扁平字段（旧配置无需预迁移即可加载）。
/// </summary>
[JsonConverter(typeof(PlcConfigJsonConverter))]
public partial class PlcConfig : ObservableObject
{
    public const string DefaultProtocolKey = "plc";

    private int _lastAutomaticPort = GetDefaultPort(PlcBrand.Mitsubishi);

    [ObservableProperty] private string _protocolKey = DefaultProtocolKey;
    [ObservableProperty] private PlcBrand _brand = PlcBrand.Mitsubishi;
    [ObservableProperty] private string _ipAddress = "127.0.0.1";
    [ObservableProperty] private int _port = GetDefaultPort(PlcBrand.Mitsubishi);
    [ObservableProperty] private int _timeoutMs = 5000;

    [ObservableProperty] private SiemensPlcOptions _siemens = new();
    [ObservableProperty] private ModbusTcpPlcOptions _modbusTcp = new();
    [ObservableProperty] private OmronFinsPlcOptions _omron = new();

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

    public static int GetDefaultPort(PlcBrand brand) => brand switch
    {
        PlcBrand.Mitsubishi => 4999,
        PlcBrand.Siemens => 102,
        PlcBrand.ModbusTcp => 502,
        PlcBrand.Omron => 9600,
        PlcBrand.Keyence => 5000,
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
    };

    /// <summary>
    /// 全字段配置签名：驱动重建客户端与"配置是否变化需热切换"判断的单一事实源。
    /// 直接复用 <see cref="PlcConfigJsonConverter"/> 的 Write（全字段含嵌套品牌 Options 已在转换器单一维护），
    /// 新增品牌参数只需改转换器一处，签名自动覆盖——消除"签名/转换器/快照三处手工同步"的遗漏风险。
    /// </summary>
    public string GetConfigurationSignature()
        => JsonSerializer.Serialize(this);

    partial void OnBrandChanged(PlcBrand value)
    {
        if (Port == _lastAutomaticPort)
            Port = GetDefaultPort(value);
        _lastAutomaticPort = GetDefaultPort(value);
    }

    partial void OnProtocolKeyChanged(string value)
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
