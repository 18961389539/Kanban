using System.IO;
using System.Linq;
using System.Text.Json;
using Kanban.Core.Services;
using MainAPP.Services;
using Xunit;

namespace MainAPP.Tests.Integration;

/// <summary>
/// 产量基线存储（ProductionBaselineStore）持久化集成测试（baselines.json）。
/// 覆盖：Save/Load 往返、班次标识保存、旧格式（顶层 Dictionary）兼容置空、
/// 原子写入产生 .bak 备份。使用临时目录隔离，不触碰真实 %APPDATA%/Kanban。
/// 原 AppSettingsBaselinesTests 已迁至此（基线归位于独立 store）。
/// </summary>
[Trait("Category","Integration")]
[Trait("Speed","Slow")]
[Trait("Requires","None")]
public class ProductionBaselineStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppSettings _appSettings;
    private readonly ProductionBaselineStore _store;

    public ProductionBaselineStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "KanbanBaselinesTests_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        // ConfigDirectory 设为绝对临时目录：GetFilePath = Path.Combine(DataRoot, 绝对目录) = 绝对目录
        _appSettings = new AppSettings { ConfigDirectory = _tempDir };
        _store = new ProductionBaselineStore(_appSettings);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void SaveAndLoad_RoundTrip_PreservesBaselinesAndShift()
    {
        var baselines = new System.Collections.Generic.Dictionary<string, int>
        {
            ["dev-001_ok_base"] = 1000,
            ["dev-001_ng_base"] = 50,
            ["dev-002_ok_base"] = 200
        };
        const string shift = "白班|08:00:00|20:00:00";

        _store.SaveBaselines(baselines, shift);
        Assert.Equal(shift, _store.BaselineShiftId);

        var loaded = new ProductionBaselineStore(_appSettings);
        loaded.Load();
        Assert.Equal(shift, loaded.BaselineShiftId);
        Assert.Equal(3, loaded.LoadedBaselines.Count);
        Assert.Equal(1000, loaded.LoadedBaselines["dev-001_ok_base"]);
        Assert.Equal(50, loaded.LoadedBaselines["dev-001_ng_base"]);
        Assert.Equal(200, loaded.LoadedBaselines["dev-002_ok_base"]);
    }

    [Fact]
    public void Load_NonExistentFile_ReturnsEmpty()
    {
        var loaded = new ProductionBaselineStore(_appSettings);
        loaded.Load();
        Assert.Empty(loaded.LoadedBaselines);
    }

    [Fact]
    public void Save_CreatesAtomicBackup_OnSecondWrite()
    {
        _store.SaveBaselines(new() { ["dev-001_ok_base"] = 1 }, "A|..|..");
        var firstFile = _appSettings.GetFilePath("baselines.json");

        // 第一次保存后尚无 .bak（之前文件不存在）
        Assert.False(File.Exists(firstFile + ".bak"));

        // 第二次保存应把上一版本备份为 .bak
        _store.SaveBaselines(new() { ["dev-001_ok_base"] = 2 }, "B|..|..");
        Assert.True(File.Exists(firstFile + ".bak"));
    }

    [Fact]
    public void Load_LegacyTopLevelDictionary_SetsShiftIdToNull()
    {
        // 旧版 baselines.json：顶层即 Dictionary，无班次标识
        var filePath = _appSettings.GetFilePath("baselines.json");
        File.WriteAllText(filePath, "{\"dev-001_ok_base\": 123, \"dev-002_ok_base\": 456}");

        var loaded = new ProductionBaselineStore(_appSettings);
        loaded.Load();

        Assert.Equal(123, loaded.LoadedBaselines["dev-001_ok_base"]);
        Assert.Equal(456, loaded.LoadedBaselines["dev-002_ok_base"]);
        // 旧格式无 ShiftId → 调用方据此判定班次不一致、丢弃旧基线
        Assert.Null(loaded.BaselineShiftId);
    }

    [Fact]
    public void Load_CorruptedJson_ReturnsEmpty()
    {
        var filePath = _appSettings.GetFilePath("baselines.json");
        File.WriteAllText(filePath, "{ not valid json !!!");

        var loaded = new ProductionBaselineStore(_appSettings);
        loaded.Load();
        Assert.Empty(loaded.LoadedBaselines);
    }

    // ──────────── BaselineFile 序列化契约（私有嵌套类，经公开 API 验证）────────────

    [Fact]
    public void SaveBaselines_WritesBaselineFileShape_WithShiftIdAndBaselines()
    {
        // BaselineFile 结构：{ ShiftId, Baselines } —— 经 SaveToFile 落盘后验证 JSON 形状
        _store.SaveBaselines(new() { ["dev-001_ok_base"] = 1000 }, "白班|08:00:00|20:00:00");
        var json = File.ReadAllText(_appSettings.GetFilePath("baselines.json"));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(TryGet(root, "ShiftId", out var shiftProp), "缺少 ShiftId 字段");
        Assert.True(TryGet(root, "Baselines", out var baseProp), "缺少 Baselines 字段");
        Assert.Equal("白班|08:00:00|20:00:00", shiftProp.GetString());
        Assert.Equal(1000, baseProp.GetProperty("dev-001_ok_base").GetInt32());
    }

    [Fact]
    public void SaveBaselines_NullShiftId_SerializesAsNull()
    {
        _store.SaveBaselines(new() { ["dev-001_ok_base"] = 1 }, null);
        var json = File.ReadAllText(_appSettings.GetFilePath("baselines.json"));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(TryGet(root, "ShiftId", out var shiftProp), "缺少 ShiftId 字段");
        Assert.Equal(JsonValueKind.Null, shiftProp.ValueKind);
    }

    [Fact]
    public void GetOrCreate_RecoversDiskBaseline_OnlyWhenShiftMatches()
    {
        // 先落盘 BaselineFile：ShiftId=S1, dev-001_ok_base=100
        _store.SaveBaselines(new() { ["dev-001_ok_base"] = 100 }, "S1");

        // Shift 匹配 → 恢复磁盘基线 100（而非以 raw 50 为新基线）
        var store = new ProductionBaselineStore(_appSettings);
        store.Load();
        Assert.Equal(100, store.GetOrCreate("dev-001_ok_base", 50, "S1"));

        // 不同 Shift → 以 raw 50 为新基线（不误恢复旧班次，避免串账）
        var store2 = new ProductionBaselineStore(_appSettings);
        store2.Load();
        Assert.Equal(50, store2.GetOrCreate("dev-001_ok_base", 50, "S2"));
        Assert.Equal("S2", store2.BaselineShiftId);
    }

    [Fact]
    public void GetOrCreate_PlcCounterRewind_ResetsToRaw()
    {
        // 活动缓存基线=100，raw=80（PLC 计数器回退/外部清零）→ 以 80 为新基线
        _store.SaveBaselines(new() { ["dev-001_ok_base"] = 100 }, "S1");
        Assert.Equal(80, _store.GetOrCreate("dev-001_ok_base", 80, "S1"));
    }

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        // 大小写不敏感查找 BaselineFile 字段（兼容 JsonOptions 命名策略）
        foreach (var prop in element.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
