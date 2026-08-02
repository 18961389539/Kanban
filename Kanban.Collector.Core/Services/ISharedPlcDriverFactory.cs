using MainAPP.Models;
using Microsoft.Extensions.Logging;

namespace MainAPP.Services;

/// <summary>为当前应用创建唯一的共享 PLC 驱动；所有设备复用该实例。</summary>
public interface ISharedPlcDriverFactory
{
    IPlcDriver Create(PlcConfig config);
}

public sealed class HslSharedPlcDriverFactory(Microsoft.Extensions.Logging.ILoggerFactory loggerFactory)
    : ISharedPlcDriverFactory
{
    public IPlcDriver Create(PlcConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Brand switch
        {
            PlcBrand.Mitsubishi => new HslPlcDriver(
                config.IpAddress, config.Port, config.TimeoutMs,
                loggerFactory.CreateLogger<HslPlcDriver>()),
            PlcBrand.Siemens => new HslSiemensPlcDriver(config, loggerFactory.CreateLogger<HslSiemensPlcDriver>()),
            PlcBrand.ModbusTcp => new HslModbusTcpDriver(config, loggerFactory.CreateLogger<HslModbusTcpDriver>()),
            PlcBrand.Omron => new HslOmronFinsDriver(config, loggerFactory.CreateLogger<HslOmronFinsDriver>()),
            _ => throw new InvalidOperationException($"未支持的 PLC 品牌：{config.Brand}"),
        };
    }
}