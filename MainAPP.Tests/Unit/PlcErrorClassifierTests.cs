using Kanban.Core.Models;
using MainAPP.Models;
using Kanban.Core.Services;
using MainAPP.Services;
using System.Net.Sockets;
using Xunit;

namespace MainAPP.Tests.Unit;

[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public sealed class PlcErrorClassifierTests
{
    [Theory]
    [InlineData("timeout while reading", PlcErrorKind.Timeout)]
    [InlineData("连接已断开", PlcErrorKind.ConnectionLost)]
    [InlineData("invalid address", PlcErrorKind.InvalidAddress)]
    [InlineData("PDU length exceeded", PlcErrorKind.RequestTooLarge)]
    [InlineData("permission denied", PlcErrorKind.AccessDenied)]
    public void FromMessage_ClassifiesCommonProtocolFailures(string message, PlcErrorKind expected)
    {
        Assert.Equal(expected, PlcErrorClassifier.FromMessage(message));
    }

    [Fact]
    public void FromException_ClassifiesSocketAndTimeout()
    {
        Assert.Equal(PlcErrorKind.ConnectionLost, PlcErrorClassifier.FromException(new SocketException()));
        Assert.Equal(PlcErrorKind.Timeout, PlcErrorClassifier.FromException(new TimeoutException()));
    }

    [Theory]
    [InlineData(10060, PlcErrorKind.Timeout)]
    [InlineData(10051, PlcErrorKind.ConnectionLost)]
    [InlineData(10054, PlcErrorKind.ConnectionLost)]
    [InlineData(10061, PlcErrorKind.ConnectionLost)]
    public void FromResult_PrefersKnownErrorCode(int errorCode, PlcErrorKind expected)
    {
        Assert.Equal(expected, PlcErrorClassifier.FromResult(PlcBrand.Siemens, errorCode, "generic protocol failure"));
    }

    [Fact]
    public void FromResult_UnknownCodeFallsBackToMessage()
    {
        Assert.Equal(PlcErrorKind.Timeout, PlcErrorClassifier.FromResult(PlcBrand.ModbusTcp, 999999, "timeout"));
    }

    [Theory]
    [InlineData(PlcBrand.ModbusTcp, 1, PlcErrorKind.UnsupportedOperation)]   // 非法功能码
    [InlineData(PlcBrand.ModbusTcp, 2, PlcErrorKind.InvalidAddress)]         // 非法数据地址
    [InlineData(PlcBrand.ModbusTcp, 3, PlcErrorKind.InvalidAddress)]         // 非法数据值
    [InlineData(PlcBrand.ModbusTcp, 4, PlcErrorKind.ProtocolError)]          // 从站设备故障
    [InlineData(PlcBrand.ModbusTcp, 5, PlcErrorKind.ProtocolError)]          // 确认
    [InlineData(PlcBrand.ModbusTcp, 6, PlcErrorKind.ProtocolError)]          // 从站设备忙
    public void FromResult_ClassifiesModbusExceptionCodes(PlcBrand brand, int errorCode, PlcErrorKind expected)
    {
        // Modbus 异常码（1~7）应按协议规范分类，不再走 message fallback
        Assert.Equal(expected, PlcErrorClassifier.FromResult(brand, errorCode, "generic"));
    }

    [Theory]
    [InlineData(PlcBrand.Siemens, 1)]
    [InlineData(PlcBrand.Omron, 2)]
    [InlineData(PlcBrand.Mitsubishi, 3)]
    [InlineData(PlcBrand.Keyence, 4)]
    public void FromResult_NonModbusSmallCodes_FallBackToMessage(PlcBrand brand, int errorCode)
    {
        // 非 Modbus 品牌的小数值错误码暂保留 message 通道分类，避免误判协议细节
        Assert.Equal(PlcErrorKind.InvalidAddress,
            PlcErrorClassifier.FromResult(brand, errorCode, "invalid address"));
    }

    [Theory]
    [InlineData(PlcBrand.ModbusTcp, 10060, PlcErrorKind.Timeout)]
    [InlineData(PlcBrand.Siemens, 10054, PlcErrorKind.ConnectionLost)]
    [InlineData(PlcBrand.Omron, 10061, PlcErrorKind.ConnectionLost)]
    public void FromResult_SocketErrorCodes_AreBrandAgnostic(PlcBrand brand, int errorCode, PlcErrorKind expected)
    {
        // Socket 层错误码所有协议共用，应优先匹配
        Assert.Equal(expected, PlcErrorClassifier.FromResult(brand, errorCode, "ignored"));
    }
}
