using MainAPP.Models;
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
}
