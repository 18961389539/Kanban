using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Xunit;

namespace MainAPP.Tests.Unit;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public sealed class PlcBrandRegistryTests
{
    [Theory]
    [InlineData(PlcBrand.Mitsubishi)]
    [InlineData(PlcBrand.Siemens)]
    [InlineData(PlcBrand.ModbusTcp)]
    [InlineData(PlcBrand.Omron)]
    [InlineData(PlcBrand.Keyence)]
    public void Resolve_AllBuiltInBrands_ReturnsDescriptor(PlcBrand brand)
    {
        var registry = PlcBrandDescriptors.CreateDefault();

        var descriptor = registry.Resolve(brand);

        Assert.Equal(brand, descriptor.Brand);
        Assert.Equal(PlcConfig.GetDefaultPort(brand), descriptor.DefaultPort);
    }

    [Fact]
    public void Descriptors_ContainsAllFiveBrands()
    {
        var registry = PlcBrandDescriptors.CreateDefault();

        Assert.Equal(5, registry.Descriptors.Count);
        Assert.Equal(5, registry.Descriptors.Select(d => d.Brand).Distinct().Count());
    }

    [Fact]
    public void Resolve_UnknownBrand_Throws()
    {
        var registry = PlcBrandDescriptors.CreateDefault();

        Assert.Throws<InvalidOperationException>(() => registry.Resolve((PlcBrand)999));
    }

    [Fact]
    public void Constructor_DuplicateBrand_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => new PlcBrandRegistry(
        [
            new MitsubishiPlcBrandDescriptor(),
            new MitsubishiPlcBrandDescriptor(),
        ]));
    }

    [Theory]
    [InlineData(PlcBrand.Mitsubishi, "HslPlcDriver")]
    [InlineData(PlcBrand.Siemens, "HslSiemensPlcDriver")]
    [InlineData(PlcBrand.ModbusTcp, "HslModbusTcpDriver")]
    [InlineData(PlcBrand.Omron, "HslOmronFinsDriver")]
    [InlineData(PlcBrand.Keyence, "HslKeyenceMcDriver")]
    public void Descriptor_CreateDriver_ReturnsBrandDriver(PlcBrand brand, string expectedTypeName)
    {
        var registry = PlcBrandDescriptors.CreateDefault();
        var descriptor = registry.Resolve(brand);

        using var driver = descriptor.CreateDriver(new PlcConfig { Brand = brand }, NullLoggerFactory.Instance);

        Assert.Equal(expectedTypeName, driver.GetType().Name);
    }

    [Fact]
    public void Descriptor_CreateAddressCodec_ModbusUsesConfigFunctionCodes()
    {
        var registry = PlcBrandDescriptors.CreateDefault();
        var config = new PlcConfig { Brand = PlcBrand.ModbusTcp };
        config.ModbusTcp.RegisterFunction = 4;
        config.ModbusTcp.BitFunction = 2;

        var codec = registry.Resolve(PlcBrand.ModbusTcp).CreateAddressCodec(config);

        // RegisterFunction=4 时 HR 地址应映射为 x=4; 前缀
        Assert.Equal("x=4;100", codec.ToTransportAddress("HR100"));
        Assert.Equal("x=2;5", codec.ToTransportAddress("C5"));
    }

    [Theory]
    [InlineData(PlcBrand.Mitsubishi)]
    [InlineData(PlcBrand.Siemens)]
    [InlineData(PlcBrand.ModbusTcp)]
    [InlineData(PlcBrand.Omron)]
    [InlineData(PlcBrand.Keyence)]
    public void GetBatchReadCapabilities_AllBrands_ReturnsValid(PlcBrand brand)
    {
        var registry = PlcBrandDescriptors.CreateDefault();

        var caps = registry.Resolve(brand).GetBatchReadCapabilities(new PlcConfig { Brand = brand });

        Assert.True(caps.SupportsInt32);
        Assert.True(caps.MaxInt32Length > 0);
        Assert.True(caps.Int32AddressStride > 0);
    }

    [Fact]
    public void Validate_SiemensInvalidModel_ReportsError()
    {
        var registry = PlcBrandDescriptors.CreateDefault();
        var config = new PlcConfig { Brand = PlcBrand.Siemens };
        config.Siemens.Model = "S9999";
        var errors = new List<string>();

        registry.Resolve(PlcBrand.Siemens).Validate(config, errors);

        Assert.Single(errors);
        Assert.Contains("Siemens", errors[0]);
    }

    [Fact]
    public void Validate_ModbusInvalidUnitId_ReportsError()
    {
        var registry = PlcBrandDescriptors.CreateDefault();
        var config = new PlcConfig { Brand = PlcBrand.ModbusTcp };
        config.ModbusTcp.UnitId = 0;
        var errors = new List<string>();

        registry.Resolve(PlcBrand.ModbusTcp).Validate(config, errors);

        Assert.Single(errors);
        Assert.Contains("UnitId", errors[0]);
    }

    [Fact]
    public void Validate_SiemensValidConfig_NoErrors()
    {
        var registry = PlcBrandDescriptors.CreateDefault();
        var config = new PlcConfig { Brand = PlcBrand.Siemens, Port = 102 };
        config.Siemens.Model = "S1500";
        config.Siemens.Rack = 0;
        config.Siemens.Slot = 2;
        config.Siemens.BatchInt32Limit = 40;
        var errors = new List<string>();

        registry.Resolve(PlcBrand.Siemens).Validate(config, errors);

        Assert.Empty(errors);
    }
}

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public sealed class PlcConfigNestedOptionsTests
{
    [Fact]
    public void FlatProxy_ReadsAndWritesNestedOptions()
    {
        var config = new PlcConfig { Brand = PlcBrand.Siemens };

        config.SiemensModel = "S1500";
        config.SiemensRack = 3;
        config.SiemensSlot = 2;
        config.SiemensBatchInt32Limit = 40;

        Assert.Equal("S1500", config.Siemens.Model);
        Assert.Equal(3, config.Siemens.Rack);
        Assert.Equal(2, config.Siemens.Slot);
        Assert.Equal(40, config.Siemens.BatchInt32Limit);
        Assert.Equal("S1500", config.SiemensModel);
        Assert.Equal(3, config.SiemensRack);
    }

    [Fact]
    public void FlatProxy_ModbusWritesNestedOptions()
    {
        var config = new PlcConfig { Brand = PlcBrand.ModbusTcp };

        config.ModbusUnitId = 7;
        config.ModbusRegisterFunction = 4;
        config.ModbusAddressStartWithZero = false;
        config.ModbusBatchInt32Limit = 100;

        Assert.Equal(7, config.ModbusTcp.UnitId);
        Assert.Equal(4, config.ModbusTcp.RegisterFunction);
        Assert.False(config.ModbusTcp.AddressStartWithZero);
        Assert.Equal(100, config.ModbusTcp.BatchInt32Limit);
    }

    [Fact]
    public void FlatProxy_OmronWritesNestedOptions()
    {
        var config = new PlcConfig { Brand = PlcBrand.Omron };

        config.OmronReadSplits = 300;

        Assert.Equal(300, config.Omron.ReadSplits);
    }

    [Fact]
    public void CreateSnapshot_CopiesNestedOptions()
    {
        var config = new PlcConfig { Brand = PlcBrand.Siemens, ProtocolKey = " Simulated " };
        config.Siemens.Model = "S400";
        config.ModbusTcp.UnitId = 5;

        var snapshot = config.CreateSnapshot();
        config.Siemens.Model = "S1200";
        config.ModbusTcp.UnitId = 9;

        Assert.Equal("S400", snapshot.Siemens.Model);
        Assert.Equal(5, snapshot.ModbusTcp.UnitId);
        Assert.Equal("simulated", snapshot.ProtocolKey);
    }

    [Fact]
    public void ProtocolKey_NormalizesAndDefaults()
    {
        var config = new PlcConfig { ProtocolKey = "  Simulated " };

        Assert.Equal("simulated", config.ProtocolKey);

        config.ProtocolKey = " ";

        Assert.Equal(PlcConfig.DefaultProtocolKey, config.ProtocolKey);
    }

    [Fact]
    public void FlatProxySetter_RaisesPropertyChanged()
    {
        var config = new PlcConfig { Brand = PlcBrand.ModbusTcp };
        var changed = new List<string?>();
        config.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        config.ModbusUnitId = 3;

        Assert.Contains(nameof(PlcConfig.ModbusUnitId), changed);
    }

    [Fact]
    public void Serialize_SettingsJson_WritesNestedOptionsWithoutFlatDuplicates()
    {
        var config = new PlcConfig { Brand = PlcBrand.Siemens, ProtocolKey = "Simulated" };
        config.Siemens.Model = "S1500";
        config.ModbusTcp.UnitId = 7;
        config.Omron.ReadSplits = 200;

        var json = JsonSerializer.Serialize(config, AppSettings.JsonOptions);

        Assert.Contains("\"Siemens\"", json);
        Assert.Contains("\"ProtocolKey\": \"simulated\"", json);
        Assert.Contains("\"ModbusTcp\"", json);
        Assert.Contains("\"Omron\"", json);
        Assert.DoesNotContain("\"SiemensModel\"", json);
        Assert.DoesNotContain("\"ModbusUnitId\"", json);
        Assert.DoesNotContain("\"OmronReadSplits\"", json);
    }

    [Fact]
    public void Deserialize_SettingsJson_PopulatesNestedOptions()
    {
        var json = """
            {
                            "ProtocolKey": "SIMULATED",
              "Brand": 2,
              "IpAddress": "10.0.0.8",
              "Port": 102,
              "TimeoutMs": 3000,
              "Siemens": { "Model": "S1500", "Rack": 0, "Slot": 2, "DataFormat": 2, "BatchInt32Limit": 40 },
              "ModbusTcp": { "UnitId": 7, "AddressStartWithZero": false, "RegisterFunction": 4, "BitFunction": 2, "DataFormat": 1, "BatchInt32Limit": 100 },
              "Omron": { "ReadSplits": 300 }
            }
            """;

        var config = JsonSerializer.Deserialize<PlcConfig>(json, AppSettings.JsonOptions)!;

        Assert.Equal(PlcBrand.Siemens, config.Brand);
        Assert.Equal("simulated", config.ProtocolKey);
        Assert.Equal("S1500", config.Siemens.Model);
        Assert.Equal(2, config.Siemens.Slot);
        Assert.Equal(40, config.Siemens.BatchInt32Limit);
        Assert.Equal(7, config.ModbusTcp.UnitId);
        Assert.False(config.ModbusTcp.AddressStartWithZero);
        Assert.Equal(300, config.Omron.ReadSplits);
        // 代理属性与嵌套值一致
        Assert.Equal("S1500", config.SiemensModel);
        Assert.Equal(7, config.ModbusUnitId);
    }

    [Fact]
    public void Deserialize_OldFlatSettings_PopulatesViaProxies()
    {
        var json = """
            {
              "Brand": 3,
              "IpAddress": "10.0.0.9",
              "Port": 502,
              "TimeoutMs": 5000,
              "SiemensModel": "S1200",
              "SiemensRack": 0,
              "SiemensSlot": 1,
              "SiemensDataFormat": 0,
              "SiemensBatchInt32Limit": 55,
              "ModbusUnitId": 9,
              "ModbusAddressStartWithZero": false,
              "ModbusRegisterFunction": 4,
              "ModbusBitFunction": 2,
              "ModbusDataFormat": 1,
              "ModbusBatchInt32Limit": 120,
              "OmronReadSplits": 400
            }
            """;

        var config = JsonSerializer.Deserialize<PlcConfig>(json, AppSettings.JsonOptions)!;

        Assert.Equal("S1200", config.Siemens.Model);
        Assert.Equal(PlcConfig.DefaultProtocolKey, config.ProtocolKey);
        Assert.Equal(9, config.ModbusTcp.UnitId);
        Assert.Equal(4, config.ModbusTcp.RegisterFunction);
        Assert.False(config.ModbusTcp.AddressStartWithZero);
        Assert.Equal(120, config.ModbusTcp.BatchInt32Limit);
        Assert.Equal(400, config.Omron.ReadSplits);
    }
}
