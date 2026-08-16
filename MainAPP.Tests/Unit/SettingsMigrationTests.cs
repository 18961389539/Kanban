using Kanban.Collector.Core.Services;
using MainAPP.Services;
using Xunit;

namespace MainAPP.Tests.Unit;


[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","Database")]
public sealed class SettingsMigrationTests
{
    [Fact]
    public void Migrate_LegacyJson_AddsCurrentSchemaVersion()
    {
        var runner = new SettingsMigrationRunner();

        var migrated = runner.Migrate("{\"PollingIntervalMs\":250}");

        Assert.Contains($"\"SchemaVersion\":{SettingsMigrationRunner.CurrentVersion}", migrated);
        Assert.Contains("\"PollingIntervalMs\":250", migrated);
    }

    [Fact]
    public void Migrate_FutureVersion_Throws()
    {
        var runner = new SettingsMigrationRunner();

        Assert.Throws<UnsupportedSettingsVersionException>(() =>
            runner.Migrate("{\"SchemaVersion\":99}"));
    }

    [Fact]
    public void Migrate_V3Settings_AddsSiemensBatchLimit()
    {
        var runner = new SettingsMigrationRunner();

        var migrated = runner.Migrate("{\"SchemaVersion\":3,\"PlcConfig\":{\"Brand\":2}}");

        Assert.Contains($"\"SchemaVersion\":{SettingsMigrationRunner.CurrentVersion}", migrated);
        // V7 迁移后品牌专属参数收敛到嵌套 Siemens 对象，扁平字段被移除
        Assert.Contains("\"BatchInt32Limit\":55", migrated);
        Assert.DoesNotContain("\"SiemensBatchInt32Limit\"", migrated);
    }

    [Fact]
    public void Migrate_V4PduSettings_RenamesBatchLimit()
    {
        var runner = new SettingsMigrationRunner();

        var migrated = runner.Migrate("{\"SchemaVersion\":4,\"PlcConfig\":{\"Brand\":2,\"SiemensPduLength\":100}}");

        Assert.Contains($"\"SchemaVersion\":{SettingsMigrationRunner.CurrentVersion}", migrated);
        Assert.Contains("\"BatchInt32Limit\":20", migrated);
        Assert.DoesNotContain("SiemensPduLength", migrated);
        Assert.DoesNotContain("\"SiemensBatchInt32Limit\"", migrated);
    }

    [Fact]
    public void Migrate_V6FlatOptions_BuildsNestedOptionsAndRemovesFlatProperties()
    {
        var runner = new SettingsMigrationRunner();

        var migrated = runner.Migrate(
            "{\"SchemaVersion\":6,\"PlcConfig\":{\"Brand\":3,\"SiemensModel\":\"S1500\",\"SiemensRack\":0," +
            "\"SiemensSlot\":2,\"SiemensDataFormat\":2,\"SiemensBatchInt32Limit\":40," +
            "\"ModbusUnitId\":7,\"ModbusAddressStartWithZero\":false,\"ModbusRegisterFunction\":4," +
            "\"ModbusBitFunction\":2,\"ModbusDataFormat\":1,\"ModbusBatchInt32Limit\":100," +
            "\"OmronReadSplits\":300}}");

        Assert.Contains("\"Siemens\":{", migrated);
        Assert.Contains("\"Model\":\"S1500\"", migrated);
        Assert.Contains("\"Slot\":2", migrated);
        Assert.Contains("\"ModbusTcp\":{", migrated);
        Assert.Contains("\"UnitId\":7", migrated);
        Assert.Contains("\"RegisterFunction\":4", migrated);
        Assert.Contains("\"Omron\":{", migrated);
        Assert.Contains("\"ReadSplits\":300", migrated);
        // 扁平属性全部移除
        Assert.DoesNotContain("\"SiemensModel\"", migrated);
        Assert.DoesNotContain("\"SiemensRack\"", migrated);
        Assert.DoesNotContain("\"ModbusUnitId\"", migrated);
        Assert.DoesNotContain("\"ModbusAddressStartWithZero\"", migrated);
        Assert.DoesNotContain("\"OmronReadSplits\"", migrated);
    }

    [Fact]
    public void Migrate_V7NestedOptions_KeepsValues()
    {
        var runner = new SettingsMigrationRunner();

        var migrated = runner.Migrate(
            "{\"SchemaVersion\":7,\"PlcConfig\":{\"Brand\":3,\"Siemens\":{\"Model\":\"S300\",\"BatchInt32Limit\":30}," +
            "\"ModbusTcp\":{\"UnitId\":9},\"Omron\":{\"ReadSplits\":200}}}");

        Assert.Contains("\"Model\":\"S300\"", migrated);
        Assert.Contains("\"BatchInt32Limit\":30", migrated);
        Assert.Contains("\"UnitId\":9", migrated);
        Assert.Contains("\"ReadSplits\":200", migrated);
    }

    [Fact]
    public void Migrate_V6MissingBrandOptions_FillsDefaults()
    {
        var runner = new SettingsMigrationRunner();

        var migrated = runner.Migrate("{\"SchemaVersion\":6,\"PlcConfig\":{\"Brand\":2}}");

        Assert.Contains("\"Model\":\"S1200\"", migrated);
        Assert.Contains("\"Rack\":0", migrated);
        Assert.Contains("\"Slot\":1", migrated);
        Assert.Contains("\"ModbusTcp\":{", migrated);
        Assert.Contains("\"Omron\":{", migrated);
        Assert.Contains("\"ReadSplits\":500", migrated);
    }
}
