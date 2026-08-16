using System.IO;
using System.Linq;
using System.Windows;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
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
    public void SearchText_FiltersByName_AndByParameterName()
    {
        _recipeStore.ReplaceAll([
            MakeRecipe("r1", "高温 PP 料", "注塑机1"),
            new Recipe { Id = "r2", Name = "透明 ABS", MachineType = "注塑机2", Items = { new RecipeItem { ParamName = "冷却时间" } } },
        ]);
        var vm = CreateVm();
        var filtered = () => vm.FilteredRecipes.Cast<Recipe>().ToList();

        vm.RecipeSearchText = "ABS";
        Assert.Single(filtered());
        Assert.Equal("r2", filtered()[0].Id);

        vm.RecipeSearchText = "冷却时间"; // 匹配参数名
        Assert.Single(filtered());
        Assert.Equal("r2", filtered()[0].Id);

        vm.RecipeSearchText = "";
        Assert.Equal(2, filtered().Count);
        Assert.Equal(2, vm.FilteredCount);
    }

    [Fact]
    public void MachineFilterChips_BuiltWithGeneralFirst_AndAllChipCount()
    {
        _recipeStore.ReplaceAll([
            MakeRecipe("r1", "通用配方"), // 空机型 = 通用
            MakeRecipe("r2", "配方B", "注塑机1"),
            MakeRecipe("r3", "配方C", "注塑机1"),
        ]);
        var vm = CreateVm();

        Assert.Equal(3, vm.MachineTypeFilters.Count); // 全部 + 通用 + 注塑机1
        Assert.Equal(RecipeManagerViewModel.AllMachineTypesFilter, vm.MachineTypeFilters[0].MachineType);
        Assert.Equal(3, vm.MachineTypeFilters[0].Count);
        Assert.Equal("", vm.MachineTypeFilters[1].MachineType); // 通用
        Assert.Equal(1, vm.MachineTypeFilters[1].Count);
        Assert.Equal("注塑机1", vm.MachineTypeFilters[2].MachineType);
        Assert.Equal(2, vm.MachineTypeFilters[2].Count);
        Assert.True(vm.MachineTypeFilters[0].IsSelected); // 默认全部选中
    }

    [Fact]
    public void SelectMachineFilter_IncludesGeneralRecipes_AlignsWithGetByMachineType()
    {
        _recipeStore.ReplaceAll([
            MakeRecipe("r1", "通用配方"), // 空机型 = 通用
            MakeRecipe("r2", "配方B", "注塑机1"),
            MakeRecipe("r3", "配方C", "注塑机1"),
        ]);
        var vm = CreateVm();
        var filtered = () => vm.FilteredRecipes.Cast<Recipe>().ToList();

        vm.SelectMachineFilterCommand.Execute(vm.MachineTypeFilters[2]); // 注塑机1
        Assert.Equal("注塑机1", vm.SelectedMachineFilter);
        // 语义对齐 GetByMachineType：通用配方对任意机型可用，筛选具体机型时一并展示
        Assert.Equal(3, filtered().Count);
        Assert.Contains(filtered(), r => r.Id == "r1");
        Assert.False(vm.MachineTypeFilters[0].IsSelected);
        Assert.True(vm.MachineTypeFilters[2].IsSelected);

        vm.SelectMachineFilterCommand.Execute(vm.MachineTypeFilters[1]); // 通用（空机型）
        Assert.Equal(1, filtered().Count);
        Assert.Equal("r1", filtered()[0].Id);

        vm.SelectMachineFilterCommand.Execute(vm.MachineTypeFilters[0]); // 全部
        Assert.Equal(3, filtered().Count);
        Assert.Equal(3, vm.FilteredCount);
    }

    [Fact]
    public async Task SaveThenEdit_DoesNotDirtyStoreRecipe()
    {
        _recipeStore.ReplaceAll([MakeRecipe("r1", "配方A")]);
        var vm = CreateVm();
        vm.SelectedRecipe = vm.AvailableRecipes[0];

        vm.EditItems[0].Value = "80";
        await vm.SaveRecipeCommand.ExecuteAsync(null);

        // 保存后编辑区与库隔离：继续编辑编辑区不得污染库中对象
        vm.EditItems[0].Value = "999";
        Assert.Equal("80", _recipeStore.Recipes[0].Items[0].Value);
        // 保存时入库的是深拷贝：库中参数项与编辑行对象无引用共享
        Assert.NotSame(vm.EditItems[0].Item, _recipeStore.Recipes[0].Items[0]);
    }

    [Fact]
    public async Task SaveRecipe_ResetsFilters_SoNewRecipeIsVisible()
    {
        _recipeStore.ReplaceAll([MakeRecipe("r1", "配方A", "注塑机1")]);
        var vm = CreateVm();
        vm.SelectMachineFilterCommand.Execute(vm.MachineTypeFilters[1]); // 注塑机1
        vm.NewRecipeCommand.Execute(null);
        vm.EditName = "新配方";
        vm.EditItems[0].ParamName = "节拍";
        vm.EditItems[0].PlcAddress = "D200";
        vm.EditItems[0].Value = "60";

        await vm.SaveRecipeCommand.ExecuteAsync(null);

        Assert.Equal(RecipeManagerViewModel.AllMachineTypesFilter, vm.SelectedMachineFilter);
        Assert.Equal("", vm.RecipeSearchText);
        Assert.Equal("新配方", vm.SelectedRecipe?.Name);
        Assert.Contains(vm.FilteredRecipes.Cast<Recipe>(), r => r.Name == "新配方");
    }

    [Fact]
    public void Apply_MachineTypeMismatch_ShowsWarningAndAborts()
    {
        _recipeStore.ReplaceAll([MakeRecipe("r1", "注塑机1配方", "注塑机1")]);
        _deviceRepo.ReplaceAll([new Device { Id = "d1", Name = "组装机1", MachineType = "组装机1" }]);
        var vm = CreateVm();
        vm.SelectedRecipe = vm.AvailableRecipes[0];
        vm.SelectedTargetDevice = vm.TargetDevices[0];
        _dialog.ShowResult = MessageBoxResult.No;

        vm.ApplyRecipeCommand.Execute(null);

        Assert.False(vm.IsApplying);
        Assert.Contains(_dialog.ShowCalls, c => c.Message.Contains("不匹配") || c.Message.Contains("does not match"));
        Assert.Empty(vm.ApplyItemResults);
    }

    [Fact]
    public void EditRow_InlineValidation_FlagsInvalidValueAndAddress()
    {
        var vm = CreateVm();
        vm.NewRecipeCommand.Execute(null);
        var row = vm.EditItems[0];
        row.ParamName = "节拍";
        row.PlcAddress = "D100";
        row.DataType = Kanban.Contracts.Enums.PlcDataType.Int32;
        row.Value = "60";
        Assert.True(row.IsValid);
        Assert.Equal("", row.ErrorText);

        row.Value = "abc"; // 非数值
        Assert.False(row.IsValid);
        Assert.NotEqual("", row.ErrorText);

        row.Value = "100";
        row.Min = 0;
        row.Max = 50; // 越界
        Assert.False(row.IsValid);
        Assert.Contains("上限", row.ErrorText);

        row.Max = 200;
        row.PlcAddress = "X999"; // 地址不可解析
        Assert.False(row.IsValid);
        Assert.Contains("解析", row.ErrorText);
    }

    [Fact]
    public void ExportRecipes_EmptyStore_ShowsWarning()
    {
        var vm = CreateVm();

        vm.ExportRecipesCommand.Execute(null);

        Assert.NotEmpty(_dialog.Warning);
    }
}
