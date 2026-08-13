namespace Kanban.Core.Services;

using Kanban.Core.Models;

/// <summary>
/// PLC 通信驱动抽象接口，解耦业务层与具体 PLC 协议库（HslCommunication）。
/// 业务层（PlcConnectionManager / PlcDataAcquisitionService / DeviceManagerViewModel）
/// 仅依赖此接口，测试时可注入 FakePlcDriver 模拟 PLC 故障、抖动、超时等场景。
/// 实现类（如 HslPlcDriver）封装具体协议的连接、读写细节。
/// 继承 IDisposable：实现类持有网络 socket 等非托管资源，DI 容器在应用关闭时统一释放。
/// </summary>
public interface IPlcDriver : IDeviceTransport
{
    /// <summary>
    /// 读取 16 位无符号整数。
    /// </summary>
    PlcOperationResult<ushort> ReadUInt16(string address);

    /// <summary>
    /// 读取 32 位整数。
    /// </summary>
    PlcOperationResult<int> ReadInt32(string address);

    /// <summary>从连续地址读取多个 32 位整数，由具体协议驱动实现。</summary>
    PlcOperationResult<int[]> ReadInt32Batch(string address, ushort length);

    /// <summary>
    /// 读取布尔值。
    /// </summary>
    PlcOperationResult<bool> ReadBool(string address);

    /// <summary>从连续地址读取多个布尔值，由具体协议驱动实现。</summary>
    PlcOperationResult<bool[]> ReadBoolBatch(string address, ushort length);

    /// <summary>读取 32 位浮点数（占用 DWord 字区地址）。</summary>
    PlcOperationResult<float> ReadFloat(string address);

    /// <summary>从连续地址读取多个 32 位浮点数。</summary>
    PlcOperationResult<float[]> ReadFloatBatch(string address, ushort length);

    /// <summary>读取字符串（length 为最大字符数，协议驱动自行处理编码/截断）。</summary>
    PlcOperationResult<string> ReadString(string address, ushort length);

    /// <summary>
    /// 写入 16 位无符号整数。
    /// </summary>
    PlcOperationResult WriteUInt16(string address, ushort value);

    /// <summary>
    /// 写入 32 位整数。
    /// </summary>
    PlcOperationResult WriteInt32(string address, int value);

    /// <summary>
    /// 写入布尔值。
    /// </summary>
    PlcOperationResult WriteBool(string address, bool value);

    /// <summary>写入 32 位浮点数。</summary>
    PlcOperationResult WriteFloat(string address, float value);

    /// <summary>写入字符串。</summary>
    PlcOperationResult WriteString(string address, string value);
}

public enum PlcErrorKind
{
    None,
    Unknown,
    Timeout,
    ConnectionLost,
    InvalidAddress,
    UnsupportedOperation,
    RequestTooLarge,
    ProtocolError,
    AccessDenied,
}

/// <summary>
/// PLC 操作结果（无返回值），与 HslCommunication.OperateResult 同构但独立于该库。
/// IsSuccess=false 时 Message 包含错误描述；IsSuccess=true 时 Content 不应被读取。
/// </summary>
public sealed class PlcOperationResult
{
    public bool IsSuccess { get; init; }
    public string Message { get; init; } = string.Empty;
    public PlcErrorKind ErrorKind { get; init; }
    public int? ErrorCode { get; init; }

    public static PlcOperationResult Success() => new() { IsSuccess = true, ErrorKind = PlcErrorKind.None };
    public static PlcOperationResult Fail(string message, PlcErrorKind errorKind = PlcErrorKind.Unknown, int? errorCode = null) =>
        new() { IsSuccess = false, Message = message, ErrorKind = errorKind, ErrorCode = errorCode };
}

/// <summary>
/// PLC 操作结果（带返回值），与 HslCommunication.OperateResult&lt;T&gt; 同构但独立于该库。
/// IsSuccess=true 时 Content 持有读取值；IsSuccess=false 时 Content 为 default。
/// </summary>
public sealed class PlcOperationResult<T>
{
    public bool IsSuccess { get; init; }
    public string Message { get; init; } = string.Empty;
    public T Content { get; init; } = default!;
    public PlcErrorKind ErrorKind { get; init; }
    public int? ErrorCode { get; init; }

    public static PlcOperationResult<T> Success(T value) => new() { IsSuccess = true, Content = value, ErrorKind = PlcErrorKind.None };
    public static PlcOperationResult<T> Fail(string message, PlcErrorKind errorKind = PlcErrorKind.Unknown, int? errorCode = null) =>
        new() { IsSuccess = false, Message = message, ErrorKind = errorKind, ErrorCode = errorCode };
}
