using Kanban.Contracts.Enums;
using Kanban.Core.Data;
using Kanban.Core.Models;
using Kanban.Core.Services;
using System.IO;
using System.Text.Json;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// 配方存储（RecipeStore）单测：CRUD / 按机型匹配 / JSON 持久化 round-trip。
/// 关键验证点：Items（[JsonInclude] private set 集合）经 SaveAll→LoadAll 后完整还原。
/// </summary>
public class RecipeStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppSettings _appSettings;

    public RecipeStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "KanbanRecipeStore_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _appSettings = new AppSettings { ConfigDirectory = _tempDir };
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private static Recipe NewRecipe(string name, string machineType, params RecipeItem[] items)
    {
        var recipe = new Recipe { Name = name, MachineType = machineType };
        foreach (var i in items) recipe.Items.Add(i);
        return recipe;
    }

    [Fact]
    public void Upsert_GetById_Delete()
    {
        var store = new RecipeStore(_appSettings);
        var recipe = NewRecipe("配方A", "注塑机",
            new RecipeItem { ParamName = "节拍", PlcAddress = "D108", DataType = PlcDataType.Int32, Value = "50" });

        store.Upsert(recipe);
        Assert.Single(store.Recipes);
        Assert.Same(recipe, store.GetById(recipe.Id));

        Assert.True(store.Delete(recipe.Id));
        Assert.Empty(store.Recipes);
        Assert.Null(store.GetById(recipe.Id));
        Assert.False(store.Delete(recipe.Id));
    }

    [Fact]
    public void GetByMachineType_MatchesExactAndGeneric()
    {
        var store = new RecipeStore(_appSettings);
        store.Upsert(NewRecipe("注塑机专用", "注塑机"));
        store.Upsert(NewRecipe("通用配方", ""));
        store.Upsert(NewRecipe("组装机专用", "组装机"));

        var machine = store.GetByMachineType("注塑机");
        Assert.Equal(2, machine.Count);
        Assert.All(machine, r => Assert.True(r.MachineType is "" or "注塑机"));
    }

    [Fact]
    public void ReplaceAll_ReplacesWholeList()
    {
        var store = new RecipeStore(_appSettings);
        store.Upsert(NewRecipe("旧配方", ""));
        store.ReplaceAll(new[] { NewRecipe("新配方1", ""), NewRecipe("新配方2", "") });

        Assert.Equal(2, store.Recipes.Count);
        Assert.Equal("新配方1", store.Recipes[0].Name);
        Assert.Equal("新配方2", store.Recipes[1].Name);
    }

    [Fact]
    public void SaveAll_LoadAll_RoundTripsItems()
    {
        var store = new RecipeStore(_appSettings);
        var recipe = NewRecipe("持久化配方", "注塑机",
            new RecipeItem { ParamName = "节拍", PlcAddress = "D108", DataType = PlcDataType.Int32, Value = "50", Min = 0, Max = 200, Unit = "件/h" },
            new RecipeItem { ParamName = "温度上限", PlcAddress = "D110", DataType = PlcDataType.Int32, Value = "180" });
        store.Upsert(recipe);
        store.SaveAllAsync().GetAwaiter().GetResult();

        Assert.True(File.Exists(store.FilePath));

        var reloaded = new RecipeStore(_appSettings);
        reloaded.LoadAll();

        var loaded = Assert.Single(reloaded.Recipes);
        Assert.Equal("持久化配方", loaded.Name);
        Assert.Equal("注塑机", loaded.MachineType);
        var items = loaded.Items;
        Assert.Equal(2, items.Count);
        Assert.Equal("节拍", items[0].ParamName);
        Assert.Equal("D108", items[0].PlcAddress);
        Assert.Equal(PlcDataType.Int32, items[0].DataType);
        Assert.Equal("50", items[0].Value);
        Assert.Equal(0, items[0].Min);
        Assert.Equal(200, items[0].Max);
        Assert.Equal("件/h", items[0].Unit);
        Assert.Equal("温度上限", items[1].ParamName);
    }

    [Fact]
    public void LoadAll_CorruptFile_BacksUpAndKeepsEmpty()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_appSettings.GetFilePath("recipes.json"))!);
        File.WriteAllText(_appSettings.GetFilePath("recipes.json"), "{ not valid json");

        var store = new RecipeStore(_appSettings);
        store.LoadAll();

        Assert.Empty(store.Recipes);
        Assert.True(File.Exists(_appSettings.GetFilePath("recipes.json") + ".corrupt"));
        Assert.False(File.Exists(_appSettings.GetFilePath("recipes.json")));
    }

    [Fact]
    public void LoadAll_FiltersInvalidEntries()
    {
        var store = new RecipeStore(_appSettings);
        store.Upsert(NewRecipe("合法配方", "",
            new RecipeItem { ParamName = "节拍", PlcAddress = "D108", DataType = PlcDataType.Int32, Value = "50" }));
        store.Upsert(NewRecipe("", "", // 空名 → 校验失败，加载时应跳过
            new RecipeItem { ParamName = "节拍", PlcAddress = "D108", DataType = PlcDataType.Int32, Value = "50" }));
        store.SaveAllAsync().GetAwaiter().GetResult();

        var reloaded = new RecipeStore(_appSettings);
        reloaded.LoadAll();

        var recipe = Assert.Single(reloaded.Recipes);
        Assert.Equal("合法配方", recipe.Name);
        Assert.True(File.Exists(_appSettings.GetFilePath("recipes.json"))); // 原文件保留，可手工修复
    }

    [Fact]
    public void LoadAll_FiltersDuplicateNames_InFile()
    {
        var item = new RecipeItem { ParamName = "节拍", PlcAddress = "D108", DataType = PlcDataType.Int32, Value = "50" };
        var store = new RecipeStore(_appSettings);
        store.Upsert(NewRecipe("同名配方", "", item));
        store.Upsert(NewRecipe("同名配方", "", item)); // 与上一条同机型同名 → 后加载的跳过
        store.SaveAllAsync().GetAwaiter().GetResult();

        var reloaded = new RecipeStore(_appSettings);
        reloaded.LoadAll();

        Assert.Single(reloaded.Recipes);
    }

    [Fact]
    public void LoadAll_NormalizesMachineType()
    {
        // 直接构造带脏机型的 JSON（绕过 Upsert 的 Trim，模拟手工编辑/旧版本文件）
        var raw = new Recipe
        {
            Id = "r1",
            Name = "带空格机型",
            MachineType = "  注塑机  ",
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now,
        };
        raw.Items.Add(new RecipeItem { ParamName = "节拍", PlcAddress = "D108", DataType = PlcDataType.Int32, Value = "50" });
        Directory.CreateDirectory(Path.GetDirectoryName(_appSettings.GetFilePath("recipes.json"))!);
        File.WriteAllText(_appSettings.GetFilePath("recipes.json"),
            JsonSerializer.Serialize(new List<Recipe> { raw }, new JsonSerializerOptions { WriteIndented = true }));

        var store = new RecipeStore(_appSettings);
        store.LoadAll();

        var recipe = Assert.Single(store.Recipes);
        Assert.Equal("注塑机", recipe.MachineType);
    }

    [Fact]
    public void Upsert_NormalizesMachineType()
    {
        var store = new RecipeStore(_appSettings);
        var recipe = NewRecipe("归一化配方", "  组装机  ");

        store.Upsert(recipe);

        Assert.Equal("组装机", recipe.MachineType);
    }

    [Fact]
    public void ReplaceAll_NormalizesMachineType()
    {
        var store = new RecipeStore(_appSettings);
        var recipe = NewRecipe("归一化配方", "  组装机  ");

        store.ReplaceAll(new[] { recipe });

        Assert.Equal("组装机", recipe.MachineType);
    }

    [Fact]
    public void Clone_CreatesIndependentCopy()
    {
        var original = NewRecipe("原始配方", "注塑机",
            new RecipeItem { ParamName = "节拍", PlcAddress = "D108", DataType = PlcDataType.Int32, Value = "50" });

        var clone = original.Clone();

        Assert.NotEqual(original.Id, clone.Id);
        Assert.Equal(original.Name, clone.Name);
        Assert.Equal(original.MachineType, clone.MachineType);
        Assert.Single(clone.Items);
        Assert.NotSame(original.Items[0], clone.Items[0]);
        Assert.Equal("节拍", clone.Items[0].ParamName);
        Assert.Equal("50", clone.Items[0].Value);
    }
}
