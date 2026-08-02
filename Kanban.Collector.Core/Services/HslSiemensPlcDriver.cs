using HslCommunication;
using HslCommunication.Profinet.Siemens;
using Kanban.Core.Models;
using Microsoft.Extensions.Logging;

namespace Kanban.Core.Services;

internal sealed class HslSiemensPlcDriver : HslNetworkPlcDriver<SiemensS7Net>
{
    public HslSiemensPlcDriver(PlcConfig config, ILogger<HslSiemensPlcDriver> logger) : base(config, logger) { }

    public override BatchReadCapabilities BatchReadCapabilities => new(
        true,
        (ushort)Math.Clamp(Configuration.SiemensBatchInt32Limit, 1, 55),
        4,
        true,
        2000,
        1);

    protected override SiemensS7Net CreateClient(PlcConfig config)
    {
        var plc = new SiemensS7Net(ParseModel(config.SiemensModel), config.IpAddress)
        {
            Port = config.Port,
            Rack = config.SiemensRack,
            Slot = config.SiemensSlot,
            ConnectTimeOut = Math.Max(1, config.TimeoutMs),
        };
        plc.ByteTransform.DataFormat = HslDataFormatMapper.ToHsl(config.SiemensDataFormat);
        return plc;
    }

    protected override OperateResult ConnectCore(SiemensS7Net client) => client.ConnectServer();
    protected override OperateResult DisconnectCore(SiemensS7Net client) => client.ConnectClose();
    protected override OperateResult<ushort> ReadUInt16Core(SiemensS7Net client, string address) => client.ReadUInt16(address);
    protected override OperateResult<int> ReadInt32Core(SiemensS7Net client, string address) => client.ReadInt32(address);
    protected override OperateResult<int[]> ReadInt32BatchCore(SiemensS7Net client, string address, ushort length) => client.ReadInt32(address, length);
    protected override OperateResult<bool> ReadBoolCore(SiemensS7Net client, string address) => client.ReadBool(address);
    protected override OperateResult<bool[]> ReadBoolBatchCore(SiemensS7Net client, string address, ushort length) => client.ReadBool(address, length);
    protected override OperateResult WriteUInt16Core(SiemensS7Net client, string address, ushort value) => client.Write(address, value);
    protected override OperateResult WriteInt32Core(SiemensS7Net client, string address, int value) => client.Write(address, value);
    protected override OperateResult WriteBoolCore(SiemensS7Net client, string address, bool value) => client.Write(address, value);

    private static SiemensPLCS ParseModel(string model) => model.ToUpperInvariant() switch
    {
        "S1200" => SiemensPLCS.S1200,
        "S1500" => SiemensPLCS.S1500,
        "S300" => SiemensPLCS.S300,
        "S400" => SiemensPLCS.S400,
        "S200SMART" => SiemensPLCS.S200Smart,
        "S200" => SiemensPLCS.S200,
        _ => throw new InvalidOperationException($"不支持的 Siemens 型号：{model}"),
    };
}
