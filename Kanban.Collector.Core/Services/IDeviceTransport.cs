using MainAPP.Models;

namespace MainAPP.Services;

/// <summary>
/// 设备连接生命周期抽象。采集业务只需要通过适配器读写，连接管理不应绑定 PLC 类型。
/// </summary>
public interface IDeviceTransport : IDisposable
{
    void Configure(string endpoint, int port);
    void Configure(PlcConfig config) => Configure(config.IpAddress, config.Port);
    PlcOperationResult Connect();
    PlcOperationResult Disconnect();
}
