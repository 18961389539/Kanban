using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kanban.Collector.Core.Services;

internal interface ISettingsMigration
{
    int FromVersion { get; }
    JsonObject Migrate(JsonObject settings);
}

internal sealed class SettingsMigrationRunner
{
    public const int CurrentVersion = 7;

    private readonly IReadOnlyList<ISettingsMigration> _migrations =
    [
        new Version0To1Migration(),
        new Version1To2Migration(),
        new Version2To3Migration(),
        new Version3To4Migration(),
        new Version4To5Migration(),
        new Version5To6Migration(),
        new Version6To7Migration(),
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
            plc["Brand"] ??= (int)Kanban.Collector.Core.Models.PlcBrand.Mitsubishi;
            if (plc["Port"] is null)
            {
                var brand = plc["Brand"]?.GetValue<int>() ?? (int)Kanban.Collector.Core.Models.PlcBrand.Mitsubishi;
                plc["Port"] = Kanban.Collector.Core.Models.PlcConfig.GetDefaultPort((Kanban.Collector.Core.Models.PlcBrand)brand);
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
            plc["ModbusDataFormat"] ??= (int)Kanban.Collector.Core.Models.PlcDataFormat.ABCD;
            plc["SiemensDataFormat"] ??= (int)Kanban.Collector.Core.Models.PlcDataFormat.ABCD;
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

    private sealed class Version5To6Migration : ISettingsMigration
    {
        public int FromVersion => 5;

        public JsonObject Migrate(JsonObject settings)
        {
            // 新增 ModbusBatchInt32Limit：将 Modbus 单次批量 Int32 上限写入配置，
            // 消除 HslModbusTcpDriver.BatchReadCapabilities 与 PlcRuntimeProfileProvider
            // 两处独立硬编码 62 的双源真相问题。默认 62 对应 HslCommunication
            // ModbusTcpNet 的单次读寄存器上限 124（每 Int32 占 2 寄存器）。
            var plc = settings["PlcConfig"] as JsonObject ?? new JsonObject();
            plc["ModbusBatchInt32Limit"] ??= 62;
            settings["PlcConfig"] = plc;
            return settings;
        }
    }

    private sealed class Version6To7Migration : ISettingsMigration
    {
        public int FromVersion => 6;

        public JsonObject Migrate(JsonObject settings)
        {
            var plc = settings["PlcConfig"] as JsonObject ?? new JsonObject();
            var siemens = plc["Siemens"] as JsonObject ?? new JsonObject();
            siemens["Model"] ??= ReadOrDefault(plc, "SiemensModel", "S1200");
            siemens["Rack"] ??= ReadOrDefault(plc, "SiemensRack", 0);
            siemens["Slot"] ??= ReadOrDefault(plc, "SiemensSlot", 1);
            siemens["DataFormat"] ??= ReadOrDefault(plc, "SiemensDataFormat", (int)Kanban.Collector.Core.Models.PlcDataFormat.ABCD);
            siemens["BatchInt32Limit"] ??= ReadOrDefault(plc, "SiemensBatchInt32Limit", 55);
            plc["Siemens"] = siemens;
            var modbus = plc["ModbusTcp"] as JsonObject ?? new JsonObject();
            modbus["UnitId"] ??= ReadOrDefault(plc, "ModbusUnitId", 1);
            modbus["AddressStartWithZero"] ??= ReadOrDefault(plc, "ModbusAddressStartWithZero", true);
            modbus["RegisterFunction"] ??= ReadOrDefault(plc, "ModbusRegisterFunction", 3);
            modbus["BitFunction"] ??= ReadOrDefault(plc, "ModbusBitFunction", 1);
            modbus["DataFormat"] ??= ReadOrDefault(plc, "ModbusDataFormat", (int)Kanban.Collector.Core.Models.PlcDataFormat.ABCD);
            modbus["BatchInt32Limit"] ??= ReadOrDefault(plc, "ModbusBatchInt32Limit", 62);
            plc["ModbusTcp"] = modbus;
            var omron = plc["Omron"] as JsonObject ?? new JsonObject();
            omron["ReadSplits"] ??= ReadOrDefault(plc, "OmronReadSplits", 500);
            plc["Omron"] = omron;

            foreach (var property in new[]
            {
                "SiemensModel", "SiemensRack", "SiemensSlot", "SiemensDataFormat", "SiemensBatchInt32Limit",
                "ModbusUnitId", "ModbusAddressStartWithZero", "ModbusRegisterFunction", "ModbusBitFunction",
                "ModbusDataFormat", "ModbusBatchInt32Limit", "OmronReadSplits",
            })
                plc.Remove(property);

            settings["PlcConfig"] = plc;
            return settings;
        }

        private static JsonNode ReadOrDefault<T>(JsonObject source, string propertyName, T defaultValue) =>
            source[propertyName]?.DeepClone() ?? JsonValue.Create(defaultValue)!;
    }
}
