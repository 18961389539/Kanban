using HslCommunication;
using HslCommunication.Profinet.Keyence;
using Kanban.Core.Models;
using Microsoft.Extensions.Logging;

namespace Kanban.Core.Services;

/// <summary>
/// 基恩士 MC 协议驱动：QnA 兼容 3E 帧、Binary 格式。
/// 地址同时支持 Keyence 原生格式（如 DM100/MR100）和三菱兼容格式（如 D100/M100）。
/// </summary>
internal sealed class HslKeyenceMcDriver : HslNetworkPlcDriver<KeyenceMcNet>
{
    public HslKeyenceMcDriver(PlcConfig config, ILogger<HslKeyenceMcDriver> logger)
        : base(config, logger)
    {
    }

    protected override KeyenceMcNet CreateClient(PlcConfig config) => new(config.IpAddress, config.Port)
    {
        ConnectTimeOut = Math.Max(1, config.TimeoutMs),
        ReceiveTimeOut = Math.Max(1, config.TimeoutMs),
    };

    protected override OperateResult ConnectCore(KeyenceMcNet client) => client.ConnectServer();
    protected override OperateResult DisconnectCore(KeyenceMcNet client) => client.ConnectClose();
    protected override OperateResult<ushort> ReadUInt16Core(KeyenceMcNet client, string address) => client.ReadUInt16(address);
    protected override OperateResult<int> ReadInt32Core(KeyenceMcNet client, string address) => client.ReadInt32(address);
    protected override OperateResult<int[]> ReadInt32BatchCore(KeyenceMcNet client, string address, ushort length) => client.ReadInt32(address, length);
    protected override OperateResult<bool> ReadBoolCore(KeyenceMcNet client, string address) => client.ReadBool(address);
    protected override OperateResult<bool[]> ReadBoolBatchCore(KeyenceMcNet client, string address, ushort length) => client.ReadBool(address, length);
    protected override OperateResult<float> ReadFloatCore(KeyenceMcNet client, string address) => client.ReadFloat(address);
    protected override OperateResult<float[]> ReadFloatBatchCore(KeyenceMcNet client, string address, ushort length) => client.ReadFloat(address, length);
    protected override OperateResult<string> ReadStringCore(KeyenceMcNet client, string address, ushort length) => client.ReadString(address, length);
    protected override OperateResult WriteUInt16Core(KeyenceMcNet client, string address, ushort value) => client.Write(address, value);
    protected override OperateResult WriteInt32Core(KeyenceMcNet client, string address, int value) => client.Write(address, value);
    protected override OperateResult WriteBoolCore(KeyenceMcNet client, string address, bool value) => client.Write(address, value);
    protected override OperateResult WriteFloatCore(KeyenceMcNet client, string address, float value) => client.Write(address, value);
    protected override OperateResult WriteStringCore(KeyenceMcNet client, string address, string value) => client.Write(address, value);
}
