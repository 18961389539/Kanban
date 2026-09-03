using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Models;
using MainAPP.Models;
using MainAPP.Services;
using MainAPP.ViewModels;
using MainAPP.Views;
using Xunit;

namespace MainAPP.E2E;

/// <summary>
/// 页面导航端到端流程：模拟用户在全部 13 个侧边栏页面（Index 0-8 + 10/11/12/13）间切换，
/// 验证每次切换无异常、SelectedIndex 同步、各 View Visibility 切换。
/// 说明：测试直接设置 SelectedIndex 遍历（绕过侧边栏角色过滤——Operator 仅见 7 项展示页）；
/// 角色过滤由 <see cref="OperatorRole_HidesGatedPages"/> 断言，管理页渲染由
/// <see cref="AdminRole_GatedPages_Render"/> 覆盖。Index 9 是设备详情上下文页（不在侧边栏）。
/// </summary>
[Collection("E2E")]
public class NavigationFlowTests
{
    private readonly TestHost _host;

    public NavigationFlowTests(TestHost host) => _host = host;

    /// <summary>全部侧边栏可见导航页的 Index 全集（0-8 + 10/11/12/13；9=设备详情隐藏占位）。
    /// 含角色受限页（设备管理=Engineer，设置/运行监控/用户管理/审计=Admin，配方管理=Engineer）。</summary>
    private static readonly int[] s_visiblePageIndexes = [0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 11, 12, 13];

    [Theory]
    [InlineData(0)] // 主页
    [InlineData(1)] // 产线
    [InlineData(2)] // 报警中心
    [InlineData(3)] // 设备管理（Engineer）
    [InlineData(4)] // 工单管理
    [InlineData(5)] // 历史查询
    [InlineData(6)] // 概览（生产复盘）
    [InlineData(7)] // 设置（Admin）
    [InlineData(8)] // 运行监控（Admin）
    [InlineData(10)] // 用户管理（Admin）
    [InlineData(11)] // 审计日志（Admin）
    [InlineData(12)] // 配方管理（Engineer）
    [InlineData(13)] // 采集监控
    public void Navigate_ToEachPage_NoException(int targetIndex)
    {
        _host.ResetState();
        _host.InitializeDatabases();
        _host.LoadDevices();

        _host.RunOnSta(app =>
        {
            var window = _host.GetMainWindow();
            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

            var vm = _host.GetMainWindowViewModel();
            vm.SelectedIndex = targetIndex;
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            window.UpdateLayout();

            Assert.Equal(targetIndex, vm.SelectedIndex);

            window.Hide();
        });
    }

    [Fact]
    public void Navigate_Sequence_HomeToSettingsToHome_NoException()
    {
        _host.ResetState();
        _host.InitializeDatabases();

        _host.RunOnSta(app =>
        {
            var window = _host.GetMainWindow();
            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

            var vm = _host.GetMainWindowViewModel();

            // 模拟用户依次点击每个侧边栏项（13 项：0-8 + 10/11/12/13；9=设备详情隐藏占位）
            foreach (var index in s_visiblePageIndexes)
            {
                vm.SelectedIndex = index;
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
                Assert.Equal(index, vm.SelectedIndex);
            }

            // 再从最后一页切回主页
            vm.SelectedIndex = 0;
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
            Assert.Equal(0, vm.SelectedIndex);

            window.Hide();
        });
    }

    [Fact]
    public void DataSourceMonitoringPage_RendersWithoutXamlException()
    {
        _host.ResetState();
        _host.InitializeDatabases();
        _host.LoadDevices();

        _host.RunOnSta(app =>
        {
            var window = _host.GetMainWindow();
            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

            var repository = _host.Resolve<DeviceRepository>();
            var device = new Device { Id = "monitor-e2e-device", Name = "采集测试设备" };
            var source = new DataSource { Id = "monitor-e2e-source", Name = "测试采集源", Type = "PLC" };
            var value = new DataSourceValue
            {
                Id = "monitor-e2e-value",
                Name = "温度",
                DataType = DataSourceValueType.Int32,
                PlcAddress = "D100",
            };
            value.SetRuntimeValue(
                new DataSourceRuntimeValue(DataSourceValueType.Int32, Int32Value: 42),
                DateTime.Now);
            source.Values.Add(value);
            device.Sources.Add(source);
            repository.ReplaceAll([device]);

            var vm = _host.GetMainWindowViewModel();
            vm.SelectedIndex = NavigationPageCatalog.DataSourceMonitoring.Index;
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
            // OnPageEnter 经 BeginInvoke(Background) 排队 Refresh（行构建），必须泵到 Background
            // 才会执行；只泵 Loaded/Render 时 Rows 恒为空（审查修复 2026-09-03）。
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            window.UpdateLayout();

            Assert.Equal(NavigationPageCatalog.DataSourceMonitoring.Index, vm.SelectedIndex);
            var page = FindDescendant<MainAPP.Views.DataSourceMonitoringView>(window);
            Assert.NotNull(page);
            Assert.True(page!.IsVisible, $"采集监控页未显示：Visibility={page.Visibility}, Opacity={page.Opacity}");
            Assert.True(page.ActualWidth > 0 && page.ActualHeight > 0,
                $"采集监控页未完成布局：Width={page.ActualWidth}, Height={page.ActualHeight}");
            Assert.IsType<DataSourceMonitoringViewModel>(page.DataContext);
            var dataGrid = FindDescendant<DataGrid>(page);
            Assert.NotNull(dataGrid);
            Assert.Single(dataGrid!.Items);

            window.Hide();
        });
    }

    [Fact]
    public void DataSourceMonitoringPage_SwitchesDisplayModes()
    {
        _host.ResetState();
        _host.InitializeDatabases();
        _host.LoadDevices();

        _host.RunOnSta(app =>
        {
            var window = _host.GetMainWindow();
            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

            var repository = _host.Resolve<DeviceRepository>();
            var device = new Device { Id = "monitor-mode-device", Name = "显示模式测试设备" };
            var source = new DataSource { Id = "monitor-mode-source", Name = "测试采集源", Type = "PLC" };
            var normalValue = new DataSourceValue
            {
                Id = "monitor-mode-normal",
                Name = "正常值",
                DataType = DataSourceValueType.Int32,
                PlcAddress = "D100",
            };
            normalValue.SetRuntimeValue(
                new DataSourceRuntimeValue(DataSourceValueType.Int32, Int32Value: 42),
                DateTime.Now);
            var invalidValue = new DataSourceValue
            {
                Id = "monitor-mode-invalid",
                Name = "无效值",
                DataType = DataSourceValueType.Int32,
                PlcAddress = "D101",
            };
            invalidValue.SetRuntimeValue(
                new DataSourceRuntimeValue(DataSourceValueType.Int32, Int32Value: 0, IsValid: false),
                DateTime.Now);
            source.Values.Add(normalValue);
            source.Values.Add(invalidValue);
            device.Sources.Add(source);
            repository.ReplaceAll(new[] { device });

            var vm = _host.GetMainWindowViewModel();
            vm.SelectedIndex = NavigationPageCatalog.DataSourceMonitoring.Index;
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
            // 泵到 Background：驱动 OnPageEnter 排队的 Refresh（Rows/TrendRows 构建），见上
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            window.UpdateLayout();

            var page = FindDescendant<MainAPP.Views.DataSourceMonitoringView>(window);
            Assert.NotNull(page);
            var pageViewModel = Assert.IsType<DataSourceMonitoringViewModel>(page!.DataContext);
            var table = FindDescendant<DataGrid>(page, "MonitorTable");
            var compactCards = FindDescendant<ListBox>(page, "MonitorCompactCards");
            var trend = FindDescendant<FrameworkElement>(page, "MonitorTrend");
            var trendValues = FindDescendant<ListBox>(page, "MonitorTrendValues");
            var trendPlot = FindDescendant<FrameworkElement>(page, "MonitorTrendPlot");
            Assert.NotNull(table);
            Assert.NotNull(compactCards);
            Assert.NotNull(trend);
            Assert.NotNull(trendValues);
            Assert.NotNull(trendPlot);

            pageViewModel.IsTableView = true;
            window.UpdateLayout();
            Assert.Equal(Visibility.Visible, table!.Visibility);
            Assert.Equal(Visibility.Collapsed, compactCards!.Visibility);

            pageViewModel.IsCompactCardsView = true;
            window.UpdateLayout();
            Assert.Equal(Visibility.Collapsed, table.Visibility);
            Assert.Equal(Visibility.Visible, compactCards.Visibility);

            pageViewModel.IsTrendView = true;
            window.UpdateLayout();
            Assert.Equal(Visibility.Visible, trend!.Visibility);
            Assert.Equal(Visibility.Visible, trendValues!.Visibility);
            Assert.Equal(pageViewModel.TrendRows.Count, trendValues.Items.Count);
            Assert.Contains(pageViewModel.TrendRows, row => row.NumericValue is not null);
            Assert.Equal(Visibility.Visible, trendPlot!.Visibility);

            window.Hide();
        });
    }

    [Fact]
    public void Navigate_AllPages_CycleTwice_NoException()
    {
        _host.ResetState();
        _host.InitializeDatabases();

        _host.RunOnSta(app =>
        {
            var window = _host.GetMainWindow();
            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

            var vm = _host.GetMainWindowViewModel();

            for (int cycle = 0; cycle < 2; cycle++)
            {
                foreach (var index in s_visiblePageIndexes)
                {
                    vm.SelectedIndex = index;
                    window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
                }
            }

            // 两轮循环后停留在最后一页（采集监控）
            Assert.Equal(NavigationPageCatalog.DataSourceMonitoring.Index, vm.SelectedIndex);

            window.Hide();
        });
    }

    [Fact]
    public void RuntimeMonitoringPage_HasScrollableContent()
    {
        _host.ResetState();
        _host.InitializeDatabases();

        _host.RunOnSta(app =>
        {
            var window = _host.GetMainWindow();
            var originalState = window.WindowState;
            var originalWidth = window.Width;
            var originalHeight = window.Height;
            try
            {
                window.WindowState = WindowState.Normal;
                window.Width = 1280;
                window.Height = 720;
                window.Show();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

                var vm = _host.GetMainWindowViewModel();
                vm.SelectedIndex = 8;
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
                window.UpdateLayout();

                var page = FindDescendant<MainAPP.Views.RuntimeMonitoringView>(window);
                var scrollViewer = FindDescendant<ScrollViewer>(page);

                Assert.NotNull(page);
                Assert.NotNull(scrollViewer);
                Assert.True(scrollViewer!.ScrollableHeight > 0,
                    $"运行监控页未产生可滚动内容，高度={scrollViewer.ExtentHeight}, 视口={scrollViewer.ViewportHeight}");
            }
            finally
            {
                window.Hide();
                window.WindowState = originalState;
                window.Width = originalWidth;
                window.Height = originalHeight;
            }
        });
    }

    /// <summary>
    /// 默认（未登录=Operator）视角下，管理页不应出现在侧边栏导航项中（NavigationPageCatalog.RequiredRole）：
    /// 设备管理/配方管理（Engineer）、设置/运行监控/用户管理/审计日志（Admin）均被 RebuildSidebarItems 过滤。
    /// Operator 仅可见 7 个无角色限制的展示页（Index 0/1/2/4/5/6/13）。
    /// </summary>
    [Fact]
    public void OperatorRole_HidesGatedPages()
    {
        _host.ResetState();
        _host.InitializeDatabases();

        _host.RunOnSta(app =>
        {
            var vm = _host.GetMainWindowViewModel();

            // 未登录（Operator）：NavItems 只含无角色限制的 7 项（0/1/2/4/5/6/13）
            Assert.Equal(7, vm.NavItems.Count);
            var visibleIndexes = vm.NavItems.Select(n => n.Index).ToHashSet();
            foreach (var gated in new[] { 3, 7, 8, 10, 11, 12 })
                Assert.False(visibleIndexes.Contains(gated), $"Operator 视角下不应出现导航项 Index={gated}");
        });
    }

    /// <summary>
    /// Admin 登录后：侧边栏恢复全部 13 项，且角色受限页（用户管理/审计日志/配方管理）可导航并渲染对应 View。
    /// 这是新增管理页（UserManager/Audit/RecipeManager）的最小渲染断言——验证 DI 懒加载 VM 在真实
    /// 视觉树中实例化无异常（空数据目录下渲染空态）。
    /// </summary>
    [Fact]
    public void AdminRole_GatedPages_Render()
    {
        _host.ResetState();
        _host.InitializeDatabases();
        _host.LoadDevices();

        _host.RunOnSta(app =>
        {
            var vm = _host.GetMainWindowViewModel();
            var session = _host.Resolve<UserSession>();
            session.Login(new User { Username = "admin", DisplayName = "管理员", Role = UserRole.Admin });
            vm.RefreshNavigationForCurrentUser();
            Assert.Equal(13, vm.NavItems.Count);

            try
            {
                var window = _host.GetMainWindow();
                window.Show();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

                // 依次导航到三个角色受限页并断言对应 View 已渲染（空数据空态）
                var cases = new (int Index, Type ViewType, string PageName)[]
                {
                    (10, typeof(UserManagerView), "用户管理"),
                    (11, typeof(AuditQueryView), "审计日志"),
                    (12, typeof(RecipeManagerView), "配方管理"),
                };
                foreach (var (index, viewType, pageName) in cases)
                {
                    vm.SelectedIndex = index;
                    window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
                    window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
                    window.UpdateLayout();
                    Assert.Equal(index, vm.SelectedIndex);

                    var view = FindDescendant(window, viewType);
                    Assert.True(view != null, $"导航到「{pageName}」后视觉树中未找到 {viewType.Name}");
                }

                window.Hide();
            }
            finally
            {
                // 恢复默认 Operator 会话，避免污染同集合后续测试
                session.Logout();
                vm.RefreshNavigationForCurrentUser();
                vm.SelectedIndex = 0;
            }
        });
    }

    private static T? FindDescendant<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root is null) return null;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) return match;
            var descendant = FindDescendant<T>(child);
            if (descendant is not null) return descendant;
        }
        return null;
    }

    private static T? FindDescendant<T>(DependencyObject? root, string name) where T : FrameworkElement
    {
        if (root is null) return null;
        if (root is T match && string.Equals(match.Name, name, StringComparison.Ordinal)) return match;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var descendant = FindDescendant<T>(VisualTreeHelper.GetChild(root, index), name);
            if (descendant is not null) return descendant;
        }
        return null;
    }

    private static DependencyObject? FindDescendant(DependencyObject? root, Type viewType)
    {
        if (root is null) return null;
        if (viewType.IsInstanceOfType(root)) return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            var found = FindDescendant(child, viewType);
            if (found is not null) return found;
        }
        return null;
    }
}
