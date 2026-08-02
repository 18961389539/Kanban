using Kanban.Core.Services;
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

        Assert.Contains("\"SchemaVersion\":5", migrated);
        Assert.Contains("\"SiemensBatchInt32Limit\":55", migrated);
    }

    [Fact]
    public void Migrate_V4PduSettings_RenamesBatchLimit()
    {
        var runner = new SettingsMigrationRunner();

        var migrated = runner.Migrate("{\"SchemaVersion\":4,\"PlcConfig\":{\"Brand\":2,\"SiemensPduLength\":100}}");

        Assert.Contains("\"SchemaVersion\":5", migrated);
        Assert.Contains("\"SiemensBatchInt32Limit\":20", migrated);
        Assert.DoesNotContain("SiemensPduLength", migrated);
    }
}
