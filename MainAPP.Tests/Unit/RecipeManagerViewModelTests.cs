using System.IO;
using System.Windows;
using Kanban.Core.Data;
using Kanban.Core.Models;
using Kanban.Core.Services;
using MainAPP.Resources;
using MainAPP.Services;
using MainAPP.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// RecipeManagerViewModel 命令流测试（审查修复 2026-08-13 补 0 覆盖盲区）：
/// 新建/保存（校验失败与成功）/删除（确认与取消）/克隆/参数项增删/外部集合变更刷新。
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public class RecipeManagerViewModelTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppSettings _appSettings;
    private readonly RecipeStore _recipeStore;
    private readonly DeviceRepository _deviceRepo;
    private readonly FakeDialogService _dialog;

    public RecipeManagerViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "RecipeVmTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _appSettings = new AppSettings { ConfigDirectory = _tempDir };
        _recipeStore = new RecipeStore(_appSettings);
        _recipeStore.LoadAll();
        _deviceRepo = new DeviceRepository(_appSettings);
        _dialog = new FakeDialogService();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private RecipeManagerViewModel CreateVm(IRuntimeMode? runtimeMode = null)
    {
        runtimeMode ??= LocalMode();
        return new RecipeManagerViewModel(
            _recipeStore,
            null!, // RecipeApplier：本测试不触达下发
            null!, // KanbanDataClient：本测试不触达 Remote 下发
            runtimeMode,
            _deviceRepo,
            _dialog,
            new RecipeJsonIOService(_recipeStore, _dialog, NullLogger<RecipeJsonIOService>.Instance),
            NullLogger<RecipeManagerViewModel>.Instance);
    }

    private static IRuntimeMode LocalMode()
    {
        var mode = Substitute.For<IRuntimeMode>();
        mode.IsRemote.Returns(false);
        return mode;
    }

    private static Recipe MakeRecipe(string id, string name, string machineType = "", string address = "D100")
        => new()
        {
            Id = id,
            Name = name,
            MachineType = machineType,
            Items = { new RecipeItem { ParamName = "节拍", PlcAddress = address, DataType = Kanban.Contracts.Enums.PlcDataType.Int32, Value = "50" } },
        };

    [Fact]
    public void NewRecipe_ClearsSelection_AndPrepopulatesOneItem()
    {
        _recipeStore.ReplaceAll([MakeRecipe("r1", "配方A")]);
        var vm = CreateVm();
        vm.SelectedRecipe = vm.AvailableRecipes[0];

        vm.NewRecipeCommand.Execute(null);

        Assert.Null(vm.SelectedRecipe);
        Assert.Single(vm.EditItems);
        Assert.Equal("", vm.EditName);
    }

    [Fact]
    public async Task SaveRecipe_Valid_PersistsAndSelects()
    {
        var vm = CreateVm();
        vm.NewRecipeCommand.Execute(null);
        vm.EditName = "新配方";
        vm.EditItems[0].ParamName = "节拍";
        vm.EditItems[0].PlcAddress = "D200";
        vm.EditItems[0].Value = "60";

        await vm.SaveRecipeCommand.ExecuteAsync(null);

        Assert.Single(_recipeStore.Recipes);
        Assert.Equal("新配方", _recipeStore.Recipes[0].Name);
        Assert.NotEmpty(_dialog.Success);
        Assert.Same(_recipeStore.Recipes[0], vm.SelectedRecipe);
    }

    [Fact]
    public async Task SaveRecipe_InvalidAddress_ShowsWarning_NotPersisted()
    {
        var vm = CreateVm();
        vm.NewRecipeCommand.Execute(null);
        vm.EditName = "非法配方";
        vm.EditItems[0].ParamName = "节拍";
        vm.EditItems[0].PlcAddress = "无效地址";
        vm.EditItems[0].Value = "60";

        await vm.SaveRecipeCommand.ExecuteAsync(null);

        Assert.Empty(_recipeStore.Recipes);
        Assert.NotEmpty(_dialog.Warning);
        Assert.Empty(_dialog.Success);
    }

    [Fact]
    public async Task DeleteRecipe_Confirmed_RemovesAndPersists()
    {
        _recipeStore.ReplaceAll([MakeRecipe("r1", "配方A")]);
        _recipeStore.SaveAll();
        var vm = CreateVm();
        _dialog.ShowResult = MessageBoxResult.Yes;

        await vm.DeleteRecipeCommand.ExecuteAsync(null);

        Assert.Empty(_recipeStore.Recipes);
        Assert.Empty(vm.AvailableRecipes);
    }

    [Fact]
    public async Task DeleteRecipe_Cancelled_KeepsRecipe()
    {
        _recipeStore.ReplaceAll([MakeRecipe("r1", "配方A")]);
        _recipeStore.SaveAll();
        var vm = CreateVm();
        _dialog.ShowResult = MessageBoxResult.No;

        await vm.DeleteRecipeCommand.ExecuteAsync(null);

        Assert.Single(_recipeStore.Recipes);
    }

    [Fact]
    public void CloneRecipe_CreatesIndependentCopy_WithSuffix()
    {
        _recipeStore.ReplaceAll([MakeRecipe("r1", "配方A")]);
        var vm = CreateVm();
        vm.SelectedRecipe = vm.AvailableRecipes[0];

        vm.CloneRecipeCommand.Execute(null);

        Assert.Null(vm.SelectedRecipe);
        Assert.Equal("配方A" + Strings.K692, vm.EditName);
        Assert.Single(vm.EditItems);
        Assert.Single(_recipeStore.Recipes); // 尚未保存，不落库
    }

    [Fact]
    public void AddItem_RemoveItem_UpdateEditorCollection()
    {
        var vm = CreateVm();
        vm.NewRecipeCommand.Execute(null);
        var count = vm.EditItems.Count;

        vm.AddItemCommand.Execute(null);
        Assert.Equal(count + 1, vm.EditItems.Count);

        var item = vm.EditItems[0];
        vm.RemoveItemCommand.Execute(item);
        Assert.DoesNotContain(vm.EditItems, i => ReferenceEquals(i, item));
    }

    [Fact]
    public void ExternalStoreChange_RefreshesAvailableRecipes()
    {
        var vm = CreateVm();
        Assert.Empty(vm.AvailableRecipes);

        _recipeStore.ReplaceAll([MakeRecipe("r1", "配方A")]); // 触发 CollectionChanged

        Assert.Single(vm.AvailableRecipes);
    }

    [Fact]
    public void ExportRecipes_EmptyStore_ShowsWarning()
    {
        var vm = CreateVm();

        vm.ExportRecipesCommand.Execute(null);

        Assert.NotEmpty(_dialog.Warning);
    }
}
