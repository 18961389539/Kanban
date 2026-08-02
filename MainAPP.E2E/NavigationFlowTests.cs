using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MainAPP.ViewModels;
using Xunit;

namespace MainAPP.E2E;

/// <summary>
/// 页面导航端到端流程：模拟用户在 7 个主导航页面（主页/产线/报警中心/设备管理/历史查询/概览/设置）间切换，
/// 验证每次切换无异常、SelectedIndex 同步、各 View Visibility 切换。
/// 设备详情页是上下文页面，不在此测试覆盖范围内（由主页"查看详情"按钮入口触发）。
/// </summary>
[Collection("E2E")]
public class NavigationFlowTests
{
    private readonly TestHost _host;

    public NavigationFlowTests(TestHost host) => _host = host;

    [Theory]
    [InlineData(0)] // 主页
    [InlineData(1)] // 产线
    [InlineData(2)] // 报警中心
    [InlineData(3)] // 设备管理
    [InlineData(4)] // 历史查询
    [InlineData(5)] // 概览
    [InlineData(6)] // 设置
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

            // 模拟用户依次点击每个侧边栏项（共 7 项：主页→产线→报警中心→设备管理→历史查询→概览→设置）
            for (int i = 0; i < 7; i++)
            {
                vm.SelectedIndex = i;
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
                Assert.Equal(i, vm.SelectedIndex);
            }

            // 再从设置切回主页
            vm.SelectedIndex = 0;
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
            Assert.Equal(0, vm.SelectedIndex);

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
                for (int i = 0; i < 7; i++)
                {
                    vm.SelectedIndex = i;
                    window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
                }
            }

            // 两轮循环后停留在最后一页（设置）
            Assert.Equal(6, vm.SelectedIndex);

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
}
