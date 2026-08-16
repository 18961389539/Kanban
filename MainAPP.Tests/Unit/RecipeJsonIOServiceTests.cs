using Kanban.Contracts.Enums;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO;
using System.Text.Json;
using System.Windows;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// 配方 JSON 导入/导出服务（RecipeJsonIOService）单测：真实 RecipeStore（临时目录）
/// + FakeDialogService（可设文件路径/确认结果/通知记录）。
/// 覆盖：导出成功/空库/取消；导入合并 by Id/取消/非法跳过/本地重名跳过/确认拒绝。
/// </summary>
public class RecipeJsonIOServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppSettings _appSettings;
    private readonly RecipeStore _store;
    private readonly FakeDialogService _dialog;
    private readonly RecipeJsonIOService _service;

    public RecipeJsonIOServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "KanbanRecipeJsonIO_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _appSettings = new AppSettings { ConfigDirectory = _tempDir };
        _store = new RecipeStore(_appSettings);
        _dialog = new FakeDialogService();
        _service = new RecipeJsonIOService(_store, _dialog, NullLogger<RecipeJsonIOService>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private static RecipeItem Item(string name = "节拍", string value = "50") =>
        new() { ParamName = name, PlcAddress = "D108", DataType = PlcDataType.Int32, Value = value };

    private static Recipe NewRecipe(string name, string machineType, params RecipeItem[] items)
    {
        var recipe = new Recipe { Name = name, MachineType = machineType };
        foreach (var i in items) recipe.Items.Add(i);
        return recipe;
    }

    // ──────────── 导出 ────────────

    [Fact]
    public void ExportRecipes_WritesJsonFile_WithAllRecipes()
    {
        _store.Upsert(NewRecipe("配方A", "", Item()));
        _store.Upsert(NewRecipe("配方B", "注塑机", Item("温度", "180")));
        var path = Path.Combine(_tempDir, "export.json");
        _dialog.SaveFilePath = path;

        var ok = _service.ExportRecipes();

        Assert.True(ok);
        Assert.True(File.Exists(path));
        Assert.Single(_dialog.Success);
        var parsed = JsonSerializer.Deserialize<List<Recipe>>(File.ReadAllText(path));
        Assert.NotNull(parsed);
        Assert.Equal(2, parsed!.Count);
        Assert.Contains(parsed, r => r.Name == "配方B" && r.Items[0].Value == "180");
    }

    [Fact]
    public void ExportRecipes_EmptyStore_WarnsAndWritesNothing()
    {
        var path = Path.Combine(_tempDir, "export.json");
        _dialog.SaveFilePath = path;

        var ok = _service.ExportRecipes();

        Assert.False(ok);
        Assert.Single(_dialog.Warning);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ExportRecipes_Cancelled_ReturnsFalse()
    {
        _store.Upsert(NewRecipe("配方A", "", Item()));
        _dialog.SaveFilePath = null; // 用户取消保存对话框

        Assert.False(_service.ExportRecipes());
        Assert.Empty(_dialog.Success);
    }

    // ──────────── 导入 ────────────

    [Fact]
    public async Task ImportRecipes_MergesById_UpdatesExistingAndAddsNew()
    {
        var local = NewRecipe("配方X", "注塑机", Item("a", "1"));
        _store.Upsert(local);
        var importPath = Path.Combine(_tempDir, "import.json");
        var updated = NewRecipe("配方X", "注塑机", Item("a", "99"));
        updated.Id = local.Id; // 同 Id → 更新而非新增
        var added = NewRecipe("配方Y", "组装机", Item("b", "2"));
        File.WriteAllText(importPath, JsonSerializer.Serialize(new List<Recipe> { updated, added }));
        _dialog.OpenFilePath = importPath;

        var count = await _service.ImportRecipesAsync();

        Assert.Equal(2, count);
        Assert.Equal(2, _store.Recipes.Count);
        Assert.Equal("99", _store.GetById(local.Id)!.Items[0].Value); // 本地配方被更新
        Assert.Single(_dialog.Success);
    }

    [Fact]
    public async Task ImportRecipes_Cancelled_ReturnsZero()
    {
        _dialog.OpenFilePath = null; // 用户取消打开对话框

        Assert.Equal(0, await _service.ImportRecipesAsync());
        Assert.Empty(_store.Recipes);
    }

    [Fact]
    public async Task ImportRecipes_SkipsInvalidEntries_ImportsValidOnly()
    {
        var importPath = Path.Combine(_tempDir, "import.json");
        File.WriteAllText(importPath, JsonSerializer.Serialize(new List<Recipe>
        {
            NewRecipe("合法配方", "", Item()),
            NewRecipe("", "", Item()), // 空名 → 校验失败跳过
        }));
        _dialog.OpenFilePath = importPath;

        var count = await _service.ImportRecipesAsync();

        Assert.Equal(1, count);
        var recipe = Assert.Single(_store.Recipes);
        Assert.Equal("合法配方", recipe.Name);
        Assert.Contains("跳过 1", _dialog.Success.Single());
    }

    [Fact]
    public async Task ImportRecipes_DuplicateNameVsLocal_Skipped()
    {
        _store.Upsert(NewRecipe("同名配方", ""));
        var importPath = Path.Combine(_tempDir, "import.json");
        File.WriteAllText(importPath, JsonSerializer.Serialize(new List<Recipe>
        {
            NewRecipe("同名配方", ""), // 与本地同机型同名 → 跳过
        }));
        _dialog.OpenFilePath = importPath;

        var count = await _service.ImportRecipesAsync();

        Assert.Equal(0, count);
        Assert.Single(_store.Recipes); // 本地未被改动
        Assert.Single(_dialog.Warning); // 全部跳过 → 警告
    }

    [Fact]
    public async Task ImportRecipes_ConfirmRejected_ImportsNothing()
    {
        var importPath = Path.Combine(_tempDir, "import.json");
        File.WriteAllText(importPath, JsonSerializer.Serialize(new List<Recipe>
        {
            NewRecipe("新配方", ""),
        }));
        _dialog.OpenFilePath = importPath;
        _dialog.ShowResult = MessageBoxResult.No; // 二次确认拒绝

        Assert.Equal(0, await _service.ImportRecipesAsync());
        Assert.Empty(_store.Recipes);
    }

    [Fact]
    public async Task ImportRecipes_InvalidJson_ReportsError()
    {
        var importPath = Path.Combine(_tempDir, "import.json");
        File.WriteAllText(importPath, "{ not valid json");
        _dialog.OpenFilePath = importPath;

        var count = await _service.ImportRecipesAsync();

        Assert.Equal(0, count);
        Assert.Single(_dialog.Error);
        Assert.Empty(_store.Recipes);
    }
}
