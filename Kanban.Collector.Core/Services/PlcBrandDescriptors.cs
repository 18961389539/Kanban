using Kanban.Collector.Core.Localization;
using Kanban.Core.Models;
using Microsoft.Extensions.Logging;

namespace Kanban.Core.Services;

/// <summary>
/// PLC 品牌能力的单一扩展点。新增品牌时注册一个描述符即可向驱动工厂、地址解析、
/// RuntimeProfile 与配置验证同时提供实现，避免在多个中心化 switch 中重复修改。
/// </summary>
public interface IPlcBrandDescriptor
{
    PlcBrand Brand { get; }
    int DefaultPort { get; }
    IPlcDriver CreateDriver(PlcConfig config, ILoggerFactory loggerFactory);
    IPlcAddressCodec CreateAddressCodec(PlcConfig config);
    BatchReadCapabilities GetBatchReadCapabilities(PlcConfig config);
    void Validate(PlcConfig config, ICollection<string> errors);
    /// <summary>
    /// 把协议错误码/错误消息分类为统一 <see cref="PlcErrorKind"/>。
    /// 基类默认：先匹配品牌无关的 Socket 错误码，再匹配品牌协议错误码表，最后回退消息文本。
    /// </summary>
    PlcErrorKind ClassifyError(int? errorCode, string? message);
}

public interface IPlcBrandRegistry
{
    IReadOnlyCollection<IPlcBrandDescriptor> Descriptors { get; }
    IPlcBrandDescriptor Resolve(PlcBrand brand);
}

public sealed class PlcBrandRegistry : IPlcBrandRegistry
{
    private readonly IReadOnlyDictionary<PlcBrand, IPlcBrandDescriptor> _descriptors;

    public PlcBrandRegistry(IEnumerable<IPlcBrandDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        var materialized = descriptors.ToArray();
        var duplicates = materialized.GroupBy(item => item.Brand).Where(group => group.Count() > 1).ToArray();
        if (duplicates.Length > 0)
            throw new InvalidOperationException($"PLC 品牌描述符重复注册：{string.Join("、", duplicates.Select(group => group.Key))}");

        _descriptors = materialized.ToDictionary(item => item.Brand);
    }

    public IReadOnlyCollection<IPlcBrandDescriptor> Descriptors => _descriptors.Values.ToArray();

    public IPlcBrandDescriptor Resolve(PlcBrand brand) => _descriptors.TryGetValue(brand, out var descriptor)
        ? descriptor
        : throw new InvalidOperationException($"未注册 PLC 品牌 {brand} 的描述符。");
}

internal abstract class PlcBrandDescriptorBase : IPlcBrandDescriptor
{
    public abstract PlcBrand Brand { get; }
    public virtual int DefaultPort => PlcConfig.GetDefaultPort(Brand);
    public abstract IPlcDriver CreateDriver(PlcConfig config, ILoggerFactory loggerFactory);
    public abstract IPlcAddressCodec CreateAddressCodec(PlcConfig config);
    public abstract BatchReadCapabilities GetBatchReadCapabilities(PlcConfig config);
    public virtual void Validate(PlcConfig config, ICollection<string> errors) { }

    public virtual PlcErrorKind ClassifyError(int? errorCode, string? message)
    {
        if (errorCode is int code && code != 0)
        {
            // Socket 层错误码所有协议共用，优先匹配
            var socketKind = PlcErrorClassifier.ClassifySocketErrorCode(code);
            if (socketKind.HasValue) return socketKind.Value;

            var codeKind = ClassifyErrorCode(code);
            if (codeKind.HasValue) return codeKind.Value;
        }
        return PlcErrorClassifier.FromMessage(message);
    }

    /// <summary>品牌协议错误码表；返回 null 表示该错误码无结构化分类，回退消息文本。</summary>
    protected virtual PlcErrorKind? ClassifyErrorCode(int errorCode) => null;
}

internal sealed class MitsubishiPlcBrandDescriptor : PlcBrandDescriptorBase
{
    public override PlcBrand Brand => PlcBrand.Mitsubishi;
    public override IPlcDriver CreateDriver(PlcConfig config, ILoggerFactory loggerFactory) =>
        new HslPlcDriver(config, loggerFactory.CreateLogger<HslPlcDriver>());
    public override IPlcAddressCodec CreateAddressCodec(PlcConfig config) => new MitsubishiAddressCodec();
    public override BatchReadCapabilities GetBatchReadCapabilities(PlcConfig config) =>
        new(true, 480, 2, true, 2000, 1);
}

internal sealed class SiemensPlcBrandDescriptor : PlcBrandDescriptorBase
{
    private static readonly string[] SupportedModels = ["S1200", "S1500", "S300", "S400", "S200SMART", "S200"];

    public override PlcBrand Brand => PlcBrand.Siemens;
    public override IPlcDriver CreateDriver(PlcConfig config, ILoggerFactory loggerFactory) =>
        new HslSiemensPlcDriver(config, loggerFactory.CreateLogger<HslSiemensPlcDriver>());
    public override IPlcAddressCodec CreateAddressCodec(PlcConfig config) => new SiemensAddressCodec();
    public override BatchReadCapabilities GetBatchReadCapabilities(PlcConfig config) =>
        new(true, (ushort)Math.Clamp(config.Siemens.BatchInt32Limit, 1, 55), 4, true, 2000, 1);

    public override void Validate(PlcConfig config, ICollection<string> errors)
    {
        var options = config.Siemens;
        if (!Enum.IsDefined(options.DataFormat))
            errors.Add(string.Format(ValidationMessages.SiemensDataFormatInvalid, options.DataFormat));
        if (!SupportedModels.Contains(options.Model, StringComparer.OrdinalIgnoreCase))
            errors.Add(string.Format(ValidationMessages.SiemensModelUnsupported, options.Model));
        if (options.Rack > 7)
            errors.Add(string.Format(ValidationMessages.SiemensRackOutOfRange, options.Rack));
        if (options.Slot > 31)
            errors.Add(string.Format(ValidationMessages.SiemensSlotOutOfRange, options.Slot));
        if (options.BatchInt32Limit is < 1 or > 55)
            errors.Add(string.Format(ValidationMessages.SiemensBatchInt32LimitOutOfRange, options.BatchInt32Limit));
    }
}

internal sealed class ModbusTcpPlcBrandDescriptor : PlcBrandDescriptorBase
{
    public override PlcBrand Brand => PlcBrand.ModbusTcp;
    public override IPlcDriver CreateDriver(PlcConfig config, ILoggerFactory loggerFactory) =>
        new HslModbusTcpDriver(config, loggerFactory.CreateLogger<HslModbusTcpDriver>());
    public override IPlcAddressCodec CreateAddressCodec(PlcConfig config) => new ModbusTcpAddressCodec(config);
    public override BatchReadCapabilities GetBatchReadCapabilities(PlcConfig config) =>
        new(true, (ushort)Math.Clamp(config.ModbusTcp.BatchInt32Limit, 1, 2000), 2, true, 2000, 1);

    public override void Validate(PlcConfig config, ICollection<string> errors)
    {
        var options = config.ModbusTcp;
        if (options.UnitId is < 1 or > 247)
            errors.Add(string.Format(ValidationMessages.ModbusUnitIdOutOfRange, options.UnitId));
        if (options.RegisterFunction is not (3 or 4))
            errors.Add(string.Format(ValidationMessages.ModbusRegisterFunctionInvalid, options.RegisterFunction));
        if (options.BitFunction is not (1 or 2))
            errors.Add(string.Format(ValidationMessages.ModbusBitFunctionInvalid, options.BitFunction));
        if (!Enum.IsDefined(options.DataFormat))
            errors.Add(string.Format(ValidationMessages.ModbusDataFormatInvalid, options.DataFormat));
        if (options.BatchInt32Limit is < 1 or > 2000)
            errors.Add(string.Format(ValidationMessages.ModbusBatchInt32LimitOutOfRange, options.BatchInt32Limit));
    }

    protected override PlcErrorKind? ClassifyErrorCode(int errorCode) => errorCode switch
    {
        // Modbus 异常响应的功能码 | 0x80 + 异常码（1~7）；
        // HslCommunication ModbusTcpNet 把异常码原样回填到 OperateResult.ErrorCode。
        1 => PlcErrorKind.UnsupportedOperation,   // 非法功能码
        2 => PlcErrorKind.InvalidAddress,         // 非法数据地址
        3 => PlcErrorKind.InvalidAddress,         // 非法数据值
        4 => PlcErrorKind.ProtocolError,          // 从站设备故障
        5 => PlcErrorKind.ProtocolError,          // 确认（长操作需要时间）
        6 => PlcErrorKind.ProtocolError,          // 从站设备忙
        _ => null,
    };
}

internal sealed class OmronPlcBrandDescriptor : PlcBrandDescriptorBase
{
    public override PlcBrand Brand => PlcBrand.Omron;
    public override IPlcDriver CreateDriver(PlcConfig config, ILoggerFactory loggerFactory) =>
        new HslOmronFinsDriver(config, loggerFactory.CreateLogger<HslOmronFinsDriver>());
    public override IPlcAddressCodec CreateAddressCodec(PlcConfig config) => new OmronAddressCodec();
    public override BatchReadCapabilities GetBatchReadCapabilities(PlcConfig config) =>
        new(true, (ushort)Math.Clamp(config.Omron.ReadSplits / 2, 1, 499), 2, true, 2000, 1);

    public override void Validate(PlcConfig config, ICollection<string> errors)
    {
        if (config.Omron.ReadSplits is < 1 or > 999)
            errors.Add($"Omron ReadSplits {config.Omron.ReadSplits} 不在合法范围 (1-999)");
    }
}

internal sealed class KeyencePlcBrandDescriptor : PlcBrandDescriptorBase
{
    public override PlcBrand Brand => PlcBrand.Keyence;
    public override IPlcDriver CreateDriver(PlcConfig config, ILoggerFactory loggerFactory) =>
        new HslKeyenceMcDriver(config, loggerFactory.CreateLogger<HslKeyenceMcDriver>());
    public override IPlcAddressCodec CreateAddressCodec(PlcConfig config) => new KeyenceAddressCodec();
    public override BatchReadCapabilities GetBatchReadCapabilities(PlcConfig config) =>
        new(true, 480, 2, true, 2000, 1);
}

internal static class PlcBrandDescriptors
{
    public static IPlcBrandRegistry CreateDefault() => new PlcBrandRegistry(
    [
        new MitsubishiPlcBrandDescriptor(),
        new SiemensPlcBrandDescriptor(),
        new ModbusTcpPlcBrandDescriptor(),
        new OmronPlcBrandDescriptor(),
        new KeyencePlcBrandDescriptor(),
    ]);
}
