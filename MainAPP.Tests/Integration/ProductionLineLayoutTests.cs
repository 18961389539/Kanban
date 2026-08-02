using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Kanban.Core.Data;
using Kanban.Core.Models;
using MainAPP.Models;
using Kanban.Core.Services;
using MainAPP.Services;
using MainAPP.Tests.Unit;
using MainAPP.ViewModels;
using MainAPP.Views;
using Xunit;

namespace MainAPP.Tests.Integration;

/// <summary>
/// 产线页布局自适应测试：验证不同设备数量下 LayoutMode 与对应布局容器的可见性。
/// </summary>
[Collection("WpfUi")]
[Trait("Category","Integration")]
[Trait("Speed","Slow")]
[Trait("Requires","STA")]
public class ProductionLineLayoutTests : WpfTestHost
{
    public ProductionLineLayoutTests(WpfStaFixture fixture) : base(fixture) { }

    [Theory]
    [InlineData(1, true, false, false)]
    [InlineData(2, true, false, false)]
    [InlineData(4, true, false, false)]
    [InlineData(8, true, false, false)]
    [InlineData(9, false, true, false)]
    [InlineData(15, false, true, false)]
    [InlineData(16, false, false, true)]
    [InlineData(20, false, false, true)]
    public void LayoutMode_Adapts_To_DeviceCount(int deviceCount, bool expectLarge, bool expectMedium, bool expectTable)
    {
        var appSettings = new AppSettings();
        var repo = new DeviceRepository(appSettings);
        var names = new[] { "注塑机A1", "焊接机器人B2", "检测机C3", "包装机D4", "激光机E5" };
        for (int i = 0; i < deviceCount; i++)
        {
            var name = i < names.Length ? names[i] : $"设备-{i + 1}";
            var d = new Device { Name = name, RecipeName = $"配方-{i + 1}" };
            repo.Devices.Add(d);
            repo.Runtimes.Add(new DeviceRuntime(d));
        }
        var conn = new PlcConnectionManager(new FakePlcDriver(), appSettings);
        var selection = new DeviceSelectionService();
        var homeVm = new HomeViewModel(repo, conn, appSettings, null!, selection);
        var lineVm = new ProductionLineViewModel(repo, selection);

        Assert.Equal(expectLarge, lineVm.IsLargeCardsLayout);
        Assert.Equal(expectMedium, lineVm.IsMediumCardsLayout);
        Assert.Equal(expectTable, lineVm.IsTableLayout);
        Assert.Equal(deviceCount, lineVm.LineDevices.Count);
    }

    [Fact]
    public void SelectedDeviceId_Mirrors_SelectionService()
    {
        var appSettings = new AppSettings();
        var repo = new DeviceRepository(appSettings);
        var d = new Device { Name = "设备-1" };
        repo.Devices.Add(d);
        repo.Runtimes.Add(new DeviceRuntime(d));
        var conn = new PlcConnectionManager(new FakePlcDriver(), appSettings);
        var selection = new DeviceSelectionService();
        var homeVm = new HomeViewModel(repo, conn, appSettings, null!, selection);
        var lineVm = new ProductionLineViewModel(repo, selection);

        // 构造即镜像：Home 默认选第一台（写 service）→ 产线页镜像 service 选中
        Assert.Equal(selection.SelectedDeviceId, lineVm.SelectedDeviceId);

        // service 选中 → Home 与产线页都跟随
        selection.SelectedDeviceId = d.Id;
        Assert.Equal(d.Id, lineVm.SelectedDeviceId);
        Assert.Equal(d.Id, homeVm.SelectedDeviceId);

        // service 切换选中 → 产线镜像同步
        selection.SelectedDeviceId = "other-device";
        Assert.Equal("other-device", lineVm.SelectedDeviceId);

        // 清空选中
        selection.SelectedDeviceId = null;
        Assert.Null(lineVm.SelectedDeviceId);
    }

    [Fact]
    public void MediumCards_Visible_When_10_Devices()
    {
        var appSettings = new AppSettings();
        var repo = new DeviceRepository(appSettings);
        for (int i = 0; i < 10; i++)
        {
            var d = new Device { Name = $"设备-{i + 1}", RecipeName = $"配方-{i + 1}" };
            repo.Devices.Add(d);
            repo.Runtimes.Add(new DeviceRuntime(d));
        }
        var conn = new PlcConnectionManager(new FakePlcDriver(), appSettings);
        var selection = new DeviceSelectionService();
        var homeVm = new HomeViewModel(repo, conn, appSettings, null!, selection);
        var lineVm = new ProductionLineViewModel(repo, selection);

        ItemsControl? mediumItemsControl = null;
        RunOnSta(app =>
        {
            var view = new ProductionLineView { DataContext = lineVm };
            var win = new Window { Content = view, Width = 1280, Height = 800 };
            win.Show();
            win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            win.UpdateLayout();
            mediumItemsControl = FindItemsControlWithPanel<UniformGrid>(view);
            win.Close();
        });

        Assert.NotNull(mediumItemsControl);
        Assert.Equal(Visibility.Visible, mediumItemsControl!.Visibility);
        Assert.Empty(BindingErrors);
    }

    [Fact]
    public void Table_Visible_When_16_Devices()
    {
        var appSettings = new AppSettings();
        var repo = new DeviceRepository(appSettings);
        for (int i = 0; i < 16; i++)
        {
            var d = new Device { Name = $"设备-{i + 1}", RecipeName = $"配方-{i + 1}" };
            repo.Devices.Add(d);
            repo.Runtimes.Add(new DeviceRuntime(d));
        }
        var conn = new PlcConnectionManager(new FakePlcDriver(), appSettings);
        var selection = new DeviceSelectionService();
        var homeVm = new HomeViewModel(repo, conn, appSettings, null!, selection);
        var lineVm = new ProductionLineViewModel(repo, selection);

        Assert.True(lineVm.IsTableLayout);

        DockPanel? tablePanel = null;
        int headerCount = 0;
        RunOnSta(app =>
        {
            var view = new ProductionLineView { DataContext = lineVm };
            var win = new Window { Content = view, Width = 1280, Height = 800 };
            win.Show();
            win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            win.UpdateLayout();
            foreach (var dp in FindVisualDescendants<DockPanel>(view))
            {
                if (dp.Visibility == Visibility.Visible) { tablePanel = dp; break; }
            }
            headerCount = CountTextBlocks(view, "设备名");
            win.Close();
        });

        Assert.NotNull(tablePanel);
        Assert.True(headerCount > 0, "表格表头未找到");
        Assert.Empty(BindingErrors);
    }

    /// <summary>查找使用指定 ItemsPanel 的 ItemsControl：通过可视树子孙中的 Panel 实例判断，
    /// 因为 ItemsControl.ItemsPanel 返回 ItemsPanelTemplate 而非 Panel 实例。</summary>
    private static ItemsControl? FindItemsControlWithPanel<TPanel>(DependencyObject root) where TPanel : Panel
    {
        foreach (var ic in FindVisualDescendants<ItemsControl>(root))
        {
            if (ic.Visibility != Visibility.Visible) continue;
            // ItemsControl 的 ItemsPresenter 子节点才是真正的 Panel 实例
            var panel = FindVisualDescendants<TPanel>(ic).FirstOrDefault();
            if (panel != null) return ic;
        }
        return null;
    }
}
