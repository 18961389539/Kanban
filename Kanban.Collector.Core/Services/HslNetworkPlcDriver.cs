using HslCommunication;
using MainAPP.Models;
using Microsoft.Extensions.Logging;
using System.Runtime.ExceptionServices;

namespace MainAPP.Services;

internal abstract class HslNetworkPlcDriver<TClient> : IPlcDriver
    where TClient : class
{
    private readonly object _sync = new();
    private readonly ILogger _logger;
    private TClient _client;
    private PlcConfig _config;
    private string _configurationKey;
    private bool _disposed;

    protected HslNetworkPlcDriver(PlcConfig config, ILogger logger)
    {
        _config = CloneConfig(config);
        _logger = logger;
        _client = CreateClient(_config);
        _configurationKey = BuildConfigurationKey(_config);
    }

    public abstract BatchReadCapabilities BatchReadCapabilities { get; }

    protected TClient Client => _client;
    protected PlcConfig Configuration => _config;

    protected abstract TClient CreateClient(PlcConfig config);
    protected abstract OperateResult ConnectCore(TClient client);
    protected abstract OperateResult DisconnectCore(TClient client);
    protected abstract OperateResult<ushort> ReadUInt16Core(TClient client, string address);
    protected abstract OperateResult<int> ReadInt32Core(TClient client, string address);
    protected abstract OperateResult<int[]> ReadInt32BatchCore(TClient client, string address, ushort length);
    protected abstract OperateResult<bool> ReadBoolCore(TClient client, string address);
    protected abstract OperateResult<bool[]> ReadBoolBatchCore(TClient client, string address, ushort length);
    protected abstract OperateResult WriteUInt16Core(TClient client, string address, ushort value);
    protected abstract OperateResult WriteInt32Core(TClient client, string address, int value);
    protected abstract OperateResult WriteBoolCore(TClient client, string address, bool value);

    public void Configure(string endpoint, int port)
    {
        var config = CloneConfig(_config);
        config.IpAddress = endpoint;
        config.Port = port;
        Configure(config);
    }

    public void Configure(PlcConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        lock (_sync)
        {
            if (_disposed) return;
            var copy = CloneConfig(config);
            if (BuildConfigurationKey(copy) == _configurationKey) return;
            try { DisconnectCore(_client); }
            catch (Exception ex) { _logger.LogWarning(ex, "释放旧 PLC 连接失败"); }
            _client = CreateClient(copy);
            _config = copy;
            _configurationKey = BuildConfigurationKey(copy);
        }
    }

    public PlcOperationResult Connect() => Execute(
        () => ConnectCore(_client));

    public PlcOperationResult Disconnect() => Execute(
        () => DisconnectCore(_client));

    public PlcOperationResult<ushort> ReadUInt16(string address) => Execute(
        () => ReadUInt16Core(_client, address));

    public PlcOperationResult<int> ReadInt32(string address) => Execute(
        () => ReadInt32Core(_client, address));

    public PlcOperationResult<int[]> ReadInt32Batch(string address, ushort length) => Execute(
        () => ReadInt32BatchCore(_client, address, length));

    public PlcOperationResult<bool> ReadBool(string address) => Execute(
        () => ReadBoolCore(_client, address));

    public PlcOperationResult<bool[]> ReadBoolBatch(string address, ushort length) => Execute(
        () => ReadBoolBatchCore(_client, address, length));

    public PlcOperationResult WriteUInt16(string address, ushort value) => Execute(
        () => WriteUInt16Core(_client, address, value));

    public PlcOperationResult WriteInt32(string address, int value) => Execute(
        () => WriteInt32Core(_client, address, value));

    public PlcOperationResult WriteBool(string address, bool value) => Execute(
        () => WriteBoolCore(_client, address, value));

    private PlcOperationResult Execute(Func<OperateResult> action)
    {
        lock (_sync)
        {
            try
            {
                var result = action();
                return result.IsSuccess
                    ? PlcOperationResult.Success()
                    : PlcOperationResult.Fail(result.Message, PlcErrorClassifier.FromResult(_config.Brand, result.ErrorCode, result.Message), result.ErrorCode);
            }
            catch (Exception ex)
            {
                return HandleException(ex, (message, kind) => PlcOperationResult.Fail(message, kind));
            }
        }
    }

    private PlcOperationResult<T> Execute<T>(Func<OperateResult<T>> action)
    {
        lock (_sync)
        {
            try
            {
                var result = action();
                return result.IsSuccess
                    ? PlcOperationResult<T>.Success(result.Content)
                    : PlcOperationResult<T>.Fail(result.Message, PlcErrorClassifier.FromResult(_config.Brand, result.ErrorCode, result.Message), result.ErrorCode);
            }
            catch (Exception ex)
            {
                return HandleException(ex, (message, kind) => PlcOperationResult<T>.Fail(message, kind));
            }
        }
    }

    private TResult HandleException<TResult>(Exception exception, Func<string, PlcErrorKind, TResult> failure)
    {
        RethrowIfCritical(exception);
        _logger.LogWarning(exception, "{Driver} PLC 操作异常", GetType().Name);
        return failure(exception.Message, PlcErrorClassifier.FromException(exception));
    }

    private string BuildConfigurationKey(PlcConfig config) =>
        $"{config.Brand}|{config.IpAddress}|{config.Port}|{config.TimeoutMs}|{config.SiemensModel}|{config.SiemensRack}|{config.SiemensSlot}|{config.SiemensDataFormat}|{config.SiemensBatchInt32Limit}|{config.OmronReadSplits}|{config.ModbusUnitId}|{config.ModbusAddressStartWithZero}|{config.ModbusRegisterFunction}|{config.ModbusBitFunction}|{config.ModbusDataFormat}";

    private static PlcConfig CloneConfig(PlcConfig source) => source.CreateSnapshot();

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            try { DisconnectCore(_client); }
            catch (Exception ex) { _logger.LogWarning(ex, "释放 PLC 连接失败"); }
        }
    }

    private static void RethrowIfCritical(Exception ex)
    {
        if (ex is OutOfMemoryException or AppDomainUnloadedException or ThreadAbortException)
            ExceptionDispatchInfo.Capture(ex).Throw();
    }
}
