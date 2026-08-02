using Kanban.Core.Models;

namespace Kanban.Core.Services;

public sealed record BatchReadCapabilities(
    bool SupportsInt32,
    ushort MaxInt32Length,
    int Int32AddressStride,
    bool SupportsBool,
    ushort MaxBoolLength,
    int BoolAddressStride);

/// <summary>
/// 设备协议适配器：隔离采集业务与具体设备协议。
/// 新增协议时实现此接口并注册到 DI，无需修改采集循环。
/// </summary>
public interface IDeviceAdapter
{
    PlcBrand Brand { get; }
    IPlcAddressCodec AddressCodec { get; }
    BatchReadCapabilities BatchReadCapabilities { get; }
    PlcOperationResult<int> ReadInt32(string address);
    PlcOperationResult<int[]> ReadInt32Batch(string address, ushort length);
    PlcOperationResult<bool> ReadBool(string address);
    PlcOperationResult<bool[]> ReadBoolBatch(string address, ushort length);
    PlcOperationResult WriteInt32(string address, int value);
    PlcOperationResult WriteBool(string address, bool value);
}

/// <summary>按设备配置选择适配器。</summary>
public interface IDeviceAdapterResolver
{
    IDeviceAdapter Current { get; }
    IDeviceAdapter Resolve(Device device);
}

public sealed class DeviceAdapterResolver : IDeviceAdapterResolver
{
    private readonly IReadOnlyList<IDeviceAdapter> _adapters;
    private readonly AppSettings? _settings;
    private readonly IPlcRuntimeProfileProvider? _profileProvider;

    // 构造时校验：同一共享 PLC 品牌不允许注册多个适配器，避免 Resolve 时才暴露配置错误。
    public DeviceAdapterResolver(
        IEnumerable<IDeviceAdapter> adapters,
        AppSettings? settings = null,
        IPlcRuntimeProfileProvider? profileProvider = null)
    {
        _adapters = adapters.ToArray();
        _settings = settings;
        _profileProvider = profileProvider;
        var duplicates = _adapters.GroupBy(a => a.Brand).Where(g => g.Count() > 1).ToList();
        if (duplicates.Count > 0)
        {
            var detail = string.Join("、", duplicates.Select(g => $"{g.Key}({string.Join("+", g.Select(a => a.GetType().Name))})"));
            throw new InvalidOperationException(
                $"以下 PLC 品牌注册了多个适配器：{detail}。每个品牌只能注册一个适配器。");
        }
    }

    private IDeviceAdapter ResolveByBrand(PlcBrand brand)
    {
        var matches = _adapters.Where(adapter => adapter.Brand == brand).ToList();
        if (matches.Count == 0)
            throw new InvalidOperationException($"未找到 PLC 品牌 {brand} 的适配器。");
        if (matches.Count > 1)
        {
            throw new InvalidOperationException(
                $"PLC 品牌 {brand} 注册了多个适配器：{string.Join("、", matches.Select(adapter => adapter.GetType().Name))}。");
        }
        return matches[0];
    }

    public IDeviceAdapter Current
    {
        get
        {
            var brand = _profileProvider?.Current.Brand ?? _settings?.PlcConfig.Brand ?? PlcBrand.Mitsubishi;
            return ResolveByBrand(brand);
        }
    }

    public IDeviceAdapter Resolve(Device device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return Current;
    }
}

/// <summary>默认 PLC 适配器，保持现有 IPlcDriver 行为。</summary>
public sealed class PlcDeviceAdapter(
    IPlcDriver driver,
    IPlcRuntimeProfileProvider? profileProvider = null,
    IPlcAddressCodecResolver? codecResolver = null) : IDeviceAdapter
{
    private readonly IPlcRuntimeProfileProvider? _profileProvider = profileProvider;
    private readonly IPlcAddressCodecResolver? _codecResolver = codecResolver;
    private readonly IPlcAddressCodec _fallbackCodec = new MitsubishiAddressCodec();
    public PlcBrand Brand => _profileProvider?.Current.Brand ?? AddressCodec.Brand;
    public IPlcAddressCodec AddressCodec => _profileProvider?.Current.AddressCodec ?? _codecResolver?.Current ?? _fallbackCodec;
    public BatchReadCapabilities BatchReadCapabilities =>
        _profileProvider?.Current.BatchReadCapabilities ?? PlcRuntimeProfileProvider.BatchReadCapabilitiesFor(Brand);
    public PlcOperationResult<int> ReadInt32(string address)
    {
        var parsed = AddressCodec.Parse(address);
        return parsed is { IsValid: true, Type: PlcAddressType.DWord } && AddressCodec.CanRead(parsed)
            ? driver.ReadInt32(AddressCodec.ToTransportAddress(parsed.Original))
            : PlcOperationResult<int>.Fail(parsed.ErrorMessage, PlcErrorKind.InvalidAddress);
    }

    public PlcOperationResult<int[]> ReadInt32Batch(string address, ushort length)
    {
        var parsed = AddressCodec.Parse(address);
        return parsed is { IsValid: true, Type: PlcAddressType.DWord } && AddressCodec.CanRead(parsed)
            ? driver.ReadInt32Batch(AddressCodec.ToTransportAddress(parsed.Original), length)
            : PlcOperationResult<int[]>.Fail(parsed.ErrorMessage, PlcErrorKind.InvalidAddress);
    }

    public PlcOperationResult<bool> ReadBool(string address)
    {
        var parsed = AddressCodec.Parse(address);
        return parsed is { IsValid: true, Type: PlcAddressType.MBit } && AddressCodec.CanRead(parsed)
            ? driver.ReadBool(AddressCodec.ToTransportAddress(parsed.Original))
            : PlcOperationResult<bool>.Fail(parsed.ErrorMessage, PlcErrorKind.InvalidAddress);
    }

    public PlcOperationResult<bool[]> ReadBoolBatch(string address, ushort length)
    {
        var parsed = AddressCodec.Parse(address);
        return parsed is { IsValid: true, Type: PlcAddressType.MBit } && AddressCodec.CanRead(parsed)
            ? driver.ReadBoolBatch(AddressCodec.ToTransportAddress(parsed.Original), length)
            : PlcOperationResult<bool[]>.Fail(parsed.ErrorMessage, PlcErrorKind.InvalidAddress);
    }

    public PlcOperationResult WriteInt32(string address, int value)
    {
        var parsed = AddressCodec.Parse(address);
        if (parsed is not { IsValid: true, Type: PlcAddressType.DWord })
            return PlcOperationResult.Fail(parsed.ErrorMessage, PlcErrorKind.InvalidAddress);
        if (!AddressCodec.CanWrite(parsed))
            return PlcOperationResult.Fail($"地址 {parsed.Original} 所属区域只读，不支持写入", PlcErrorKind.UnsupportedOperation);
        return driver.WriteInt32(AddressCodec.ToTransportAddress(parsed.Original), value);
    }

    public PlcOperationResult WriteBool(string address, bool value)
    {
        var parsed = AddressCodec.Parse(address);
        if (parsed is not { IsValid: true, Type: PlcAddressType.MBit })
            return PlcOperationResult.Fail(parsed.ErrorMessage, PlcErrorKind.InvalidAddress);
        if (!AddressCodec.CanWrite(parsed))
            return PlcOperationResult.Fail($"地址 {parsed.Original} 所属区域只读，不支持写入", PlcErrorKind.UnsupportedOperation);
        return driver.WriteBool(AddressCodec.ToTransportAddress(parsed.Original), value);
    }
}
