using System.Diagnostics;
using System.Collections.Frozen;
using Kanban.Collector.Core.Localization;
using Kanban.Collector.Core.Models;

namespace Kanban.Collector.Core.Services;

public static class DataSourceProtocolKeys
{
    public const string Plc = PlcConfig.DefaultProtocolKey;
}

public enum DataSourceReaderErrorKind
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

public sealed class DataSourceReaderResult
{
    public bool IsSuccess { get; init; }
    public string Message { get; init; } = string.Empty;
    public DataSourceReaderErrorKind ErrorKind { get; init; }
    public int? ErrorCode { get; init; }

    public static DataSourceReaderResult Success() => new() { IsSuccess = true, ErrorKind = DataSourceReaderErrorKind.None };

    public static DataSourceReaderResult Fail(
        string message,
        DataSourceReaderErrorKind errorKind = DataSourceReaderErrorKind.Unknown,
        int? errorCode = null) =>
        new() { IsSuccess = false, Message = message, ErrorKind = errorKind, ErrorCode = errorCode };
}

public sealed class DataSourceReaderResult<T>
{
    public bool IsSuccess { get; init; }
    public string Message { get; init; } = string.Empty;
    public T Content { get; init; } = default!;
    public DataSourceReaderErrorKind ErrorKind { get; init; }
    public int? ErrorCode { get; init; }

    public static DataSourceReaderResult<T> Success(T value) =>
        new() { IsSuccess = true, Content = value, ErrorKind = DataSourceReaderErrorKind.None };

    public static DataSourceReaderResult<T> Fail(
        string message,
        DataSourceReaderErrorKind errorKind = DataSourceReaderErrorKind.Unknown,
        int? errorCode = null) =>
        new() { IsSuccess = false, Message = message, ErrorKind = errorKind, ErrorCode = errorCode };

    public static DataSourceReaderResult<T> Fail(
        T content,
        string message,
        DataSourceReaderErrorKind errorKind,
        int? errorCode = null) =>
        new() { IsSuccess = false, Content = content, Message = message, ErrorKind = errorKind, ErrorCode = errorCode };
}

internal static class DataSourceReaderResultMapper
{
    public static DataSourceReaderResult FromPlc(PlcOperationResult result) =>
        result.IsSuccess
            ? DataSourceReaderResult.Success()
            : DataSourceReaderResult.Fail(result.Message, MapErrorKind(result.ErrorKind), result.ErrorCode);

    public static DataSourceReaderResult<T> FromPlc<T>(PlcOperationResult<T> result) =>
        result.IsSuccess
            ? DataSourceReaderResult<T>.Success(result.Content)
            : DataSourceReaderResult<T>.Fail(result.Message, MapErrorKind(result.ErrorKind), result.ErrorCode);

    public static DataSourceReaderErrorKind MapErrorKind(PlcErrorKind errorKind) => errorKind switch
    {
        PlcErrorKind.None => DataSourceReaderErrorKind.None,
        PlcErrorKind.Timeout => DataSourceReaderErrorKind.Timeout,
        PlcErrorKind.ConnectionLost => DataSourceReaderErrorKind.ConnectionLost,
        PlcErrorKind.InvalidAddress => DataSourceReaderErrorKind.InvalidAddress,
        PlcErrorKind.UnsupportedOperation => DataSourceReaderErrorKind.UnsupportedOperation,
        PlcErrorKind.RequestTooLarge => DataSourceReaderErrorKind.RequestTooLarge,
        PlcErrorKind.ProtocolError => DataSourceReaderErrorKind.ProtocolError,
        PlcErrorKind.AccessDenied => DataSourceReaderErrorKind.AccessDenied,
        _ => DataSourceReaderErrorKind.Unknown,
    };
}

[Flags]
public enum DataSourceReaderOperations
{
    None = 0,
    TriggerRead = 1,
    ValueRead = 2,
    AcknowledgementWrite = 4,
}

public sealed record DataSourceReaderCapabilities(
    DataSourceReaderOperations Operations,
    IReadOnlySet<DataSourceValueType> SupportedValueTypes)
{
    private static readonly IReadOnlySet<DataSourceValueType> AllValueTypes =
        Enum.GetValues<DataSourceValueType>().ToFrozenSet();

    public static DataSourceReaderCapabilities All { get; } = new(
        DataSourceReaderOperations.TriggerRead
        | DataSourceReaderOperations.ValueRead
        | DataSourceReaderOperations.AcknowledgementWrite,
        AllValueTypes);

    public bool Supports(DataSourceReaderOperations operation) => (Operations & operation) == operation;

    public bool SupportsValueType(DataSourceValueType valueType) => SupportedValueTypes.Contains(valueType);
}

public static class DataSourceReaderMetricBuckets
{
    public static IReadOnlyList<long> DurationUpperBoundsMilliseconds { get; } =
        Array.AsReadOnly(new long[] { 1, 5, 10, 25, 50, 100, 250, 500, 1000, 2000, 5000 });
}

public sealed record DataSourceReaderDescriptor
{
    public DataSourceReaderDescriptor(
        string protocolKey,
        int priority = 0,
        DataSourceReaderCapabilities? capabilities = null)
    {
        if (string.IsNullOrWhiteSpace(protocolKey))
            throw new ArgumentException(ValidationMessages.DataSourceProtocolKeyEmpty, nameof(protocolKey));

        ProtocolKey = protocolKey.Trim().ToLowerInvariant();
        Priority = priority;
        Capabilities = capabilities ?? DataSourceReaderCapabilities.All;
    }

    public string ProtocolKey { get; }
    public int Priority { get; }
    public DataSourceReaderCapabilities Capabilities { get; }
}

public sealed record DataSourceReaderDiagnosticsSnapshot
{
    public string ProtocolKey { get; init; } = string.Empty;
    public string ReaderType { get; init; } = string.Empty;
    public int Priority { get; init; }
    public long ValidationCount { get; init; }
    public long ValidationSuccessCount { get; init; }
    public long ResolveCount { get; init; }
    public long ValidationFailureCount { get; init; }
    public long ReadCount { get; init; }
    public long ReadSuccessCount { get; init; }
    public long ReadFailureCount { get; init; }
    public long ReadDurationTotalMilliseconds { get; init; }
    public IReadOnlyList<long> ReadDurationBucketCounts { get; init; } = [];
    public long ReadP95Milliseconds { get; init; }
    public long ReadP99Milliseconds { get; init; }
    public long AcknowledgementCount { get; init; }
    public long AcknowledgementSuccessCount { get; init; }
    public long AcknowledgementFailureCount { get; init; }
    public long AcknowledgementDurationTotalMilliseconds { get; init; }
    public IReadOnlyList<long> AcknowledgementDurationBucketCounts { get; init; } = [];
    public long AcknowledgementP95Milliseconds { get; init; }
    public long AcknowledgementP99Milliseconds { get; init; }
}

/// <summary>数据源 reader 在一次扫描中使用的设备操作上下文。</summary>
public sealed class DataSourceReaderContext
{
    public DataSourceReaderContext(
        IDeviceAdapter adapter,
        Func<string, DataSourceReaderResult<int>> readInt32)
    {
        Adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        ReadInt32 = readInt32 ?? throw new ArgumentNullException(nameof(readInt32));
    }

    public IDeviceAdapter Adapter { get; }
    public Func<string, DataSourceReaderResult<int>> ReadInt32 { get; }
}

/// <summary>
/// 数据源协议 reader。reader 只负责触发值、值项和回执的协议读写，
/// 采集触发语义、告警状态和快照仍由 PlcScanPipeline 负责。
/// </summary>
public interface IDataSourceReader
{
    DataSourceReaderDescriptor Descriptor { get; }

    bool CanHandle(IDeviceAdapter adapter, DataSource source);

    DataSourceReaderResult ValidateTriggerAddress(DataSourceReaderContext context, string address);

    DataSourceReaderResult ValidateValueAddress(DataSourceReaderContext context, DataSourceValue value);

    DataSourceReaderResult<int> ReadTrigger(DataSourceReaderContext context, string address);

    DataSourceReaderResult<DataSourceRuntimeValue> ReadValue(DataSourceReaderContext context, DataSourceValue value);

    DataSourceReaderResult WriteAcknowledgement(DataSourceReaderContext context, string address, int value);
}

/// <summary>按设备适配器和数据源选择协议 reader。</summary>
public interface IDataSourceReaderRegistry
{
    IDataSourceReader Resolve(IDeviceAdapter adapter, DataSource source);
    IReadOnlyList<DataSourceReaderDiagnosticsSnapshot> GetDiagnosticsSnapshot();
}

public sealed class DataSourceReaderRegistry : IDataSourceReaderRegistry
{
    private readonly IReadOnlyList<ReaderRegistration> _readers;

    public DataSourceReaderRegistry(IEnumerable<IDataSourceReader> readers)
    {
        ArgumentNullException.ThrowIfNull(readers);
        _readers = readers
            .Select(reader => new ReaderRegistration(reader))
            .OrderByDescending(registration => registration.Reader.Descriptor.Priority)
            .ToArray();
        if (_readers.Count == 0)
            throw new InvalidOperationException("至少需要注册一个数据源 reader。");

        var duplicates = _readers
            .GroupBy(reader => reader.Reader.Descriptor.ProtocolKey, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .ToArray();
        if (duplicates.Length > 0)
        {
            var detail = string.Join(
                "、",
                duplicates.Select(group =>
                    $"{group.Key}({string.Join("+", group.Select(reader => $"{reader.OriginalReader.GetType().Name}@{reader.Reader.Descriptor.Priority}"))})"));
            throw new InvalidOperationException($"数据源 reader 注册冲突：{detail}。");
        }
    }

    public IDataSourceReader Resolve(IDeviceAdapter adapter, DataSource source)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(source);

        var protocolKey = string.IsNullOrWhiteSpace(adapter.ProtocolKey)
            ? DataSourceProtocolKeys.Plc
            : adapter.ProtocolKey;
        var matches = _readers
            .Where(registration => string.Equals(registration.Reader.Descriptor.ProtocolKey, protocolKey, StringComparison.OrdinalIgnoreCase))
            .Where(registration => registration.Reader.CanHandle(adapter, source))
            .ToArray();
        if (matches.Length == 0)
            throw new InvalidOperationException(
                $"未找到协议 {protocolKey} 的数据源 reader（适配器 {adapter.GetType().Name}，数据源 {source.Name}）。");

        if (matches.Length > 1)
        {
            throw new InvalidOperationException(
            $"数据源 {source.Name} 匹配了多个 reader：{string.Join("、", matches.Select(registration => registration.OriginalReader.GetType().Name))}。");
        }

        matches[0].Metrics.RecordResolve();
        return matches[0].Reader;
    }

    public IReadOnlyList<DataSourceReaderDiagnosticsSnapshot> GetDiagnosticsSnapshot() =>
        _readers.Select(registration => registration.Metrics.Snapshot()).ToArray();

    private sealed class ReaderRegistration
    {
        public ReaderRegistration(IDataSourceReader reader)
        {
            OriginalReader = reader ?? throw new ArgumentNullException(nameof(reader));
            Metrics = new DataSourceReaderMetrics(reader.Descriptor, reader.GetType());
            Reader = new InstrumentedDataSourceReader(reader, Metrics);
        }

        public IDataSourceReader OriginalReader { get; }
        public IDataSourceReader Reader { get; }
        public DataSourceReaderMetrics Metrics { get; }
    }
}

internal sealed class DataSourceReaderMetrics
{
    private readonly object _sync = new();
    private readonly DataSourceReaderDescriptor _descriptor;
    private readonly string _readerType;
    private readonly Queue<long> _readDurations = new();
    private readonly Queue<long> _acknowledgementDurations = new();
    private long _resolveCount;
    private long _validationCount;
    private long _validationSuccessCount;
    private long _validationFailureCount;
    private long _readCount;
    private long _readSuccessCount;
    private long _readFailureCount;
    private long _readDurationTotalMilliseconds;
    private readonly long[] _readDurationBucketCounts = new long[DataSourceReaderMetricBuckets.DurationUpperBoundsMilliseconds.Count];
    private long _acknowledgementCount;
    private long _acknowledgementSuccessCount;
    private long _acknowledgementFailureCount;
    private long _acknowledgementDurationTotalMilliseconds;
    private readonly long[] _acknowledgementDurationBucketCounts = new long[DataSourceReaderMetricBuckets.DurationUpperBoundsMilliseconds.Count];

    public DataSourceReaderMetrics(DataSourceReaderDescriptor descriptor, Type readerType)
    {
        _descriptor = descriptor;
        _readerType = readerType.Name;
    }

    public void RecordResolve()
    {
        lock (_sync) _resolveCount++;
    }

    public void RecordValidation(bool success)
    {
        lock (_sync)
        {
            _validationCount++;
            if (success) _validationSuccessCount++;
            else _validationFailureCount++;
        }
    }

    public void RecordRead(bool success, long durationMilliseconds)
    {
        lock (_sync)
        {
            _readCount++;
            if (success) _readSuccessCount++;
            else _readFailureCount++;
            _readDurationTotalMilliseconds += durationMilliseconds;
            AddDuration(_readDurations, _readDurationBucketCounts, durationMilliseconds);
        }
    }

    public void RecordAcknowledgement(bool success, long durationMilliseconds)
    {
        lock (_sync)
        {
            _acknowledgementCount++;
            if (success) _acknowledgementSuccessCount++;
            else _acknowledgementFailureCount++;
            _acknowledgementDurationTotalMilliseconds += durationMilliseconds;
            AddDuration(_acknowledgementDurations, _acknowledgementDurationBucketCounts, durationMilliseconds);
        }
    }

    public DataSourceReaderDiagnosticsSnapshot Snapshot()
    {
        lock (_sync)
        {
            return new DataSourceReaderDiagnosticsSnapshot
            {
                ProtocolKey = _descriptor.ProtocolKey,
                ReaderType = _readerType,
                Priority = _descriptor.Priority,
                ValidationCount = _validationCount,
                ValidationSuccessCount = _validationSuccessCount,
                ResolveCount = _resolveCount,
                ValidationFailureCount = _validationFailureCount,
                ReadCount = _readCount,
                ReadSuccessCount = _readSuccessCount,
                ReadFailureCount = _readFailureCount,
                ReadDurationTotalMilliseconds = _readDurationTotalMilliseconds,
                ReadDurationBucketCounts = _readDurationBucketCounts.ToArray(),
                ReadP95Milliseconds = Percentile(_readDurations, 0.95),
                ReadP99Milliseconds = Percentile(_readDurations, 0.99),
                AcknowledgementCount = _acknowledgementCount,
                AcknowledgementSuccessCount = _acknowledgementSuccessCount,
                AcknowledgementFailureCount = _acknowledgementFailureCount,
                AcknowledgementDurationTotalMilliseconds = _acknowledgementDurationTotalMilliseconds,
                AcknowledgementDurationBucketCounts = _acknowledgementDurationBucketCounts.ToArray(),
                AcknowledgementP95Milliseconds = Percentile(_acknowledgementDurations, 0.95),
                AcknowledgementP99Milliseconds = Percentile(_acknowledgementDurations, 0.99),
            };
        }
    }

    private static void AddDuration(Queue<long> durations, long[] bucketCounts, long durationMilliseconds)
    {
        durations.Enqueue(durationMilliseconds);
        while (durations.Count > 1024) durations.Dequeue();

        for (var index = 0; index < DataSourceReaderMetricBuckets.DurationUpperBoundsMilliseconds.Count; index++)
        {
            if (durationMilliseconds <= DataSourceReaderMetricBuckets.DurationUpperBoundsMilliseconds[index])
            {
                bucketCounts[index]++;
                break;
            }
        }
    }

    private static long Percentile(IEnumerable<long> durations, double percentile)
    {
        var ordered = durations.OrderBy(duration => duration).ToArray();
        if (ordered.Length == 0) return 0;
        var index = (int)Math.Ceiling(ordered.Length * percentile) - 1;
        return ordered[Math.Clamp(index, 0, ordered.Length - 1)];
    }
}

internal sealed class InstrumentedDataSourceReader(
    IDataSourceReader inner,
    DataSourceReaderMetrics metrics) : IDataSourceReader
{
    public DataSourceReaderDescriptor Descriptor => inner.Descriptor;

    public bool CanHandle(IDeviceAdapter adapter, DataSource source) => inner.CanHandle(adapter, source);

    public DataSourceReaderResult ValidateTriggerAddress(DataSourceReaderContext context, string address) =>
        Validate(() => inner.Descriptor.Capabilities.Supports(DataSourceReaderOperations.TriggerRead)
            ? inner.ValidateTriggerAddress(context, address)
            : Unsupported("触发读取"));

    public DataSourceReaderResult ValidateValueAddress(DataSourceReaderContext context, DataSourceValue value) =>
        Validate(() => inner.Descriptor.Capabilities.Supports(DataSourceReaderOperations.ValueRead)
            && inner.Descriptor.Capabilities.SupportsValueType(value.DataType)
            ? inner.ValidateValueAddress(context, value)
            : Unsupported($"值类型 {value.DataType}"));

    public DataSourceReaderResult<int> ReadTrigger(DataSourceReaderContext context, string address) =>
        Measure(
            () => inner.Descriptor.Capabilities.Supports(DataSourceReaderOperations.TriggerRead)
                ? inner.ReadTrigger(context, address)
                : DataSourceReaderResult<int>.Fail("reader 不支持触发读取。", DataSourceReaderErrorKind.UnsupportedOperation),
            result => result.IsSuccess,
            metrics.RecordRead);

    public DataSourceReaderResult<DataSourceRuntimeValue> ReadValue(DataSourceReaderContext context, DataSourceValue value) =>
        Measure(
            () => inner.Descriptor.Capabilities.Supports(DataSourceReaderOperations.ValueRead)
                && inner.Descriptor.Capabilities.SupportsValueType(value.DataType)
                ? inner.ReadValue(context, value)
                : DataSourceReaderResult<DataSourceRuntimeValue>.Fail(
                    new DataSourceRuntimeValue(value.DataType, IsValid: false),
                    "reader 不支持该值类型。",
                    DataSourceReaderErrorKind.UnsupportedOperation),
            result => result.IsSuccess && result.Content.IsValid,
            metrics.RecordRead);

    public DataSourceReaderResult WriteAcknowledgement(DataSourceReaderContext context, string address, int value) =>
        Measure(
            () => inner.Descriptor.Capabilities.Supports(DataSourceReaderOperations.AcknowledgementWrite)
                ? inner.WriteAcknowledgement(context, address, value)
                : DataSourceReaderResult.Fail("reader 不支持回执写入。", DataSourceReaderErrorKind.UnsupportedOperation),
            result => result.IsSuccess,
            metrics.RecordAcknowledgement);

    private static DataSourceReaderResult Unsupported(string operation) =>
        DataSourceReaderResult.Fail($"reader 不支持{operation}。", DataSourceReaderErrorKind.UnsupportedOperation);

    private DataSourceReaderResult Validate(Func<DataSourceReaderResult> operation)
    {
        try
        {
            var result = operation();
            metrics.RecordValidation(result.IsSuccess);
            return result;
        }
        catch
        {
            metrics.RecordValidation(false);
            throw;
        }
    }

    private static TResult Measure<TResult>(
        Func<TResult> operation,
        Func<TResult, bool> isSuccess,
        Action<bool, long> record)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            var result = operation();
            record(isSuccess(result), ElapsedMilliseconds(startTimestamp));
            return result;
        }
        catch
        {
            record(false, ElapsedMilliseconds(startTimestamp));
            throw;
        }
    }

    private static long ElapsedMilliseconds(long startTimestamp) =>
        (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
}

/// <summary>现有 PLC 适配器的数据源 reader。</summary>
public sealed class PlcDataSourceReader : IDataSourceReader
{
    public DataSourceReaderDescriptor Descriptor { get; } = new(
        DataSourceProtocolKeys.Plc,
        capabilities: DataSourceReaderCapabilities.All);

    public bool CanHandle(IDeviceAdapter adapter, DataSource source) => true;

    public DataSourceReaderResult ValidateTriggerAddress(DataSourceReaderContext context, string address) =>
        ValidateAddress(context.Adapter, address, PlcAddressType.DWord);

    public DataSourceReaderResult ValidateValueAddress(DataSourceReaderContext context, DataSourceValue value) =>
        ValidateAddress(
            context.Adapter,
            value.PlcAddress,
            value.DataType == DataSourceValueType.Bool ? PlcAddressType.MBit : PlcAddressType.DWord);

    public DataSourceReaderResult<int> ReadTrigger(DataSourceReaderContext context, string address) =>
        context.ReadInt32(address);

    public DataSourceReaderResult<DataSourceRuntimeValue> ReadValue(DataSourceReaderContext context, DataSourceValue value)
    {
        return value.DataType switch
        {
            DataSourceValueType.Float32 => ToRuntime<float>(
                context.Adapter.ReadFloat(value.PlcAddress),
                value.DataType,
                content => new DataSourceRuntimeValue(value.DataType, Float32Value: content)),
            DataSourceValueType.Bool => ToRuntime<bool>(
                context.Adapter.ReadBool(value.PlcAddress),
                value.DataType,
                content => new DataSourceRuntimeValue(value.DataType, BoolValue: content)),
            DataSourceValueType.String => ToRuntime<string>(
                context.Adapter.ReadString(value.PlcAddress, (ushort)Math.Clamp(value.StringLength, 1, ushort.MaxValue)),
                value.DataType,
                content => new DataSourceRuntimeValue(value.DataType, StringValue: content)),
            _ => ToRuntime<int>(
                context.ReadInt32(value.PlcAddress),
                value.DataType,
                content => new DataSourceRuntimeValue(value.DataType, Int32Value: content)),
        };
    }

    public DataSourceReaderResult WriteAcknowledgement(DataSourceReaderContext context, string address, int value) =>
        DataSourceReaderResultMapper.FromPlc(context.Adapter.WriteInt32(address, value));

    private static DataSourceReaderResult ValidateAddress(IDeviceAdapter adapter, string address, PlcAddressType expectedType)
    {
        var parsed = adapter.AddressCodec.Parse(address);
        if (!parsed.IsValid || parsed.Type != expectedType)
        {
            return DataSourceReaderResult.Fail(
                $"地址格式无效（需要{expectedType}）: {address}",
                DataSourceReaderErrorKind.InvalidAddress);
        }
        if (!adapter.AddressCodec.CanRead(parsed))
            return DataSourceReaderResult.Fail($"地址不可读: {address}", DataSourceReaderErrorKind.AccessDenied);
        return DataSourceReaderResult.Success();
    }

    private static DataSourceReaderResult<DataSourceRuntimeValue> ToRuntime<T>(
        PlcOperationResult<T> result,
        DataSourceValueType type,
        Func<T, DataSourceRuntimeValue> convert)
        => ToRuntime(DataSourceReaderResultMapper.FromPlc(result), type, convert);

    private static DataSourceReaderResult<DataSourceRuntimeValue> ToRuntime<T>(
        DataSourceReaderResult<T> result,
        DataSourceValueType type,
        Func<T, DataSourceRuntimeValue> convert)
    {
        var invalid = new DataSourceRuntimeValue(type, IsValid: false);
        if (!result.IsSuccess)
        {
            return DataSourceReaderResult<DataSourceRuntimeValue>.Fail(
                invalid,
                result.Message,
                result.ErrorKind,
                result.ErrorCode);
        }

        var runtime = convert(result.Content);
        return runtime.IsValid
            ? DataSourceReaderResult<DataSourceRuntimeValue>.Success(runtime)
            : DataSourceReaderResult<DataSourceRuntimeValue>.Fail(
                runtime,
                "读取值无效。",
                DataSourceReaderErrorKind.ProtocolError,
                result.ErrorCode);
    }
}