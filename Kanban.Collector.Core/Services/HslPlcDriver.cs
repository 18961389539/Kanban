using HslCommunication;
using HslCommunication.Profinet.Melsec;
using Kanban.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.ExceptionServices;

namespace Kanban.Core.Services;

/// <summary>
/// IPlcDriver 的 HslCommunication 实现，基于三菱 MC 协议（3E 帧，二进制）。
/// 连接/断开由 PlcConnectionManager 统一管理，外部不可直接调用 Connect/Disconnect。
/// _plc 实例会被后台采集线程（Read/Write）与 UI 线程（Configure/WriteRecipe）并发访问，
/// 所有对 _plc 的访问必须经过 _plcLock 同步，避免 Configure 重建实例时引用到不一致的实例。
/// 实现 IDisposable：防御性释放底层 socket，避免应用异常退出未调 Disconnect 时 socket 泄漏。
/// </summary>
public sealed class HslPlcDriver : IPlcDriver
{
    private readonly object _plcLock = new();
    private MelsecMcNet _plc;
    private string _ipAddress;
    private int _port;
    private bool _disposed;
    private readonly ILogger<HslPlcDriver> _logger;

    /// <summary>
    /// 无参构造（DI 默认使用）。真实连接参数由 PlcConnectionManager.EnsureConnected 调用 Configure 时注入。
    /// DI 容器会通过 [ActivatorUtilitiesConstructor] 优先选择带 ILogger 的构造函数注入真实 logger。
    /// </summary>
    public HslPlcDriver() : this("127.0.0.1", 4999, 5000, NullLogger<HslPlcDriver>.Instance)
    {
    }

    /// <summary>
    /// DI 使用的构造函数：注入 ILogger&lt;HslPlcDriver&gt;，PLC 通信故障可落盘日志便于排查。
    /// </summary>
    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public HslPlcDriver(ILogger<HslPlcDriver> logger) : this("127.0.0.1", 4999, 5000, logger) { }

    public HslPlcDriver(string ipAddress, int port = 4999)
        : this(ipAddress, port, 5000, NullLogger<HslPlcDriver>.Instance) { }

    public HslPlcDriver(string ipAddress, int port, int timeoutMs, ILogger<HslPlcDriver> logger)
    {
        _ipAddress = ipAddress;
        _port = port;
        _plc = new MelsecMcNet(ipAddress, port);
        _plc.ConnectTimeOut = Math.Max(1, timeoutMs);
        _logger = logger;
    }

    public void Configure(PlcConfig config)
    {
        Configure(config.IpAddress, config.Port);
        lock (_plcLock) _plc.ConnectTimeOut = Math.Max(1, config.TimeoutMs);
    }

    /// <summary>
    /// 重新配置连接参数（不立即重连，下次 Connect 时生效）。
    /// 仅当 IP 或端口变化时才重建底层 MelsecMcNet，并先释放旧实例的连接。
    /// 加锁保证重建期间不会有其他线程持有旧实例引用进行读写。
    /// </summary>
    public void Configure(string ipAddress, int port)
    {
        lock (_plcLock)
        {
            if (_disposed) return;
            if (_ipAddress == ipAddress && _port == port)
                return;

            // 释放旧实例的连接，避免 socket 泄漏；ConnectClose 在已断开/异常场景会抛异常，
            // 此处记录日志便于排查 socket 释放失败问题，不阻断 Configure 流程
            try { _plc.ConnectClose(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Configure 释放旧 PLC 实例连接失败（IP={OldIp}:{OldPort}→{NewIp}:{NewPort}）",
                    _ipAddress, _port, ipAddress, port);
            }

            _ipAddress = ipAddress;
            _port = port;
            // new MelsecMcNet 在极端环境（如类型初始化异常）可能抛异常，包裹 try-catch
            // 避免Configure 流程中断；此时 _plc 仍指向旧实例，后续 Connect 会基于旧 IP/端口尝试
            try
            {
                _plc = new MelsecMcNet(ipAddress, port);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Configure 重建 MelsecMcNet 实例失败，保留旧实例（目标 IP={NewIp}:{NewPort}）",
                    ipAddress, port);
            }
        }
    }

    /// <summary>
    /// 连接 PLC（仅 PlcConnectionManager 调用）
    /// </summary>
    public PlcOperationResult Connect()
    {
        lock (_plcLock)
        {
            try
            {
                var r = _plc.ConnectServer();
                return r.IsSuccess ? PlcOperationResult.Success() : PlcOperationResult.Fail(r.Message, PlcErrorClassifier.FromResult(PlcBrand.Mitsubishi, r.ErrorCode, r.Message), r.ErrorCode);
            }
            catch (Exception ex)
            {
                RethrowIfCritical(ex);
                // HslCommunication 在 socket 重置/对象已释放等场景会直接抛异常而非返回 OperateResult.Fail
                _logger.LogWarning(ex, "PLC Connect 抛异常（IP={Ip}:{Port}，类型={ExType}）",
                    _ipAddress, _port, ex.GetType().Name);
                return PlcOperationResult.Fail(ex.Message, PlcErrorClassifier.FromException(ex));
            }
        }
    }

    /// <summary>
    /// 断开连接（仅 PlcConnectionManager 调用）
    /// </summary>
    public PlcOperationResult Disconnect()
    {
        lock (_plcLock)
        {
            try
            {
                var r = _plc.ConnectClose();
                return r.IsSuccess ? PlcOperationResult.Success() : PlcOperationResult.Fail(r.Message, PlcErrorClassifier.FromResult(PlcBrand.Mitsubishi, r.ErrorCode, r.Message), r.ErrorCode);
            }
            catch (Exception ex)
            {
                RethrowIfCritical(ex);
                _logger.LogWarning(ex, "PLC Disconnect 抛异常（IP={Ip}:{Port}，类型={ExType}）",
                    _ipAddress, _port, ex.GetType().Name);
                return PlcOperationResult.Fail(ex.Message, PlcErrorClassifier.FromException(ex));
            }
        }
    }

    /// <summary>
    /// 读取 16 位无符号整数
    /// </summary>
    public PlcOperationResult<ushort> ReadUInt16(string address)
    {
        lock (_plcLock)
        {
            try
            {
                var r = _plc.ReadUInt16(address);
                if (!r.IsSuccess)
                {
                    _logger.LogWarning("PLC ReadUInt16 失败（地址={Address}，IP={Ip}:{Port}）：{Message}",
                        address, _ipAddress, _port, r.Message);
                    return PlcOperationResult<ushort>.Fail(r.Message, PlcErrorClassifier.FromResult(PlcBrand.Mitsubishi, r.ErrorCode, r.Message), r.ErrorCode);
                }
                return PlcOperationResult<ushort>.Success(r.Content);
            }
            catch (Exception ex)
            {
                RethrowIfCritical(ex);
                // HslCommunication 在 socket 重置/对象已释放等场景会直接抛异常而非返回 OperateResult.Fail
                _logger.LogWarning(ex, "PLC ReadUInt16 抛异常（地址={Address}，IP={Ip}:{Port}，类型={ExType}）",
                    address, _ipAddress, _port, ex.GetType().Name);
                return PlcOperationResult<ushort>.Fail(ex.Message, PlcErrorClassifier.FromException(ex));
            }
        }
    }

    /// <summary>
    /// 读取 32 位整数
    /// </summary>
    public PlcOperationResult<int> ReadInt32(string address)
    {
        lock (_plcLock)
        {
            try
            {
                var r = _plc.ReadInt32(address);
                if (!r.IsSuccess)
                {
                    _logger.LogWarning("PLC ReadInt32 失败（地址={Address}，IP={Ip}:{Port}）：{Message}",
                        address, _ipAddress, _port, r.Message);
                    return PlcOperationResult<int>.Fail(r.Message, PlcErrorClassifier.FromResult(PlcBrand.Mitsubishi, r.ErrorCode, r.Message), r.ErrorCode);
                }
                return PlcOperationResult<int>.Success(r.Content);
            }
            catch (Exception ex)
            {
                RethrowIfCritical(ex);
                _logger.LogWarning(ex, "PLC ReadInt32 抛异常（地址={Address}，IP={Ip}:{Port}，类型={ExType}）",
                    address, _ipAddress, _port, ex.GetType().Name);
                return PlcOperationResult<int>.Fail(ex.Message, PlcErrorClassifier.FromException(ex));
            }
        }
    }

    public PlcOperationResult<int[]> ReadInt32Batch(string address, ushort length)
    {
        lock (_plcLock)
        {
            try
            {
                var result = _plc.ReadInt32(address, length);
                return result.IsSuccess
                    ? PlcOperationResult<int[]>.Success(result.Content)
                    : PlcOperationResult<int[]>.Fail(result.Message, PlcErrorClassifier.FromResult(PlcBrand.Mitsubishi, result.ErrorCode, result.Message), result.ErrorCode);
            }
            catch (Exception ex)
            {
                RethrowIfCritical(ex);
                _logger.LogWarning(ex, "PLC ReadInt32Batch 抛异常（地址={Address}，长度={Length}）", address, length);
                return PlcOperationResult<int[]>.Fail(ex.Message, PlcErrorClassifier.FromException(ex));
            }
        }
    }

    /// <summary>
    /// 读取布尔值
    /// </summary>
    public PlcOperationResult<bool> ReadBool(string address)
    {
        lock (_plcLock)
        {
            try
            {
                var r = _plc.ReadBool(address);
                if (!r.IsSuccess)
                {
                    _logger.LogWarning("PLC ReadBool 失败（地址={Address}，IP={Ip}:{Port}）：{Message}",
                        address, _ipAddress, _port, r.Message);
                    return PlcOperationResult<bool>.Fail(r.Message, PlcErrorClassifier.FromResult(PlcBrand.Mitsubishi, r.ErrorCode, r.Message), r.ErrorCode);
                }
                return PlcOperationResult<bool>.Success(r.Content);
            }
            catch (Exception ex)
            {
                RethrowIfCritical(ex);
                _logger.LogWarning(ex, "PLC ReadBool 抛异常（地址={Address}，IP={Ip}:{Port}，类型={ExType}）",
                    address, _ipAddress, _port, ex.GetType().Name);
                return PlcOperationResult<bool>.Fail(ex.Message, PlcErrorClassifier.FromException(ex));
            }
        }
    }

    public PlcOperationResult<bool[]> ReadBoolBatch(string address, ushort length)
    {
        lock (_plcLock)
        {
            try
            {
                var result = _plc.ReadBool(address, length);
                return result.IsSuccess
                    ? PlcOperationResult<bool[]>.Success(result.Content)
                    : PlcOperationResult<bool[]>.Fail(result.Message, PlcErrorClassifier.FromResult(PlcBrand.Mitsubishi, result.ErrorCode, result.Message), result.ErrorCode);
            }
            catch (Exception ex)
            {
                RethrowIfCritical(ex);
                _logger.LogWarning(ex, "PLC ReadBoolBatch 抛异常（地址={Address}，长度={Length}）", address, length);
                return PlcOperationResult<bool[]>.Fail(ex.Message, PlcErrorClassifier.FromException(ex));
            }
        }
    }

    /// <summary>
    /// 写入 16 位无符号整数
    /// </summary>
    public PlcOperationResult WriteUInt16(string address, ushort value)
    {
        lock (_plcLock)
        {
            try
            {
                var r = _plc.Write(address, value);
                return r.IsSuccess ? PlcOperationResult.Success() : PlcOperationResult.Fail(r.Message, PlcErrorClassifier.FromResult(PlcBrand.Mitsubishi, r.ErrorCode, r.Message), r.ErrorCode);
            }
            catch (Exception ex)
            {
                RethrowIfCritical(ex);
                _logger.LogWarning(ex, "PLC WriteUInt16 抛异常（地址={Address}，值={Value}，IP={Ip}:{Port}，类型={ExType}）",
                    address, value, _ipAddress, _port, ex.GetType().Name);
                return PlcOperationResult.Fail(ex.Message, PlcErrorClassifier.FromException(ex));
            }
        }
    }

    /// <summary>
    /// 写入 32 位整数
    /// </summary>
    public PlcOperationResult WriteInt32(string address, int value)
    {
        lock (_plcLock)
        {
            try
            {
                var r = _plc.Write(address, value);
                return r.IsSuccess ? PlcOperationResult.Success() : PlcOperationResult.Fail(r.Message, PlcErrorClassifier.FromResult(PlcBrand.Mitsubishi, r.ErrorCode, r.Message), r.ErrorCode);
            }
            catch (Exception ex)
            {
                RethrowIfCritical(ex);
                _logger.LogWarning(ex, "PLC WriteInt32 抛异常（地址={Address}，值={Value}，IP={Ip}:{Port}，类型={ExType}）",
                    address, value, _ipAddress, _port, ex.GetType().Name);
                return PlcOperationResult.Fail(ex.Message, PlcErrorClassifier.FromException(ex));
            }
        }
    }

    /// <summary>
    /// 写入布尔值
    /// </summary>
    public PlcOperationResult WriteBool(string address, bool value)
    {
        lock (_plcLock)
        {
            try
            {
                var r = _plc.Write(address, value);
                return r.IsSuccess ? PlcOperationResult.Success() : PlcOperationResult.Fail(r.Message, PlcErrorClassifier.FromResult(PlcBrand.Mitsubishi, r.ErrorCode, r.Message), r.ErrorCode);
            }
            catch (Exception ex)
            {
                RethrowIfCritical(ex);
                _logger.LogWarning(ex, "PLC WriteBool 抛异常（地址={Address}，值={Value}，IP={Ip}:{Port}，类型={ExType}）",
                    address, value, _ipAddress, _port, ex.GetType().Name);
                return PlcOperationResult.Fail(ex.Message, PlcErrorClassifier.FromException(ex));
            }
        }
    }

    /// <summary>
    /// 防御性释放底层 socket：应用退出时由 DI 容器调用。
    /// 正常退出路径已由 PlcConnectionManager.Disconnect 关闭连接，此处仅作兜底，
    /// 避免应用异常崩溃或忘记调 Disconnect 时 socket 残留。
    /// 多次调用安全（_disposed 守卫，重复调用直接返回，避免重复 ConnectClose 触发底层异常）。
    /// </summary>
    public void Dispose()
    {
        lock (_plcLock)
        {
            if (_disposed) return;
            _disposed = true;
            try { _plc.ConnectClose(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Dispose 释放 PLC 连接失败（IP={Ip}:{Port}）", _ipAddress, _port);
            }
        }
    }

    /// <summary>
    /// 关键异常重抛：OutOfMemoryException / ThreadAbortException / AppDomainUnloadedException 等
    /// 不应被业务 try-catch 吞掉，否则后续行为不可预测。统一在此处判断并重抛。
    /// 使用 ExceptionDispatchInfo.Capture(ex).Throw() 保留原始堆栈（throw; 仅在 catch 块内合法，
    /// 此方法从 catch 块外调用；throw ex; 会重置 StackTrace）。
    /// </summary>
    private static void RethrowIfCritical(Exception ex)
    {
        if (ex is OutOfMemoryException or AppDomainUnloadedException or ThreadAbortException)
            ExceptionDispatchInfo.Capture(ex).Throw();
    }
}
