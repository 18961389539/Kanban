using System.Collections.Concurrent;
using Kanban.Collector.Core.Localization;
using Kanban.Collector.Core.Models;

namespace Kanban.Collector.Core.Services;

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
    /// <summary>协议实现标识，用于选择数据源 reader；未扩展的旧适配器默认为 PLC。</summary>
    string ProtocolKey => DataSourceProtocolKeys.Plc;
    /// <summary>绑定的连接档案；旧的非 keyed adapter 统一视为 default。</summary>
    string ConnectionProfileId => ConnectionProfile.DefaultId;
    PlcBrand Brand { get; }
    IPlcAddressCodec AddressCodec { get; }
    BatchReadCapabilities BatchReadCapabilities { get; }
    PlcOperationResult<int> ReadInt32(string address);
    PlcOperationResult<int[]> ReadInt32Batch(string address, ushort length);
    PlcOperationResult<bool> ReadBool(string address);
    PlcOperationResult<bool[]> ReadBoolBatch(string address, ushort length);
    PlcOperationResult<float> ReadFloat(string address);
    PlcOperationResult<float[]> ReadFloatBatch(string address, ushort length);
    PlcOperationResult<ushort> ReadUInt16(string address);
    PlcOperationResult<string> ReadString(string address, ushort length);
    PlcOperationResult WriteInt32(string address, int value);
    PlcOperationResult WriteBool(string address, bool value);
    PlcOperationResult WriteFloat(string address, float value);
    PlcOperationResult WriteUInt16(string address, ushort value);
    PlcOperationResult WriteString(string address, string value);
}

/// <summary>按设备配置选择适配器。</summary>
public interface IDeviceAdapterResolver
{
    IDeviceAdapter Current { get; }
    IDeviceAdapter Resolve(Device device);
}

internal interface IProfileBoundDeviceAdapter
{
    bool CanBind(ConnectionProfile profile);
    IDeviceAdapter BindToProfile(ConnectionProfile profile);
}

public sealed class DeviceAdapterResolver : IDeviceAdapterResolver
{
    private readonly IReadOnlyList<IDeviceAdapter> _adapters;
    private readonly AppSettings? _settings;
    private readonly IPlcRuntimeProfileProvider? _profileProvider;
    private readonly ConcurrentDictionary<string, IDeviceAdapter> _profileBindings = new(StringComparer.OrdinalIgnoreCase);

    // 构造时校验：同一协议/品牌组合不允许注册多个适配器，避免 Resolve 时才暴露配置错误。
    public DeviceAdapterResolver(
        IEnumerable<IDeviceAdapter> adapters,
        AppSettings? settings = null,
        IPlcRuntimeProfileProvider? profileProvider = null)
    {
        _adapters = adapters.ToArray();
        _settings = settings;
        _profileProvider = profileProvider;
        var duplicates = _adapters
            .GroupBy(a => (ProtocolKey: NormalizeProtocolKey(a.ProtocolKey), a.Brand))
            .Where(g => g.Count() > 1)
            .ToList();
        if (duplicates.Count > 0)
        {
            var detail = string.Join("、", duplicates.Select(g =>
                $"{g.Key.ProtocolKey}/{g.Key.Brand}({string.Join("+", g.Select(a => a.GetType().Name))})"));
            throw new InvalidOperationException(
                $"以下协议/PLC 品牌组合注册了多个适配器：{detail}。每个组合只能注册一个适配器。");
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

    private IDeviceAdapter ResolveByProfile(ConnectionProfile profile)
    {
        var protocolKey = NormalizeProtocolKey(profile.Config.ProtocolKey);
        var matches = _adapters.Where(adapter =>
                adapter is IProfileBoundDeviceAdapter bindable && bindable.CanBind(profile)
                || string.Equals(NormalizeProtocolKey(adapter.ProtocolKey), protocolKey, StringComparison.OrdinalIgnoreCase)
                && adapter.Brand == profile.Config.Brand)
            .ToList();
        if (matches.Count == 1)
        {
            if (matches[0] is not IProfileBoundDeviceAdapter bindable)
                return matches[0];
            var profileId = profile.Id.Trim();
            return _profileBindings.GetOrAdd(profileId, _ => bindable.BindToProfile(profile));
        }
        if (matches.Count > 1)
        {
            throw new InvalidOperationException(
                string.Format(
                    ValidationMessages.ConnectionProfileAdapterAmbiguous,
                    profile.Id,
                    protocolKey,
                    profile.Config.Brand,
                    string.Join("、", matches.Select(adapter => adapter.GetType().Name))));
        }

        throw new InvalidOperationException(
            string.Format(
                ValidationMessages.ConnectionProfileAdapterNotFound,
                profile.Id,
                protocolKey,
                profile.Config.Brand));
    }

    private static string NormalizeProtocolKey(string? protocolKey)
        => string.IsNullOrWhiteSpace(protocolKey)
            ? DataSourceProtocolKeys.Plc
            : protocolKey.Trim().ToLowerInvariant();

    public IDeviceAdapter Current
    {
        get
        {
            if (_settings is not null)
                return ResolveByProfile(_settings.DefaultConnectionProfile);
            if (_profileProvider is not null)
            {
                var current = _profileProvider.Current;
                return ResolveByProfile(new ConnectionProfile
                {
                    Id = ConnectionProfile.DefaultId,
                    Config = current.Config,
                });
            }
            return ResolveByBrand(PlcBrand.Mitsubishi);
        }
    }

    public IDeviceAdapter Resolve(Device device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (_settings is null)
            return Current;

        var profile = _settings.FindConnectionProfile(device.ConnectionProfileId)
            ?? throw new InvalidOperationException(
                string.Format(
                    ValidationMessages.DeviceConnectionProfileMissing,
                    device.Name,
                    device.ConnectionProfileId));
        return ResolveByProfile(profile);
    }
}

/// <summary>默认 PLC 适配器，保持现有 IPlcDriver 行为。</summary>
public sealed class PlcDeviceAdapter : IDeviceAdapter, IProfileBoundDeviceAdapter
{
    private readonly IPlcDriver _driver;
    private readonly IPlcRuntimeProfileProvider? _profileProvider;
    private readonly IPlcAddressCodecResolver? _codecResolver;
    private readonly IPlcRuntimeSessionManager? _sessionManager;
    private readonly string _profileId;
    private readonly IPlcAddressCodec _fallbackCodec = new MitsubishiAddressCodec();

    // 运行时 session/profile/driver 字段级缓存：adapter 经 DeviceAdapterResolver._profileBindings
    // 按档案缓存（长生命周期），避免每次读写都调 sessionManager.Get()（全局锁 + 配置查找）。
    // profile 按 Version 失效（Provider.Refresh 时递增），driver/session 引用在 session 生命周期内稳定。
    private PlcRuntimeSession? _cachedSession;
    private IPlcDriver? _cachedDriver;
    private PlcRuntimeProfile? _cachedProfile;
    private long _cachedProfileVersion = -1;

    public PlcDeviceAdapter(
        IPlcDriver driver,
        IPlcRuntimeProfileProvider? profileProvider = null,
        IPlcAddressCodecResolver? codecResolver = null,
        IPlcRuntimeSessionManager? sessionManager = null,
        string? profileId = null)
    {
        _driver = driver;
        _profileProvider = profileProvider;
        _codecResolver = codecResolver;
        _sessionManager = sessionManager;
        _profileId = PlcRuntimeSession.NormalizeProfileId(profileId);
    }

    private PlcRuntimeSession? RuntimeSession => _cachedSession ??= _sessionManager?.Get(_profileId);

    private PlcRuntimeProfile? RuntimeProfile
    {
        get
        {
            var session = RuntimeSession;
            if (session == null) return _profileProvider?.Current;
            var profile = session.Profile;
            if (profile.Version != _cachedProfileVersion)
            {
                _cachedProfile = profile;
                _cachedProfileVersion = profile.Version;
            }
            return _cachedProfile;
        }
    }

    private IPlcDriver RuntimeDriver => _cachedDriver ??= RuntimeSession?.Driver ?? _driver;

    public bool CanBind(ConnectionProfile profile)
        => _sessionManager is not null
           && string.Equals(
               NormalizeProtocolKey(profile.Config.ProtocolKey),
               DataSourceProtocolKeys.Plc,
               StringComparison.OrdinalIgnoreCase);

    public IDeviceAdapter BindToProfile(ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!CanBind(profile))
            return this;
        return new PlcDeviceAdapter(_driver, _profileProvider, _codecResolver, _sessionManager, profile.Id);
    }

    private static string NormalizeProtocolKey(string? protocolKey)
        => string.IsNullOrWhiteSpace(protocolKey)
            ? DataSourceProtocolKeys.Plc
            : protocolKey.Trim().ToLowerInvariant();

    public string ProtocolKey
    {
        get
        {
            var protocolKey = RuntimeProfile?.Config.ProtocolKey;
            return string.IsNullOrWhiteSpace(protocolKey)
                ? DataSourceProtocolKeys.Plc
                : protocolKey.Trim().ToLowerInvariant();
        }
    }
    public string ConnectionProfileId => _profileId;
    public PlcBrand Brand => RuntimeProfile?.Brand ?? AddressCodec.Brand;
    public IPlcAddressCodec AddressCodec => RuntimeProfile?.AddressCodec ?? _codecResolver?.Current ?? _fallbackCodec;
    public BatchReadCapabilities BatchReadCapabilities =>
        RuntimeProfile?.BatchReadCapabilities ?? PlcRuntimeProfileProvider.BatchReadCapabilitiesFor(Brand);
    public PlcOperationResult<int> ReadInt32(string address)
    {
        var parsed = AddressCodec.Parse(address);
        return parsed is { IsValid: true, Type: PlcAddressType.DWord } && AddressCodec.CanRead(parsed)
            ? RuntimeDriver.ReadInt32(AddressCodec.ToTransportAddress(parsed.Original))
            : PlcOperationResult<int>.Fail(parsed.ErrorMessage, PlcErrorKind.InvalidAddress);
    }

    public PlcOperationResult<int[]> ReadInt32Batch(string address, ushort length)
    {
        var parsed = AddressCodec.Parse(address);
        return parsed is { IsValid: true, Type: PlcAddressType.DWord } && AddressCodec.CanRead(parsed)
            ? RuntimeDriver.ReadInt32Batch(AddressCodec.ToTransportAddress(parsed.Original), length)
            : PlcOperationResult<int[]>.Fail(parsed.ErrorMessage, PlcErrorKind.InvalidAddress);
    }

    public PlcOperationResult<bool> ReadBool(string address)
    {
        var parsed = AddressCodec.Parse(address);
        return parsed is { IsValid: true, Type: PlcAddressType.MBit } && AddressCodec.CanRead(parsed)
            ? RuntimeDriver.ReadBool(AddressCodec.ToTransportAddress(parsed.Original))
            : PlcOperationResult<bool>.Fail(parsed.ErrorMessage, PlcErrorKind.InvalidAddress);
    }

    public PlcOperationResult<bool[]> ReadBoolBatch(string address, ushort length)
    {
        var parsed = AddressCodec.Parse(address);
        return parsed is { IsValid: true, Type: PlcAddressType.MBit } && AddressCodec.CanRead(parsed)
            ? RuntimeDriver.ReadBoolBatch(AddressCodec.ToTransportAddress(parsed.Original), length)
            : PlcOperationResult<bool[]>.Fail(parsed.ErrorMessage, PlcErrorKind.InvalidAddress);
    }

    public PlcOperationResult<float> ReadFloat(string address)
    {
        var parsed = AddressCodec.Parse(address);
        return parsed is { IsValid: true, Type: PlcAddressType.DWord } && AddressCodec.CanRead(parsed)
            ? RuntimeDriver.ReadFloat(AddressCodec.ToTransportAddress(parsed.Original))
            : PlcOperationResult<float>.Fail(parsed.ErrorMessage, PlcErrorKind.InvalidAddress);
    }

    public PlcOperationResult<float[]> ReadFloatBatch(string address, ushort length)
    {
        var parsed = AddressCodec.Parse(address);
        return parsed is { IsValid: true, Type: PlcAddressType.DWord } && AddressCodec.CanRead(parsed)
            ? RuntimeDriver.ReadFloatBatch(AddressCodec.ToTransportAddress(parsed.Original), length)
            : PlcOperationResult<float[]>.Fail(parsed.ErrorMessage, PlcErrorKind.InvalidAddress);
    }

    public PlcOperationResult<string> ReadString(string address, ushort length)
    {
        var parsed = AddressCodec.Parse(address);
        return parsed is { IsValid: true, Type: PlcAddressType.DWord } && AddressCodec.CanRead(parsed)
            ? RuntimeDriver.ReadString(AddressCodec.ToTransportAddress(parsed.Original), length)
            : PlcOperationResult<string>.Fail(parsed.ErrorMessage, PlcErrorKind.InvalidAddress);
    }

    public PlcOperationResult<ushort> ReadUInt16(string address)
    {
        var parsed = AddressCodec.Parse(address);
        return parsed is { IsValid: true, Type: PlcAddressType.DWord } && AddressCodec.CanRead(parsed)
            ? RuntimeDriver.ReadUInt16(AddressCodec.ToTransportAddress(parsed.Original))
            : PlcOperationResult<ushort>.Fail(parsed.ErrorMessage, PlcErrorKind.InvalidAddress);
    }

    public PlcOperationResult WriteInt32(string address, int value)
    {
        var parsed = AddressCodec.Parse(address);
        if (parsed is not { IsValid: true, Type: PlcAddressType.DWord })
            return PlcOperationResult.Fail(parsed.ErrorMessage, PlcErrorKind.InvalidAddress);
        if (!AddressCodec.CanWrite(parsed))
            return PlcOperationResult.Fail(string.Format(Kanban.Collector.Core.Localization.RecipeValidationMessages.RecipeAddressReadonly, parsed.Original), PlcErrorKind.UnsupportedOperation);
        return RuntimeDriver.WriteInt32(AddressCodec.ToTransportAddress(parsed.Original), value);
    }

    public PlcOperationResult WriteBool(string address, bool value)
    {
        var parsed = AddressCodec.Parse(address);
        if (parsed is not { IsValid: true, Type: PlcAddressType.MBit })
            return PlcOperationResult.Fail(parsed.ErrorMessage, PlcErrorKind.InvalidAddress);
        if (!AddressCodec.CanWrite(parsed))
            return PlcOperationResult.Fail(string.Format(Kanban.Collector.Core.Localization.RecipeValidationMessages.RecipeAddressReadonly, parsed.Original), PlcErrorKind.UnsupportedOperation);
        return RuntimeDriver.WriteBool(AddressCodec.ToTransportAddress(parsed.Original), value);
    }

    public PlcOperationResult WriteFloat(string address, float value)
    {
        var parsed = AddressCodec.Parse(address);
        if (parsed is not { IsValid: true, Type: PlcAddressType.DWord })
            return PlcOperationResult.Fail(parsed.ErrorMessage, PlcErrorKind.InvalidAddress);
        if (!AddressCodec.CanWrite(parsed))
            return PlcOperationResult.Fail(string.Format(Kanban.Collector.Core.Localization.RecipeValidationMessages.RecipeAddressReadonly, parsed.Original), PlcErrorKind.UnsupportedOperation);
        return RuntimeDriver.WriteFloat(AddressCodec.ToTransportAddress(parsed.Original), value);
    }

    public PlcOperationResult WriteUInt16(string address, ushort value)
    {
        var parsed = AddressCodec.Parse(address);
        if (parsed is not { IsValid: true, Type: PlcAddressType.DWord })
            return PlcOperationResult.Fail(parsed.ErrorMessage, PlcErrorKind.InvalidAddress);
        if (!AddressCodec.CanWrite(parsed))
            return PlcOperationResult.Fail(string.Format(Kanban.Collector.Core.Localization.RecipeValidationMessages.RecipeAddressReadonly, parsed.Original), PlcErrorKind.UnsupportedOperation);
        return RuntimeDriver.WriteUInt16(AddressCodec.ToTransportAddress(parsed.Original), value);
    }

    public PlcOperationResult WriteString(string address, string value)
    {
        var parsed = AddressCodec.Parse(address);
        if (parsed is not { IsValid: true, Type: PlcAddressType.DWord })
            return PlcOperationResult.Fail(parsed.ErrorMessage, PlcErrorKind.InvalidAddress);
        if (!AddressCodec.CanWrite(parsed))
            return PlcOperationResult.Fail(string.Format(Kanban.Collector.Core.Localization.RecipeValidationMessages.RecipeAddressReadonly, parsed.Original), PlcErrorKind.UnsupportedOperation);
        return RuntimeDriver.WriteString(AddressCodec.ToTransportAddress(parsed.Original), value);
    }
}
