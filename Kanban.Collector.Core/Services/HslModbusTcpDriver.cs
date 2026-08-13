using HslCommunication;
using HslCommunication.ModBus;
using Kanban.Core.Models;
using Microsoft.Extensions.Logging;

namespace Kanban.Core.Services;

internal sealed class HslModbusTcpDriver : HslNetworkPlcDriver<ModbusTcpNet>
{
    public HslModbusTcpDriver(PlcConfig config, ILogger<HslModbusTcpDriver> logger) : base(config, logger) { }

    protected override ModbusTcpNet CreateClient(PlcConfig config) => new(config.IpAddress, config.Port, config.ModbusTcp.UnitId)
    {
        ConnectTimeOut = Math.Max(1, config.TimeoutMs),
        AddressStartWithZero = config.ModbusTcp.AddressStartWithZero,
        DataFormat = HslDataFormatMapper.ToHsl(config.ModbusTcp.DataFormat),
    };

    protected override OperateResult ConnectCore(ModbusTcpNet client) => client.ConnectServer();
    protected override OperateResult DisconnectCore(ModbusTcpNet client) => client.ConnectClose();
    protected override OperateResult<ushort> ReadUInt16Core(ModbusTcpNet client, string address) => client.ReadUInt16(address);
    protected override OperateResult<int> ReadInt32Core(ModbusTcpNet client, string address) => client.ReadInt32(address);
    protected override OperateResult<int[]> ReadInt32BatchCore(ModbusTcpNet client, string address, ushort length) => client.ReadInt32(address, length);
    protected override OperateResult<bool> ReadBoolCore(ModbusTcpNet client, string address) => client.ReadBool(address);
    protected override OperateResult<bool[]> ReadBoolBatchCore(ModbusTcpNet client, string address, ushort length) => client.ReadBool(address, length);
    protected override OperateResult<float> ReadFloatCore(ModbusTcpNet client, string address) => client.ReadFloat(address);
    protected override OperateResult<float[]> ReadFloatBatchCore(ModbusTcpNet client, string address, ushort length) => client.ReadFloat(address, length);
    protected override OperateResult<string> ReadStringCore(ModbusTcpNet client, string address, ushort length) => client.ReadString(address, length);
    protected override OperateResult WriteUInt16Core(ModbusTcpNet client, string address, ushort value) => client.Write(address, value);
    protected override OperateResult WriteInt32Core(ModbusTcpNet client, string address, int value) => client.Write(address, value);
    protected override OperateResult WriteBoolCore(ModbusTcpNet client, string address, bool value) => client.Write(address, value);
    protected override OperateResult WriteFloatCore(ModbusTcpNet client, string address, float value) => client.Write(address, value);
    protected override OperateResult WriteStringCore(ModbusTcpNet client, string address, string value) => client.Write(address, value);
}
