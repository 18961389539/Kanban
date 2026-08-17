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
        public List<(string Address, int Value)> Writes { get; } = [];

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

        public PlcOperationResult<bool> ReadBool(string address) => PlcOperationResult<bool>.Fail("不支持", PlcErrorKind.UnsupportedOperation);
        public PlcOperationResult<bool[]> ReadBoolBatch(string address, ushort length) => PlcOperationResult<bool[]>.Fail("不支持", PlcErrorKind.UnsupportedOperation);
        public PlcOperationResult<float> ReadFloat(string address) => PlcOperationResult<float>.Fail("不支持", PlcErrorKind.UnsupportedOperation);
        public PlcOperationResult<float[]> ReadFloatBatch(string address, ushort length) => PlcOperationResult<float[]>.Fail("不支持", PlcErrorKind.UnsupportedOperation);
        public PlcOperationResult<ushort> ReadUInt16(string address) => PlcOperationResult<ushort>.Fail("不支持", PlcErrorKind.UnsupportedOperation);
        public PlcOperationResult<string> ReadString(string address, ushort length) => PlcOperationResult<string>.Fail("不支持", PlcErrorKind.UnsupportedOperation);
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
    }

    private static (PlcScanPipeline Pipeline, FakeAdapter Adapter, Device Device) BuildPipeline(DataSource source)
    {
        var settings = new AppSettings { ConfigDirectory = "KanbanScanSourcesTests_" + Guid.NewGuid().ToString("N") };
        var adapter = new FakeAdapter();
        var repository = new DeviceRepository(settings);
        var device = new Device { Id = "dev-001", Name = "注塑机1" };
        source.DeviceId = device.Id;
        device.Sources.Add(source);
        repository.Devices.Add(device);

        var pipeline = new PlcScanPipeline(
            new FakeResolver(adapter),
            repository,
            new RecordingAlarmHistory(),
            settings,
            () => "白班",
            NullLogger.Instance);
        return (pipeline, adapter, device);
    }

    // ──────────── 定时采集：无触发地址 = 每轮采集 ────────────

    [Fact]
    public void Periodic_NoTriggerAddress_ReadsEveryScan()
    {
        var source = new DataSource { Name = "车间温度", PlcAddress = "D300", Unit = "℃" };
        var (pipeline, adapter, _) = BuildPipeline(source);
        adapter.Registers["D300"] = 245;

        pipeline.ScanSources();

        Assert.Equal(245, source.CurrentValue);
        var cycleValues = pipeline.GetCycleSourceValues();
        Assert.True(cycleValues.ContainsKey("dev-001:" + source.Id));
        Assert.Equal(245, cycleValues["dev-001:" + source.Id]);
    }

    // ──────────── 电平触发：值==触发值 → 采集 + 同址回执 ────────────

    [Fact]
    public void Triggered_TriggerValueMatches_CollectsAndWritesAck()
    {
        var source = new DataSource
        {
            Name = "温度采集",
            PlcAddress = "D300",
            TriggerAddress = "D510",
            TriggerValue = 1,
            AckValue = 2,
        };
        var (pipeline, adapter, _) = BuildPipeline(source);
        adapter.Registers["D300"] = 310;
        adapter.Registers["D510"] = 1; // PLC 置触发

        pipeline.ScanSources();

        // 采集成功 + 同址回执
        Assert.Equal(310, source.CurrentValue);
        Assert.Equal(2, adapter.Registers["D510"]);
        Assert.Contains(adapter.Writes, w => w.Address == "D510" && w.Value == 2);
    }

    // ──────────── 回执态：触发字已为回执值 → 不再重复采集 ────────────

    [Fact]
    public void Triggered_AckStateOnTriggerAddress_DoesNotRecollect()
    {
        var source = new DataSource
        {
            Name = "温度采集",
            PlcAddress = "D300",
            TriggerAddress = "D510",
            TriggerValue = 1,
            AckValue = 2,
        };
        var (pipeline, adapter, _) = BuildPipeline(source);
        adapter.Registers["D300"] = 245;
        adapter.Registers["D510"] = 2; // 上轮回执态

        pipeline.ScanSources();

        // 未触发：不采集、不回写
        Assert.Equal(0, source.CurrentValue);
        Assert.Empty(adapter.Writes);
    }

    // ──────────── 禁用源跳过 ────────────

    [Fact]
    public void DisabledSource_IsSkipped()
    {
        var source = new DataSource { Name = "车间温度", PlcAddress = "D300", Enabled = false };
        var (pipeline, adapter, _) = BuildPipeline(source);
        adapter.Registers["D300"] = 245;

        pipeline.ScanSources();

        Assert.Equal(0, source.CurrentValue);
        Assert.Empty(pipeline.GetCycleSourceValues());
    }
}