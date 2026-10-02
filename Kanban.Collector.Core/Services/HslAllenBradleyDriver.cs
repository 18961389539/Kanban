using System.Globalization;
using System.Text.RegularExpressions;
using HslCommunication;
using HslCommunication.Core.Net;
using HslCommunication.Profinet.AllenBradley;
using Kanban.Collector.Core.Models;
using Microsoft.Extensions.Logging;

namespace Kanban.Collector.Core.Services;

/// <summary>
/// 罗克韦尔 EtherNet/IP（CIP）驱动。ControlLogix / CompactLogix 按标签名读写。
/// 默认使用无连接的 <see cref="AllenBradleyNet"/>；配置打开后改用有连接的 CIP 会话。
/// </summary>
internal sealed class HslAllenBradleyDriver : HslNetworkPlcDriver<NetworkDeviceBase>
{
    private static readonly Regex ArrayElementPattern = new(
        @"^(?<name>.+)\[(?<index>\d+)\]$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public HslAllenBradleyDriver(PlcConfig config, ILogger<HslAllenBradleyDriver> logger)
        : base(config, logger)
    {
    }

    protected override NetworkDeviceBase CreateClient(PlcConfig config)
    {
        NetworkDeviceBase client = config.AllenBradley.UseConnectedCip
            ? new AllenBradleyConnectedCipNet(config.IpAddress, config.Port)
            : new AllenBradleyNet(config.IpAddress, config.Port)
            {
                Slot = config.AllenBradley.Slot,
            };
        client.ConnectTimeOut = Math.Max(1, config.TimeoutMs);
        client.ReceiveTimeOut = Math.Max(1, config.TimeoutMs);
        return client;
    }

    protected override OperateResult ConnectCore(NetworkDeviceBase client) => client.ConnectServer();

    protected override OperateResult DisconnectCore(NetworkDeviceBase client)
    {
        var result = client.ConnectClose();
        // 虚拟服务器和部分控制器在 CIP 会话关闭时直接 RST，连接已经断开。
        return result.IsSuccess || IsPeerReset(result) ? OperateResult.CreateSuccessResult() : result;
    }

    protected override OperateResult<ushort> ReadUInt16Core(NetworkDeviceBase client, string address) => client.ReadUInt16(address);
    protected override OperateResult<int> ReadInt32Core(NetworkDeviceBase client, string address) => client.ReadInt32(address);
    protected override OperateResult<int[]> ReadInt32BatchCore(NetworkDeviceBase client, string address, ushort length) => client.ReadInt32(address, length);
    protected override OperateResult<bool> ReadBoolCore(NetworkDeviceBase client, string address) => client.ReadBool(address);

    protected override OperateResult<bool[]> ReadBoolBatchCore(NetworkDeviceBase client, string address, ushort length)
    {
        // Hsl 的 AllenBradleyNet.ReadBool(address, length) 对布尔数组只回填第一个元素。
        var match = ArrayElementPattern.Match(address);
        if (!match.Success || length <= 1)
            return client.ReadBool(address, length);

        var name = match.Groups["name"].Value;
        var start = int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture);
        var values = new bool[length];
        for (var i = 0; i < length; i++)
        {
            var one = client.ReadBool($"{name}[{start + i}]");
            if (!one.IsSuccess)
                return OperateResult.CreateFailedResult<bool[]>(one);
            values[i] = one.Content;
        }

        return OperateResult.CreateSuccessResult(values);
    }

    protected override OperateResult<float> ReadFloatCore(NetworkDeviceBase client, string address) => client.ReadFloat(address);
    protected override OperateResult<float[]> ReadFloatBatchCore(NetworkDeviceBase client, string address, ushort length) => client.ReadFloat(address, length);
    protected override OperateResult<string> ReadStringCore(NetworkDeviceBase client, string address, ushort length) => client.ReadString(address, length);
    protected override OperateResult WriteUInt16Core(NetworkDeviceBase client, string address, ushort value) => client.Write(address, value);
    protected override OperateResult WriteInt32Core(NetworkDeviceBase client, string address, int value) => client.Write(address, value);
    protected override OperateResult WriteBoolCore(NetworkDeviceBase client, string address, bool value) => client.Write(address, value);
    protected override OperateResult WriteFloatCore(NetworkDeviceBase client, string address, float value) => client.Write(address, value);
    protected override OperateResult WriteStringCore(NetworkDeviceBase client, string address, string value) => client.Write(address, value);

    private static bool IsPeerReset(OperateResult result)
    {
        var message = result.Message ?? string.Empty;
        return message.Contains("远程关闭", StringComparison.Ordinal)
            || message.Contains("强迫关闭", StringComparison.Ordinal)
            || message.Contains("forcibly closed", StringComparison.OrdinalIgnoreCase);
    }
}
