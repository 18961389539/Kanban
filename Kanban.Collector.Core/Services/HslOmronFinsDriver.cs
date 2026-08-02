using HslCommunication;
using HslCommunication.Profinet.Omron;
using MainAPP.Models;
using Microsoft.Extensions.Logging;

namespace MainAPP.Services;

/// <summary>
/// 欧姆龙 FINS TCP 驱动。地址格式由 OmronAddressCodec 负责转换，
/// 连接和读写生命周期复用 HslNetworkPlcDriver 的线程安全实现。
/// </summary>
internal sealed class HslOmronFinsDriver : HslNetworkPlcDriver<OmronFinsNet>
{
    public HslOmronFinsDriver(PlcConfig config, ILogger<HslOmronFinsDriver> logger)
        : base(config, logger)
    {
    }

    public override BatchReadCapabilities BatchReadCapabilities => new(
        true,
        (ushort)Math.Clamp(Configuration.OmronReadSplits / 2, 1, 499),
        2,
        true,
        2000,
        1);

    protected override OmronFinsNet CreateClient(PlcConfig config)
    {
        var client = new OmronFinsNet(config.IpAddress, config.Port)
        {
            ConnectTimeOut = Math.Max(1, config.TimeoutMs),
            ReceiveTimeOut = Math.Max(1, config.TimeoutMs),
            ReadSplits = Math.Clamp(config.OmronReadSplits, 1, 999),
        };
        return client;
    }

    protected override OperateResult ConnectCore(OmronFinsNet client) => client.ConnectServer();
    protected override OperateResult DisconnectCore(OmronFinsNet client) => client.ConnectClose();
    protected override OperateResult<ushort> ReadUInt16Core(OmronFinsNet client, string address) => client.ReadUInt16(address);
    protected override OperateResult<int> ReadInt32Core(OmronFinsNet client, string address) => client.ReadInt32(address);
    protected override OperateResult<int[]> ReadInt32BatchCore(OmronFinsNet client, string address, ushort length) => client.ReadInt32(address, length);
    protected override OperateResult<bool> ReadBoolCore(OmronFinsNet client, string address) => client.ReadBool(address);
    protected override OperateResult<bool[]> ReadBoolBatchCore(OmronFinsNet client, string address, ushort length) => client.ReadBool(address, length);
    protected override OperateResult WriteUInt16Core(OmronFinsNet client, string address, ushort value) => client.Write(address, value);
    protected override OperateResult WriteInt32Core(OmronFinsNet client, string address, int value) => client.Write(address, value);
    protected override OperateResult WriteBoolCore(OmronFinsNet client, string address, bool value) => client.Write(address, value);
}
