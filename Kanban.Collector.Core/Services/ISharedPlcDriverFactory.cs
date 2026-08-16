using Kanban.Collector.Core.Models;
using Microsoft.Extensions.Logging;

namespace Kanban.Collector.Core.Services;

/// <summary>为当前应用创建唯一的共享 PLC 驱动；所有设备复用该实例。</summary>
public interface ISharedPlcDriverFactory
{
    IPlcDriver Create(PlcConfig config);
}

public sealed class HslSharedPlcDriverFactory(
    Microsoft.Extensions.Logging.ILoggerFactory loggerFactory,
    IPlcBrandRegistry? brandRegistry = null)
    : ISharedPlcDriverFactory
{
    private readonly IPlcBrandRegistry _brandRegistry = brandRegistry ?? PlcBrandDescriptors.CreateDefault();

    public IPlcDriver Create(PlcConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return _brandRegistry.Resolve(config.Brand).CreateDriver(config, loggerFactory);
    }
}