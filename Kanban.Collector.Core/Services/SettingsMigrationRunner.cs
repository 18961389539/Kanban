using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kanban.Core.Services;

internal interface ISettingsMigration
{
    int FromVersion { get; }
    JsonObject Migrate(JsonObject settings);
}

internal sealed class SettingsMigrationRunner
{
    public const int CurrentVersion = 5;

    private readonly IReadOnlyList<ISettingsMigration> _migrations =
    [
        new Version0To1Migration(),
        new Version1To2Migration(),
        new Version2To3Migration(),
        new Version3To4Migration(),
        new Version4To5Migration(),
    ];

    public string Migrate(string json)
    {
        var node = JsonNode.Parse(json) as JsonObject
            ?? throw new JsonException("settings.json 根节点必须是 JSON 对象。");
        var version = node["SchemaVersion"]?.GetValue<int>() ?? 0;
        if (version > CurrentVersion)
            throw new UnsupportedSettingsVersionException(version, CurrentVersion);

        while (version < CurrentVersion)
        {
            var migration = _migrations.SingleOrDefault(item => item.FromVersion == version)
                ?? throw new InvalidOperationException($"缺少 settings.json 从版本 {version} 开始的迁移器。");
            node = migration.Migrate(node);
            version = migration.FromVersion + 1;
            node["SchemaVersion"] = version;
        }

        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    private sealed class Version0To1Migration : ISettingsMigration
    {
        public int FromVersion => 0;

        public JsonObject Migrate(JsonObject settings) => settings;
    }

    private sealed class Version1To2Migration : ISettingsMigration
    {
        public int FromVersion => 1;

        public JsonObject Migrate(JsonObject settings)
        {
            var plc = settings["PlcConfig"] as JsonObject ?? new JsonObject();
            plc["Brand"] ??= (int)Kanban.Core.Models.PlcBrand.Mitsubishi;
            if (plc["Port"] is null)
            {
                var brand = plc["Brand"]?.GetValue<int>() ?? (int)Kanban.Core.Models.PlcBrand.Mitsubishi;
                plc["Port"] = Kanban.Core.Models.PlcConfig.GetDefaultPort((Kanban.Core.Models.PlcBrand)brand);
            }
            plc["TimeoutMs"] ??= 5000;
            plc["SiemensModel"] ??= "S1200";
            plc["SiemensRack"] ??= 0;
            plc["SiemensSlot"] ??= 1;
            plc["ModbusUnitId"] ??= 1;
            settings["PlcConfig"] = plc;
            return settings;
        }
    }

    private sealed class Version2To3Migration : ISettingsMigration
    {
        public int FromVersion => 2;

        public JsonObject Migrate(JsonObject settings)
        {
            var plc = settings["PlcConfig"] as JsonObject ?? new JsonObject();
            plc["ModbusAddressStartWithZero"] ??= true;
            plc["ModbusRegisterFunction"] ??= 3;
            plc["ModbusBitFunction"] ??= 1;
            plc["ModbusDataFormat"] ??= (int)Kanban.Core.Models.PlcDataFormat.ABCD;
            plc["SiemensDataFormat"] ??= (int)Kanban.Core.Models.PlcDataFormat.ABCD;
            settings["PlcConfig"] = plc;
            return settings;
        }
    }

    private sealed class Version3To4Migration : ISettingsMigration
    {
        public int FromVersion => 3;

        public JsonObject Migrate(JsonObject settings)
        {
            var plc = settings["PlcConfig"] as JsonObject ?? new JsonObject();
            plc["SiemensPduLength"] ??= 240;
            settings["PlcConfig"] = plc;
            return settings;
        }
    }

    private sealed class Version4To5Migration : ISettingsMigration
    {
        public int FromVersion => 4;

        public JsonObject Migrate(JsonObject settings)
        {
            var plc = settings["PlcConfig"] as JsonObject ?? new JsonObject();
            if (plc["SiemensBatchInt32Limit"] is null)
            {
                var oldPduLength = plc["SiemensPduLength"]?.GetValue<int>() ?? 240;
                plc["SiemensBatchInt32Limit"] = Math.Clamp((oldPduLength - 20) / 4, 1, 55);
            }
            plc.Remove("SiemensPduLength");
            settings["PlcConfig"] = plc;
            return settings;
        }
    }
}
