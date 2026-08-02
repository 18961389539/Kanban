using System.IO;
using MainAPP.Models;

namespace MainAPP.Services;

public static class PlcErrorClassifier
{
    public static PlcErrorKind FromResult(PlcBrand brand, int? errorCode, string? message)
    {
        if (errorCode is int code && code != 0)
        {
            var codeKind = FromErrorCode(brand, code);
            if (codeKind.HasValue)
                return codeKind.Value;
        }
        return FromMessage(message);
    }

    public static PlcErrorKind FromMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return PlcErrorKind.Unknown;
        var text = message.ToLowerInvariant();
        if (text.Contains("timeout") || text.Contains("超时")) return PlcErrorKind.Timeout;
        if (text.Contains("address") || text.Contains("地址")) return PlcErrorKind.InvalidAddress;
        if (text.Contains("length") || text.Contains("数量") || text.Contains("pdu")) return PlcErrorKind.RequestTooLarge;
        if (text.Contains("permission") || text.Contains("access") || text.Contains("权限")) return PlcErrorKind.AccessDenied;
        if (text.Contains("connect") || text.Contains("socket") || text.Contains("connection") || text.Contains("连接")) return PlcErrorKind.ConnectionLost;
        return PlcErrorKind.ProtocolError;
    }

    public static PlcErrorKind FromException(Exception exception) => exception switch
    {
        TimeoutException => PlcErrorKind.Timeout,
        System.Net.Sockets.SocketException => PlcErrorKind.ConnectionLost,
        IOException => PlcErrorKind.ConnectionLost,
        ObjectDisposedException => PlcErrorKind.ConnectionLost,
        _ => PlcErrorKind.Unknown,
    };

    private static PlcErrorKind? FromErrorCode(PlcBrand brand, int errorCode) => errorCode switch
    {
        10060 => PlcErrorKind.Timeout,
        10051 or 10054 or 10061 => PlcErrorKind.ConnectionLost,
        _ => null,
    };
}