using Kanban.Contracts.Enums;
using Kanban.Core.Data;
using Kanban.Core.Models;
using MainAPP.Models;
using MainAPP.ViewModels;
using System.Windows;
using System.Windows.Threading;
using Xunit;

namespace MainAPP.E2E;

/// <summary>
/// 配方管理页端到端流程（独立导航页）：导航到配方管理 → 目标设备下拉 →
/// 新建配方（表单+参数项）→ 保存落库 → 下发（TestHost 无 PLC 连接，验证失败提示）→ 删除。
/// 验证配方页完整链路无异常、状态正确回写。
/// </summary>
[Collection("E2E")]
public class RecipeFlowTests
{
    private readonly TestHost _host;

    public RecipeFlowTests(TestHost host) => _host = host;

    [Fact]
    public void RecipePage_NewSaveApplyDelete_FullFlow()
    {
        _host.ResetState();
        _host.InitializeDatabases();

        _host.RunOnSta(app =>
        {
            var store = _host.Resolve<IRecipeStore>();
            // Host 共享实例，ResetState 不清理配方库，测试前自清
            store.Recipes.Clear();

            // 造一台"注塑机"设备（先于配方页 VM 首次创建，构造时 RefreshDevices 可加载）
            var device = new Device { Name = "注塑机E2E", MachineType = "注塑机" };
            _host.Resolve<DeviceRepository>().Devices.Add(device);

            var window = _host.GetMainWindow();
            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

            // 导航到配方管理页（独立导航项）
            var mainVm = _host.GetMainWindowViewModel();
            mainVm.SelectedIndex = NavigationPageCatalog.RecipeManager.Index;
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            window.UpdateLayout();

            var recipeVm = _host.Resolve<RecipeManagerViewModel>();
            Assert.NotNull(recipeVm);

            // 目标设备下拉：确保包含测试设备并选中（VM 可能已在先前测试构造，手动对齐）
            if (!recipeVm.TargetDevices.Contains(device))
                recipeVm.TargetDevices.Add(device);
            recipeVm.SelectedTargetDevice = device;
            Assert.NotNull(recipeVm.SelectedTargetDevice);

            // ── 新建配方（清掉新建时预置的空参数项，避免空项校验失败） ──
            recipeVm.NewRecipeCommand.Execute(null);
            recipeVm.EditItems.Clear();
            recipeVm.EditName = "配方A";
            recipeVm.EditMachineType = "注塑机";
            recipeVm.EditRemark = "E2E 端到端";
            recipeVm.EditItems.Add(new RecipeEditItemRow(new RecipeItem
            {
                ParamName = "节拍",
                PlcAddress = "D108",
                DataType = PlcDataType.Int32,
                Value = "50",
                Min = 0,
                Max = 200,
                Unit = "件/h",
            }));

            // ── 保存落库 ──
            recipeVm.SaveRecipeCommand.Execute(null);
            PumpUntil(window, () => store.Recipes.Count == 1, "保存落库");

            var saved = Assert.Single(store.Recipes);
            Assert.Equal("配方A", saved.Name);
            Assert.Equal("注塑机", saved.MachineType);
            Assert.Equal("E2E 端到端", saved.Remark);
            var item = Assert.Single(saved.Items);
            Assert.Equal("节拍", item.ParamName);
            Assert.Equal("D108", item.PlcAddress);
            Assert.Equal(PlcDataType.Int32, item.DataType);
            Assert.Equal("50", item.Value);

            // 保存后自动选中新配方
            PumpUntil(window, () => recipeVm.SelectedRecipe?.Id == saved.Id, "保存后自动选中");
            Assert.Equal("配方A", recipeVm.SelectedRecipe!.Name);

            // ── 下发：TestHost 不起 PLC 采集 → 应返回失败提示（不抛异常） ──
            Assert.True(recipeVm.ApplyRecipeCommand.CanExecute(null), "下发命令应可执行");
            recipeVm.ApplyRecipeCommand.Execute(null);
            Assert.True(recipeVm.IsApplying, "下发命令应已开始执行");
            PumpUntil(window, () => recipeVm.ApplyStatusKind == MainAPP.ViewModels.ApplyStatusKind.Error, "下发失败提示");
            Assert.Contains("失败", recipeVm.ApplyStatus);
            Assert.False(recipeVm.IsApplying);

            // ── 删除 ──
            recipeVm.DeleteRecipeCommand.Execute(null);
            PumpUntil(window, () => store.Recipes.Count == 0, "删除配方");
            Assert.Empty(store.Recipes);

            window.Hide();
        });
    }

    [Fact]
    public void RecipePage_CloneCreatesIndependentCopy()
    {
        _host.ResetState();
        _host.InitializeDatabases();

        _host.RunOnSta(app =>
        {
            var store = _host.Resolve<IRecipeStore>();
            store.Recipes.Clear();

            var device = new Device { Name = "注塑机E2E", MachineType = "注塑机" };
            _host.Resolve<DeviceRepository>().Devices.Add(device);

            var window = _host.GetMainWindow();
            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

            var mainVm = _host.GetMainWindowViewModel();
            mainVm.SelectedIndex = NavigationPageCatalog.RecipeManager.Index;
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            window.UpdateLayout();

            var recipeVm = _host.Resolve<RecipeManagerViewModel>();
            if (!recipeVm.TargetDevices.Contains(device))
                recipeVm.TargetDevices.Add(device);
            recipeVm.SelectedTargetDevice = device;

            // 先建一个配方并保存
            recipeVm.NewRecipeCommand.Execute(null);
            recipeVm.EditItems.Clear();
            recipeVm.EditName = "原始配方";
            recipeVm.EditMachineType = "注塑机";
            recipeVm.EditItems.Add(new RecipeEditItemRow(new RecipeItem
            {
                ParamName = "节拍",
                PlcAddress = "D108",
                DataType = PlcDataType.Int32,
                Value = "50",
            }));
            recipeVm.SaveRecipeCommand.Execute(null);
            PumpUntil(window, () => store.Recipes.Count == 1, "保存原配方");
            var original = store.Recipes[0];

            // 复制：编辑区变为独立副本（新 Id 语义，保存后不覆盖原配方）
            Assert.True(recipeVm.CloneRecipeCommand.CanExecute(null), "复制命令应可执行（有选中）");
            recipeVm.CloneRecipeCommand.Execute(null);
            Assert.Contains("副本", recipeVm.EditName);
            Assert.Equal("注塑机", recipeVm.EditMachineType);
            Assert.Single(recipeVm.EditItems);
            Assert.Equal("50", recipeVm.EditItems[0].Value);

            recipeVm.SaveRecipeCommand.Execute(null);
            PumpUntil(window, () => store.Recipes.Count == 2, "保存副本");

            Assert.Equal(2, store.Recipes.Count);
            Assert.Contains(store.Recipes, r => r.Id == original.Id); // 原配方未被覆盖
            var clone = store.Recipes.First(r => r.Id != original.Id);
            Assert.NotEqual(original.Id, clone.Id);
            Assert.Contains("副本", clone.Name);
            Assert.Equal(original.MachineType, clone.MachineType);
            Assert.Single(clone.Items);
            Assert.Equal("50", clone.Items[0].Value);

            window.Hide();
        });
    }

    /// <summary>泵 Dispatcher 消息直到条件满足（异步命令完成）；超时抛断言失败（含阶段名）。</summary>
    private static void PumpUntil(Window window, Func<bool> condition, string stage, int timeoutMs = 5000)
    {
        var deadline = DateTime.Now.AddMilliseconds(timeoutMs);
        while (DateTime.Now < deadline && !condition())
        {
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            System.Threading.Thread.Sleep(50);
        }
        Assert.True(condition(), $"等待条件超时：{stage}");
    }
}
