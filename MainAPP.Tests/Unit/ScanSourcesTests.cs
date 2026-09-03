using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// PlcScanPipeline.ScanSources 单元测试：定时采集（无触发地址=每轮采集）、
/// PLC 电平触发（值==触发值 → 采集 → 同址回执）、回执态不重复采集、禁用源跳过。
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public class ScanSourcesTests
{
    /// <summary>内存寄存器假适配器：读/写 D 字地址，记录写入便于断言回执。</summary>
    private sealed class FakeAdapter : IDeviceAdapter
    {
        public Dictionary<string, int> Registers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, float> FloatRegisters { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, bool> BoolRegisters { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> StringRegisters { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<(string Address, int Value)> Writes { get; } = [];
        public int ReadInt32BatchCallCount { get; private set; }
        public string ProtocolKey { get; set; } = DataSourceProtocolKeys.Plc;

        public PlcBrand Brand => PlcBrand.Mitsubishi;
        public IPlcAddressCodec AddressCodec => new MitsubishiAddressCodec();
        public BatchReadCapabilities BatchReadCapabilities =>
            new(SupportsInt32: true, MaxInt32Length: 128, Int32AddressStride: 1, SupportsBool: false, MaxBoolLength: 0, BoolAddressStride: 1);

        public PlcOperationResult<int> ReadInt32(string address)
            => Registers.TryGetValue(AddressCodec.CanonicalKey(address), out var v)
                ? PlcOperationResult<int>.Success(v)
                : PlcOperationResult<int>.Fail("寄存器不存在", PlcErrorKind.Unknown);

        public PlcOperationResult<int[]> ReadInt32Batch(string address, ushort length)
        {
            ReadInt32BatchCallCount++;
            var values = new int[length];
            for (var i = 0; i < length; i++)
            {
                var addr = AddressCodec.Add(address, i);
                values[i] = Registers.TryGetValue(AddressCodec.CanonicalKey(addr), out var v) ? v : 0;
            }
            return PlcOperationResult<int[]>.Success(values);
        }

        public PlcOperationResult WriteInt32(string address, int value)
        {
            Registers[AddressCodec.CanonicalKey(address)] = value;
            Writes.Add((AddressCodec.CanonicalKey(address), value));
            return PlcOperationResult.Success();
        }

        public PlcOperationResult<bool> ReadBool(string address)
            => BoolRegisters.TryGetValue(AddressCodec.CanonicalKey(address), out var v)
                ? PlcOperationResult<bool>.Success(v)
                : PlcOperationResult<bool>.Fail("位不存在", PlcErrorKind.Unknown);
        public PlcOperationResult<bool[]> ReadBoolBatch(string address, ushort length) => PlcOperationResult<bool[]>.Fail("不支持", PlcErrorKind.UnsupportedOperation);
        public PlcOperationResult<float> ReadFloat(string address)
            => FloatRegisters.TryGetValue(AddressCodec.CanonicalKey(address), out var v)
                ? PlcOperationResult<float>.Success(v)
                : PlcOperationResult<float>.Fail("浮点寄存器不存在", PlcErrorKind.Unknown);
        public PlcOperationResult<float[]> ReadFloatBatch(string address, ushort length) => PlcOperationResult<float[]>.Fail("不支持", PlcErrorKind.UnsupportedOperation);
        public PlcOperationResult<ushort> ReadUInt16(string address) => PlcOperationResult<ushort>.Fail("不支持", PlcErrorKind.UnsupportedOperation);
        public PlcOperationResult<string> ReadString(string address, ushort length)
            => StringRegisters.TryGetValue(AddressCodec.CanonicalKey(address), out var v)
                ? PlcOperationResult<string>.Success(v)
                : PlcOperationResult<string>.Fail("字符串寄存器不存在", PlcErrorKind.Unknown);
        public PlcOperationResult WriteBool(string address, bool value) => PlcOperationResult.Fail("不支持", PlcErrorKind.UnsupportedOperation);
        public PlcOperationResult WriteFloat(string address, float value) => PlcOperationResult.Fail("不支持", PlcErrorKind.UnsupportedOperation);
        public PlcOperationResult WriteUInt16(string address, ushort value) => PlcOperationResult.Fail("不支持", PlcErrorKind.UnsupportedOperation);
        public PlcOperationResult WriteString(string address, string value) => PlcOperationResult.Fail("不支持", PlcErrorKind.UnsupportedOperation);
    }

    private sealed class FakeResolver(FakeAdapter adapter) : IDeviceAdapterResolver
    {
        public IDeviceAdapter Current => adapter;
        public IDeviceAdapter Resolve(Device device) => adapter;
    }

    private sealed class SimulatedDataSourceReader : IDataSourceReader
    {
        private readonly Dictionary<string, int> _values = new(StringComparer.OrdinalIgnoreCase);

        public DataSourceReaderDescriptor Descriptor { get; } = new("simulated", 100);
        public int ReadValueCount { get; private set; }
        public List<(string Address, int Value)> Acknowledgements { get; } = [];

        public bool CanHandle(IDeviceAdapter adapter, DataSource source) => true;

        public DataSourceReaderResult ValidateTriggerAddress(DataSourceReaderContext context, string address) =>
            DataSourceReaderResult.Success();

        public DataSourceReaderResult ValidateValueAddress(DataSourceReaderContext context, DataSourceValue value) =>
            DataSourceReaderResult.Success();

        public DataSourceReaderResult<int> ReadTrigger(DataSourceReaderContext context, string address) =>
            ToReaderResult(ReadInt32(address));

        public DataSourceReaderResult<DataSourceRuntimeValue> ReadValue(DataSourceReaderContext context, DataSourceValue value)
        {
            ReadValueCount++;
            var result = ReadInt32(value.PlcAddress);
            var runtime = new DataSourceRuntimeValue(
                DataSourceValueType.Int32,
                Int32Value: result.IsSuccess ? result.Content : 0,
                IsValid: result.IsSuccess);
            return result.IsSuccess
                ? DataSourceReaderResult<DataSourceRuntimeValue>.Success(runtime)
                : DataSourceReaderResult<DataSourceRuntimeValue>.Fail(
                    runtime,
                    result.Message,
                    DataSourceReaderErrorKind.Unknown,
                    result.ErrorCode);
        }

        public DataSourceReaderResult WriteAcknowledgement(DataSourceReaderContext context, string address, int value)
        {
            _values[address] = value;
            Acknowledgements.Add((address, value));
            return DataSourceReaderResult.Success();
        }

        public void SetInt32(string address, int value) => _values[address] = value;

        private PlcOperationResult<int> ReadInt32(string address) =>
            _values.TryGetValue(address, out var value)
                ? PlcOperationResult<int>.Success(value)
                : PlcOperationResult<int>.Fail("模拟寄存器不存在", PlcErrorKind.Unknown);

        private static DataSourceReaderResult<int> ToReaderResult(PlcOperationResult<int> result) =>
            result.IsSuccess
                ? DataSourceReaderResult<int>.Success(result.Content)
                : DataSourceReaderResult<int>.Fail(result.Message, DataSourceReaderErrorKind.Unknown, result.ErrorCode);
    }

    private sealed class RecordingAlarmHistory : IAlarmHistoryService
    {
        public List<AlarmEventRecord> Events { get; } = [];
        public AlarmEventRecord? GetLatestAlarmEvent(string alarmId) => null;
        public AlarmEventRecord? GetLatestAlarmEventStrict(string alarmId) => throw new NotImplementedException();
        public bool LogAlarmEvent(string deviceId, string deviceName, string alarmId, string alarmName,
            string plcAddress, AlarmEventType eventType, DateTime eventTime, string? shiftName = null)
        {
            Events.Add(new AlarmEventRecord
            {
                DeviceId = deviceId, DeviceName = deviceName, AlarmId = alarmId, AlarmName = alarmName,
                PlcAddress = plcAddress, EventType = eventType, EventTime = eventTime, ShiftName = shiftName ?? "",
            });
            return true;
        }
        public List<AlarmEventRecord> QueryAlarmEvents(DateTime from, DateTime to, string? deviceId = null, string? shiftName = null) => [];
        public List<AlarmEventRecord> QueryAlarmEventsStrict(DateTime from, DateTime to, string? deviceId = null, string? shiftName = null) => [];
        public Dictionary<string, List<AlarmEventRecord>> QueryAlarmEventsBatch(DateTime from, DateTime to, IReadOnlyList<string> deviceIds) => [];
        public (List<AlarmEventRecord> Items, int Total) QueryAlarmEventsPaged(DateTime from, DateTime to, string? deviceId, string? shiftName, int page, int pageSize) => ([], 0);
        public List<ActiveAlarmStateRecord> QueryActiveAlarmStates(string? deviceId = null) => [];
    }

    private static (PlcScanPipeline Pipeline, FakeAdapter Adapter, Device Device, DataSourceValue Value) BuildPipeline(
        DataSource source,
        DataSourceValue value,
        IDataSourceReaderRegistry? dataSourceReaderRegistry = null,
        string protocolKey = DataSourceProtocolKeys.Plc)
    {
        var settings = new AppSettings { ConfigDirectory = "KanbanScanSourcesTests_" + Guid.NewGuid().ToString("N") };
        var adapter = new FakeAdapter { ProtocolKey = protocolKey };
        var repository = new DeviceRepository(settings);
        var device = new Device { Id = "dev-001", Name = "注塑机1" };
        source.DeviceId = device.Id;
        source.Values.Add(value);
        device.Sources.Add(source);
        repository.Devices.Add(device);

        var pipeline = new PlcScanPipeline(
            new FakeResolver(adapter),
            repository,
            new RecordingAlarmHistory(),
            settings,
            () => "白班",
            NullLogger.Instance,
            dataSourceReaderRegistry: dataSourceReaderRegistry);
        return (pipeline, adapter, device, value);
    }

    // ──────────── 定时采集：无触发地址 = 每轮采集 ────────────

    [Fact]
    public void Periodic_NoTriggerAddress_ReadsEveryScan()
    {
        var value = new DataSourceValue { Name = "车间温度", PlcAddress = "D300", Unit = "℃" };
        var source = new DataSource { Name = "温湿度" };
        var (pipeline, adapter, _, valueItem) = BuildPipeline(source, value);
        adapter.Registers["D300"] = 245;

        var beforeSample = DateTime.Now;
        pipeline.ScanSources();
        var afterSample = DateTime.Now;

        Assert.Equal(245, valueItem.CurrentValue);
        Assert.InRange(valueItem.LastUpdatedAt!.Value, beforeSample, afterSample);
        var cycleValues = pipeline.GetCycleSourceValues();
        Assert.True(cycleValues.ContainsKey($"dev-001:{source.Id}:{valueItem.Id}"));
        Assert.Equal(245, cycleValues[$"dev-001:{source.Id}:{valueItem.Id}"]);
        var sample = Assert.Single(pipeline.GetCycleSourceSamples()).Value;
        Assert.Equal(valueItem.LastUpdatedAt, sample.SampledAt);
    }

    [Fact]
    public void Int32Source_UsesPreparedBatchValueThroughReader()
    {
        var value = new DataSourceValue { Name = "车间温度", PlcAddress = "D300" };
        var source = new DataSource { Name = "温度源" };
        var (pipeline, adapter, _, valueItem) = BuildPipeline(source, value);
        adapter.Registers["D300"] = 245;

        pipeline.PrepareDWordBatchValues();
        pipeline.ScanSources();

        Assert.Equal(245, valueItem.CurrentValue);
        Assert.Equal(1, adapter.ReadInt32BatchCallCount);
        Assert.Empty(adapter.Writes);
    }

    // ──────────── 电平触发：值==触发值 → 采集 + 同址回执 ────────────

    [Fact]
    public void Triggered_TriggerValueMatches_CollectsAndWritesAck()
    {
        var value = new DataSourceValue { Name = "温度", PlcAddress = "D300" };
        var source = new DataSource
        {
            Name = "温度采集",
            TriggerAddress = "D510",
            TriggerValue = 1,
            AckValue = 2,
        };
        var (pipeline, adapter, _, valueItem) = BuildPipeline(source, value);
        adapter.Registers["D300"] = 310;
        adapter.Registers["D510"] = 1; // PLC 置触发

        pipeline.ScanSources();

        // 采集成功 + 同址回执
        Assert.Equal(310, valueItem.CurrentValue);
        Assert.Equal(2, adapter.Registers["D510"]);
        Assert.Contains(adapter.Writes, w => w.Address == "D510" && w.Value == 2);
    }

    // ──────────── 回执态：触发字已为回执值 → 不再重复采集 ────────────

    [Fact]
    public void Triggered_AckStateOnTriggerAddress_DoesNotRecollect()
    {
        var value = new DataSourceValue { Name = "温度", PlcAddress = "D300" };
        var source = new DataSource
        {
            Name = "温度采集",
            TriggerAddress = "D510",
            TriggerValue = 1,
            AckValue = 2,
        };
        var (pipeline, adapter, _, valueItem) = BuildPipeline(source, value);
        adapter.Registers["D300"] = 245;
        adapter.Registers["D510"] = 2; // 上轮回执态

        pipeline.ScanSources();

        // 未触发：不采集、不回写
        Assert.Equal(0, valueItem.CurrentValue);
        Assert.Empty(adapter.Writes);
    }

    // ──────────── 多值源：一个触发位驱动全部值项（重构核心语义） ────────────

    [Fact]
    public void Triggered_MultiValueSource_TriggerOnceCollectsAllValues()
    {
        var temp1 = new DataSourceValue { Name = "温度", PlcAddress = "D300" };
        var temp2 = new DataSourceValue { Name = "湿度", PlcAddress = "D304" };
        var source = new DataSource
        {
            Name = "温湿度采集",
            TriggerAddress = "D510",
            TriggerValue = 1,
            AckValue = 2,
        };
        var (pipeline, adapter, _, _) = BuildPipeline(source, temp1);
        source.Values.Add(temp2);
        adapter.Registers["D300"] = 245;
        adapter.Registers["D304"] = 538;
        adapter.Registers["D510"] = 1; // PLC 置触发

        pipeline.ScanSources();

        // 两个值项都采集到 + 同址回执只写一次
        Assert.Equal(245, temp1.CurrentValue);
        Assert.Equal(538, temp2.CurrentValue);
        Assert.Equal(2, adapter.Registers["D510"]);
        var cycleValues = pipeline.GetCycleSourceValues();
        Assert.Equal(2, cycleValues.Count);
        Assert.Contains($"dev-001:{source.Id}:{temp1.Id}", cycleValues.Keys);
        Assert.Contains($"dev-001:{source.Id}:{temp2.Id}", cycleValues.Keys);
    }

    // ──────────── 值项独立启用：禁用值项跳过（修复 2026-08-17） ────────────

    [Fact]
    public void DisabledValue_IsSkipped_ButSiblingValuesCollected()
    {
        var temp1 = new DataSourceValue { Name = "温度", PlcAddress = "D300", Enabled = false };
        var temp2 = new DataSourceValue { Name = "湿度", PlcAddress = "D304" };
        var source = new DataSource { Name = "温湿度" };
        var (pipeline, adapter, _, _) = BuildPipeline(source, temp1);
        source.Values.Add(temp2);
        adapter.Registers["D300"] = 245;
        adapter.Registers["D304"] = 538;

        pipeline.ScanSources();

        Assert.Equal(0, temp1.CurrentValue); // 禁用值项不采集
        Assert.Equal(538, temp2.CurrentValue); // 同源启用值项正常采集
        var cycleValues = pipeline.GetCycleSourceValues();
        Assert.Single(cycleValues);
        Assert.Contains($"dev-001:{source.Id}:{temp2.Id}", cycleValues.Keys);
    }

    // ──────────── 禁用源跳过 ────────────

    [Fact]
    public void DisabledSource_IsSkipped()
    {
        var value = new DataSourceValue { Name = "车间温度", PlcAddress = "D300" };
        var source = new DataSource { Name = "温湿度", Enabled = false };
        var (pipeline, adapter, _, valueItem) = BuildPipeline(source, value);
        adapter.Registers["D300"] = 245;

        pipeline.ScanSources();

        Assert.Equal(0, valueItem.CurrentValue);
        Assert.Empty(pipeline.GetCycleSourceValues());
    }

    [Fact]
    public void BoolFalse_ReadSuccess_PreservesFalseContent()
    {
        var value = new DataSourceValue { Name = "运行许可", DataType = DataSourceValueType.Bool, PlcAddress = "M10" };
        var source = new DataSource { Name = "状态" };
        var (pipeline, adapter, _, valueItem) = BuildPipeline(source, value);
        adapter.BoolRegisters["M10"] = false;

        pipeline.ScanSources();

        Assert.False(valueItem.CurrentBoolValue);
        var runtime = Assert.Single(pipeline.GetCycleSourceRuntimeValues()).Value;
        Assert.True(runtime.IsValid);
        Assert.False(runtime.BoolValue);
    }

    [Fact]
    public void Float32_ReadsTypedValue()
    {
        var value = new DataSourceValue { Name = "压力", DataType = DataSourceValueType.Float32, PlcAddress = "D320" };
        var source = new DataSource { Name = "压力源" };
        var (pipeline, adapter, _, valueItem) = BuildPipeline(source, value);
        adapter.FloatRegisters["D320"] = 12.5f;

        pipeline.ScanSources();

        Assert.Equal(12.5f, valueItem.CurrentFloatValue);
    }

    [Fact]
    public void String_ReadsTypedValue()
    {
        var value = new DataSourceValue { Name = "状态文本", DataType = DataSourceValueType.String, PlcAddress = "D340", StringLength = 16 };
        var source = new DataSource { Name = "状态源" };
        var (pipeline, adapter, _, valueItem) = BuildPipeline(source, value);
        adapter.StringRegisters["D340"] = "READY";

        pipeline.ScanSources();

        Assert.Equal("READY", valueItem.CurrentStringValue);
    }

    [Fact]
    public void SimulatedReader_UsesSamePipelineAndProducesCycleSample()
    {
        var value = new DataSourceValue { Name = "模拟温度", PlcAddress = "D300" };
        var source = new DataSource
        {
            Name = "模拟协议源",
            TriggerAddress = "D510",
            TriggerValue = 1,
            AckValue = 2,
        };
        var simulatedReader = new SimulatedDataSourceReader();
        simulatedReader.SetInt32("D300", 901);
        simulatedReader.SetInt32("D510", 1);
        var registry = new DataSourceReaderRegistry([simulatedReader, new PlcDataSourceReader()]);
        var (pipeline, adapter, _, valueItem) = BuildPipeline(source, value, registry, "simulated");

        pipeline.ScanSources();

        Assert.Equal(901, valueItem.CurrentValue);
        Assert.Equal(2, simulatedReader.Acknowledgements.Single().Value);
        Assert.Empty(adapter.Writes);
        var sample = Assert.Single(pipeline.GetCycleSourceSamples()).Value;
        Assert.Equal(901, sample.Value.Int32Value);
        Assert.Equal(1, simulatedReader.ReadValueCount);
    }

    [Fact]
    public void SimulatedReader_TriggerReadFailure_DoesNotMatchZero()
    {
        var value = new DataSourceValue { Name = "模拟温度", PlcAddress = "D300" };
        var source = new DataSource
        {
            Name = "模拟协议源",
            TriggerAddress = "D510",
            TriggerValue = 0,
            AckValue = 2,
        };
        var simulatedReader = new SimulatedDataSourceReader();
        simulatedReader.SetInt32("D300", 901);
        var registry = new DataSourceReaderRegistry([simulatedReader, new PlcDataSourceReader()]);
        var (pipeline, _, _, valueItem) = BuildPipeline(source, value, registry, "simulated");

        pipeline.ScanSources();

        Assert.Equal(0, valueItem.CurrentValue);
        Assert.Empty(simulatedReader.Acknowledgements);
        Assert.Empty(pipeline.GetCycleSourceValues());
    }
}