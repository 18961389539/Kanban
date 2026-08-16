using HslCommunication;
using HslCommunication.Profinet.Melsec;
using Kanban.Collector.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kanban.Collector.Core.Services;

/// <summary>
/// IPlcDriver 的 HslCommunication 实现，基于三菱 MC 协议（3E 帧，二进制）。
///
/// 继承 <see cref="HslNetworkPlcDriver{TClient}"/> 复用线程安全、配置热替换、超时与异常分类、
/// Dispose 守卫等通用能力；本类只负责 MelsecMcNet 实例化与 10 个 *Core 协议转发方法。
/// 与 HslSiemensPlcDriver / HslModbusTcpDriver / HslOmronFinsDriver / HslKeyenceMcDriver 走同一基类，
/// 消除原 Mitsubishi 单独实现的并发/Configure 行为漂移。
///
/// 连接/断开由 PlcConnectionManager 统一管理，外部不可直接调用 Connect/Disconnect。
/// 旧 public 构造（无参 / ILogger / ipAddress+port / ipAddress+port+timeout+logger）保留以兼容
/// ISharedPlcDriverFactory 之外的历史调用点；内部转调基类的 PlcConfig 构造。
/// </summary>
public sealed class HslPlcDriver : HslNetworkPlcDriver<MelsecMcNet>
{
    /// <summary>
    /// 无参构造（兼容历史调用点）。真实连接参数由 PlcConnectionManager.EnsureConnected 调用 Configure 时注入。
    /// 默认指向 127.0.0.1:4999，超时 5000ms；DI 不会用此构造。
    /// </summary>
    public HslPlcDriver() : this("127.0.0.1", 4999, 5000, NullLogger<HslPlcDriver>.Instance)
    {
    }

    /// <summary>
    /// DI 兼容构造：注入 ILogger&lt;HslPlcDriver&gt;。生产路径走 HslSharedPlcDriverFactory.Create
    /// 直接传完整参数，此构造仅作 [ActivatorUtilitiesConstructor] 占位以兼容旧 DI 注册。
    /// </summary>
    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public HslPlcDriver(ILogger<HslPlcDriver> logger) : this("127.0.0.1", 4999, 5000, logger) { }

    public HslPlcDriver(string ipAddress, int port = 4999)
        : this(ipAddress, port, 5000, NullLogger<HslPlcDriver>.Instance) { }

    public HslPlcDriver(string ipAddress, int port, int timeoutMs, ILogger<HslPlcDriver> logger)
        : base(BuildConfig(ipAddress, port, timeoutMs), logger)
    {
    }

    public HslPlcDriver(PlcConfig config, ILogger<HslPlcDriver> logger) : base(config, logger) { }

    protected override MelsecMcNet CreateClient(PlcConfig config)
    {
        var plc = new MelsecMcNet(config.IpAddress, config.Port)
        {
            ConnectTimeOut = Math.Max(1, config.TimeoutMs),
            ReceiveTimeOut = Math.Max(1, config.TimeoutMs),
        };
        return plc;
    }

    protected override OperateResult ConnectCore(MelsecMcNet client) => client.ConnectServer();
    protected override OperateResult DisconnectCore(MelsecMcNet client) => client.ConnectClose();
    protected override OperateResult<ushort> ReadUInt16Core(MelsecMcNet client, string address) => client.ReadUInt16(address);
    protected override OperateResult<int> ReadInt32Core(MelsecMcNet client, string address) => client.ReadInt32(address);
    protected override OperateResult<int[]> ReadInt32BatchCore(MelsecMcNet client, string address, ushort length) => client.ReadInt32(address, length);
    protected override OperateResult<bool> ReadBoolCore(MelsecMcNet client, string address) => client.ReadBool(address);
    protected override OperateResult<bool[]> ReadBoolBatchCore(MelsecMcNet client, string address, ushort length) => client.ReadBool(address, length);
    protected override OperateResult<float> ReadFloatCore(MelsecMcNet client, string address) => client.ReadFloat(address);
    protected override OperateResult<float[]> ReadFloatBatchCore(MelsecMcNet client, string address, ushort length) => client.ReadFloat(address, length);
    protected override OperateResult<string> ReadStringCore(MelsecMcNet client, string address, ushort length) => client.ReadString(address, length);
    protected override OperateResult WriteUInt16Core(MelsecMcNet client, string address, ushort value) => client.Write(address, value);
    protected override OperateResult WriteInt32Core(MelsecMcNet client, string address, int value) => client.Write(address, value);
    protected override OperateResult WriteBoolCore(MelsecMcNet client, string address, bool value) => client.Write(address, value);
    protected override OperateResult WriteFloatCore(MelsecMcNet client, string address, float value) => client.Write(address, value);
    protected override OperateResult WriteStringCore(MelsecMcNet client, string address, string value) => client.Write(address, value);

    private static PlcConfig BuildConfig(string ipAddress, int port, int timeoutMs) => new()
    {
        Brand = PlcBrand.Mitsubishi,
        IpAddress = ipAddress,
        Port = port,
        TimeoutMs = timeoutMs,
    };
}
