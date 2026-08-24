using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using NSubstitute;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>所有数据源 reader 都应通过的最小协议契约。</summary>
public abstract class DataSourceReaderContractTestBase
{
    protected abstract IDataSourceReader CreateReader();
    protected abstract string ProtocolKey { get; }

    [Fact]
    public void Reader_DeclaresCoreCapabilities()
    {
        var capabilities = CreateReader().Descriptor.Capabilities;

        Assert.True(capabilities.Supports(DataSourceReaderOperations.TriggerRead));
        Assert.True(capabilities.Supports(DataSourceReaderOperations.ValueRead));
        Assert.True(capabilities.Supports(DataSourceReaderOperations.AcknowledgementWrite));
        Assert.Contains(DataSourceValueType.Int32, capabilities.SupportedValueTypes);
    }

    [Fact]
    public void Reader_ExecutesCoreOperations()
    {
        var adapter = CreateAdapter(ProtocolKey);
        var context = new DataSourceReaderContext(
            adapter,
            _ => DataSourceReaderResult<int>.Success(7));
        var reader = CreateReader();
        var value = new DataSourceValue
        {
            DataType = DataSourceValueType.Int32,
            PlcAddress = "D100",
        };

        Assert.True(reader.ValidateTriggerAddress(context, "D100").IsSuccess);
        Assert.True(reader.ValidateValueAddress(context, value).IsSuccess);
        Assert.True(reader.ReadTrigger(context, "D100").IsSuccess);
        Assert.True(reader.ReadValue(context, value).Content.IsValid);
        Assert.True(reader.WriteAcknowledgement(context, "D100", 1).IsSuccess);
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
        adapter.WriteInt32("D100", 1).Returns(PlcOperationResult.Success());
        return adapter;
    }
}

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public sealed class PlcDataSourceReaderContractTests : DataSourceReaderContractTestBase
{
    protected override string ProtocolKey => DataSourceProtocolKeys.Plc;

    protected override IDataSourceReader CreateReader() => new PlcDataSourceReader();
}

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public sealed class ContractDataSourceReaderContractTests : DataSourceReaderContractTestBase
{
    protected override string ProtocolKey => "contract-test";

    protected override IDataSourceReader CreateReader() => new ContractReader();

    private sealed class ContractReader : IDataSourceReader
    {
        public DataSourceReaderDescriptor Descriptor { get; } = new(
            "contract-test",
            capabilities: DataSourceReaderCapabilities.All);

        public bool CanHandle(IDeviceAdapter adapter, DataSource source) => true;

        public DataSourceReaderResult ValidateTriggerAddress(DataSourceReaderContext context, string address) =>
            DataSourceReaderResult.Success();

        public DataSourceReaderResult ValidateValueAddress(DataSourceReaderContext context, DataSourceValue value) =>
            DataSourceReaderResult.Success();

        public DataSourceReaderResult<int> ReadTrigger(DataSourceReaderContext context, string address) =>
            DataSourceReaderResult<int>.Success(1);

        public DataSourceReaderResult<DataSourceRuntimeValue> ReadValue(DataSourceReaderContext context, DataSourceValue value) =>
            DataSourceReaderResult<DataSourceRuntimeValue>.Success(new(value.DataType, Int32Value: 1));

        public DataSourceReaderResult WriteAcknowledgement(DataSourceReaderContext context, string address, int value) =>
            DataSourceReaderResult.Success();
    }
}
