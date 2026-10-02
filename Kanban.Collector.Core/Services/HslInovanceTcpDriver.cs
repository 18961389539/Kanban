using HslCommunication;
using HslCommunication.Profinet.Inovance;
using Kanban.Collector.Core.Models;
using Microsoft.Extensions.Logging;

namespace Kanban.Collector.Core.Services;

/// <summary>
/// 汇川 Modbus TCP 驱动。客户端按系列把 D/M/X/Y 等软元件地址换算成 Modbus 地址，
/// 调用方继续写汇川地址，不手算寄存器偏移。
/// </summary>
internal sealed class HslInovanceTcpDriver : HslNetworkPlcDriver<InovanceTcpNet>
{
    public HslInovanceTcpDriver(PlcConfig config, ILogger<HslInovanceTcpDriver> logger)
        : base(config, logger)
    {
    }

    protected override InovanceTcpNet CreateClient(PlcConfig config)
    {
        var client = new InovanceTcpNet(
            ToHslSeries(config.Inovance.Series),
            config.IpAddress,
            config.Port,
            config.Inovance.Station)
        {
            ConnectTimeOut = Math.Max(1, config.TimeoutMs),
            ReceiveTimeOut = Math.Max(1, config.TimeoutMs),
            DataFormat = HslDataFormatMapper.ToHsl(config.Inovance.DataFormat),
        };
        return client;
    }

    internal static InovanceSeries ToHslSeries(InovancePlcSeries series) => series switch
    {
        InovancePlcSeries.AM => InovanceSeries.AM,
        InovancePlcSeries.H3U => InovanceSeries.H3U,
        _ => InovanceSeries.H5U,
    };

    protected override OperateResult ConnectCore(InovanceTcpNet client) => client.ConnectServer();
    protected override OperateResult DisconnectCore(InovanceTcpNet client) => client.ConnectClose();
    protected override OperateResult<ushort> ReadUInt16Core(InovanceTcpNet client, string address) => client.ReadUInt16(address);
    protected override OperateResult<int> ReadInt32Core(InovanceTcpNet client, string address) => client.ReadInt32(address);
    protected override OperateResult<int[]> ReadInt32BatchCore(InovanceTcpNet client, string address, ushort length) => client.ReadInt32(address, length);
    protected override OperateResult<bool> ReadBoolCore(InovanceTcpNet client, string address) => client.ReadBool(address);
    protected override OperateResult<bool[]> ReadBoolBatchCore(InovanceTcpNet client, string address, ushort length) => client.ReadBool(address, length);
    protected override OperateResult<float> ReadFloatCore(InovanceTcpNet client, string address) => client.ReadFloat(address);
    protected override OperateResult<float[]> ReadFloatBatchCore(InovanceTcpNet client, string address, ushort length) => client.ReadFloat(address, length);
    protected override OperateResult<string> ReadStringCore(InovanceTcpNet client, string address, ushort length) => client.ReadString(address, length);
    protected override OperateResult WriteUInt16Core(InovanceTcpNet client, string address, ushort value) => client.Write(address, value);
    protected override OperateResult WriteInt32Core(InovanceTcpNet client, string address, int value) => client.Write(address, value);
    protected override OperateResult WriteBoolCore(InovanceTcpNet client, string address, bool value) => client.Write(address, value);
    protected override OperateResult WriteFloatCore(InovanceTcpNet client, string address, float value) => client.Write(address, value);
    protected override OperateResult WriteStringCore(InovanceTcpNet client, string address, string value) => client.Write(address, value);
}
