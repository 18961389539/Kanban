using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using NSubstitute;
using Xunit;

namespace MainAPP.Tests.Unit;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public sealed class DataSourceReaderRegistryTests
{
    [Fact]
    public void Resolve_UsesAdapterProtocolKey_AndIgnoresBusinessSourceType()
    {
        var plcReader = new TestReader(new(DataSourceProtocolKeys.Plc));
        var simulatedReader = new TestReader(new("simulated"));
        var registry = new DataSourceReaderRegistry([plcReader, simulatedReader]);
        var source = new DataSource { Name = "温度", Type = "展示类型" };

        var resolved = registry.Resolve(CreateAdapter("simulated"), source);

        Assert.Equal(simulatedReader.Descriptor, resolved.Descriptor);
    }

    [Fact]
    public void Constructor_RejectsDuplicateProtocolRegardlessOfPriority()
    {
        var first = new TestReader(new("plc", 10));
        var second = new TestReader(new("PLC", 20));

        var exception = Assert.Throws<InvalidOperationException>(
            () => new DataSourceReaderRegistry([first, second]));

        Assert.Contains("plc", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@10", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@20", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_RejectsUnknownProtocol()
    {
        var registry = new DataSourceReaderRegistry([new TestReader(new(DataSourceProtocolKeys.Plc))]);

        var exception = Assert.Throws<InvalidOperationException>(
            () => registry.Resolve(CreateAdapter("modbus"), new DataSource()));

        Assert.Contains("modbus", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlcReader_ValidatesAddressAccordingToValueType()
    {
        var adapter = CreateAdapter(DataSourceProtocolKeys.Plc);
        var reader = new PlcDataSourceReader();
        var context = new DataSourceReaderContext(adapter, _ => DataSourceReaderResult<int>.Fail("未调用"));

        Assert.True(reader.ValidateTriggerAddress(context, "D100").IsSuccess);
        var invalidTrigger = reader.ValidateTriggerAddress(context, "M100");
        Assert.False(invalidTrigger.IsSuccess);
        Assert.Equal(DataSourceReaderErrorKind.InvalidAddress, invalidTrigger.ErrorKind);
        Assert.True(reader.ValidateValueAddress(context, new DataSourceValue
        {
            DataType = DataSourceValueType.Int32,
            PlcAddress = "D100",
        }).IsSuccess);
        Assert.True(reader.ValidateValueAddress(context, new DataSourceValue
        {
            DataType = DataSourceValueType.Bool,
            PlcAddress = "M100",
        }).IsSuccess);
        Assert.False(reader.ValidateValueAddress(context, new DataSourceValue
        {
            DataType = DataSourceValueType.Bool,
            PlcAddress = "D100",
        }).IsSuccess);
    }

    [Fact]
    public void Descriptor_RejectsBlankProtocolKey()
    {
        Assert.Throws<ArgumentException>(() => new DataSourceReaderDescriptor(" "));
    }

    [Fact]
    public void Descriptor_NormalizesProtocolKey()
    {
        var descriptor = new DataSourceReaderDescriptor("  SiMuLaTeD  ");

        Assert.Equal("simulated", descriptor.ProtocolKey);
    }

    [Fact]
    public void Instrumentation_RejectsOperationsOutsideDeclaredCapabilities()
    {
        var descriptor = new DataSourceReaderDescriptor(
            "read-only",
            capabilities: new(
                DataSourceReaderOperations.ValueRead,
                new HashSet<DataSourceValueType> { DataSourceValueType.Int32 }));
        var registry = new DataSourceReaderRegistry([new TestReader(descriptor)]);
        var context = new DataSourceReaderContext(
            CreateAdapter("read-only"),
            _ => DataSourceReaderResult<int>.Success(7));

        var resolved = registry.Resolve(CreateAdapter("read-only"), new DataSource());

        Assert.True(resolved.ValidateValueAddress(context, new DataSourceValue { DataType = DataSourceValueType.Int32 }).IsSuccess);
        Assert.False(resolved.ValidateValueAddress(context, new DataSourceValue { DataType = DataSourceValueType.Bool }).IsSuccess);
        Assert.Equal(DataSourceReaderErrorKind.UnsupportedOperation, resolved.ReadTrigger(context, "D100").ErrorKind);
        Assert.False(resolved.ReadValue(context, new DataSourceValue { DataType = DataSourceValueType.Bool }).Content.IsValid);
        Assert.Equal(
            DataSourceReaderErrorKind.UnsupportedOperation,
            resolved.WriteAcknowledgement(context, "D100", 1).ErrorKind);
    }

    [Fact]
    public void Registry_ReportsReaderOperationDiagnostics()
    {
        var reader = new TestReader(new(DataSourceProtocolKeys.Plc));
        var registry = new DataSourceReaderRegistry([reader]);
        var resolved = registry.Resolve(CreateAdapter(DataSourceProtocolKeys.Plc), new DataSource());
        var context = new DataSourceReaderContext(
            CreateAdapter(DataSourceProtocolKeys.Plc),
            _ => DataSourceReaderResult<int>.Success(7));

        resolved.ValidateValueAddress(context, new DataSourceValue { PlcAddress = "D100" });
        resolved.ReadTrigger(context, "D100");
        resolved.ReadValue(context, new DataSourceValue { PlcAddress = "D100" });
        resolved.WriteAcknowledgement(context, "D100", 2);

        var diagnostics = Assert.Single(registry.GetDiagnosticsSnapshot());
        Assert.Equal(1, diagnostics.ResolveCount);
        Assert.Equal(1, diagnostics.ValidationCount);
        Assert.Equal(1, diagnostics.ValidationSuccessCount);
        Assert.Equal(2, diagnostics.ReadCount);
        Assert.Equal(2, diagnostics.ReadSuccessCount);
        Assert.Equal(2, diagnostics.ReadDurationBucketCounts[0]);
        Assert.Equal(1, diagnostics.AcknowledgementCount);
        Assert.Equal(1, diagnostics.AcknowledgementSuccessCount);
        Assert.Equal(1, diagnostics.AcknowledgementDurationBucketCounts[0]);
        Assert.Equal(0, diagnostics.ValidationFailureCount);
    }

    private static IDeviceAdapter CreateAdapter(string protocolKey)
    {
        var adapter = Substitute.For<IDeviceAdapter>();
        adapter.ProtocolKey.Returns(protocolKey);
        adapter.Brand.Returns(PlcBrand.Mitsubishi);
        adapter.AddressCodec.Returns(new MitsubishiAddressCodec());
        adapter.BatchReadCapabilities.Returns(new BatchReadCapabilities(
            SupportsInt32: true,
            MaxInt32Length: 128,
            Int32AddressStride: 1,
            SupportsBool: false,
            MaxBoolLength: 0,
            BoolAddressStride: 1));
        return adapter;
    }

    private sealed class TestReader(DataSourceReaderDescriptor descriptor) : IDataSourceReader
    {
        public DataSourceReaderDescriptor Descriptor { get; } = descriptor;

        public bool CanHandle(IDeviceAdapter adapter, DataSource source) => true;

        public DataSourceReaderResult ValidateTriggerAddress(DataSourceReaderContext context, string address) =>
            DataSourceReaderResult.Success();

        public DataSourceReaderResult ValidateValueAddress(DataSourceReaderContext context, DataSourceValue value) =>
            DataSourceReaderResult.Success();

        public DataSourceReaderResult<int> ReadTrigger(DataSourceReaderContext context, string address) =>
            DataSourceReaderResult<int>.Success(0);

        public DataSourceReaderResult<DataSourceRuntimeValue> ReadValue(DataSourceReaderContext context, DataSourceValue value) =>
            DataSourceReaderResult<DataSourceRuntimeValue>.Success(new(value.DataType));

        public DataSourceReaderResult WriteAcknowledgement(DataSourceReaderContext context, string address, int value) =>
            DataSourceReaderResult.Success();
    }
}