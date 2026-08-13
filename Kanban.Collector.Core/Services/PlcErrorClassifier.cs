using System.IO;
using Kanban.Core.Models;

namespace Kanban.Core.Services;

/// <summary>
/// PLC 错误分类门面：把错误码/消息分类委托给品牌描述符（<see cref="IPlcBrandDescriptor.ClassifyError"/>），
/// 保持原有公共 API 不变。新增品牌可在描述符中提供协议错误码表，无需修改本类。
/// Socket 层错误码（10060/10051/10054/10061 等）为所有协议共用，仍在本类处理。
/// </summary>
public static class PlcErrorClassifier
{
    private static readonly IPlcBrandRegistry DefaultRegistry = PlcBrandDescriptors.CreateDefault();

    public static PlcErrorKind FromResult(PlcBrand brand, int? errorCode, string? message)
    {
        try
        {
            return DefaultRegistry.Resolve(brand).ClassifyError(errorCode, message);
        }
        catch (InvalidOperationException)
        {
            // 未注册品牌：回退到消息分类，避免抛出影响业务路径
            return FromMessage(message);
        }
    }

    /// <summary>Socket 层错误码分类（品牌无关）。返回 null 表示不是已知 socket 错误码。</summary>
    public static PlcErrorKind? ClassifySocketErrorCode(int errorCode) => errorCode switch
    {
        // WSAETIMEDOUT=10060, WSAENETUNREACH=10051, WSAECONNRESET=10054, WSAECONNREFUSED=10061
        10060 => PlcErrorKind.Timeout,
        10051 or 10054 or 10061 => PlcErrorKind.ConnectionLost,
        _ => null,
    };

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
}
