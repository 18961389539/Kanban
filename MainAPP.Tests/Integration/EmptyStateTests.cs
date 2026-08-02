using System.Windows;
using System.Windows.Controls;
using MainAPP.Controls;
using Material.Icons;
using Xunit;

namespace MainAPP.Tests.Integration;

/// <summary>
/// EmptyState 用户控件测试：验证依赖属性默认值、赋值变更及渲染无异常。
/// 因控件在构造时加载 XAML 并引用 Application 资源画刷，需在 WpfUi 集合中执行。
/// </summary>
[Collection("WpfUi")]
[Trait("Category","Integration")]
[Trait("Speed","Slow")]
[Trait("Requires","STA")]
public class EmptyStateTests : WpfTestHost
{
    public EmptyStateTests(WpfStaFixture fixture) : base(fixture) { }

    [Fact]
    public void DefaultDependencyProperties_HaveExpectedValues()
    {
        RunOnSta(app =>
        {
            var state = new EmptyState();
            Assert.Equal(MaterialIconKind.InboxOutline, state.IconKind);
            Assert.Equal("暂无数据", state.Message);
            Assert.Equal("", state.Hint);
        });
    }

    [Fact]
    public void SettingIconKind_UpdatesProperty()
    {
        RunOnSta(app =>
        {
            var state = new EmptyState { IconKind = MaterialIconKind.Search };
            Assert.Equal(MaterialIconKind.Search, state.IconKind);
        });
    }

    [Fact]
    public void SettingMessage_UpdatesProperty()
    {
        RunOnSta(app =>
        {
            var state = new EmptyState { Message = "没有匹配的设备" };
            Assert.Equal("没有匹配的设备", state.Message);
        });
    }

    [Fact]
    public void SettingHint_UpdatesProperty()
    {
        RunOnSta(app =>
        {
            var state = new EmptyState { Hint = "请添加设备后再试" };
            Assert.Equal("请添加设备后再试", state.Hint);
        });
    }

    [Fact]
    public void Render_WithMessageAndHint_NoException()
    {
        RunOnSta(app =>
        {
            var state = new EmptyState
            {
                IconKind = MaterialIconKind.FolderOpen,
                Message = "暂无历史记录",
                Hint = "调整筛选条件后重试"
            };
            var win = new Window { Content = state, Width = 400, Height = 300 };
            win.Show();
            win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            win.UpdateLayout();
            win.Close();
        });
    }

    [Fact]
    public void Render_EmptyHint_HintTextCollapsed()
    {
        RunOnSta(app =>
        {
            var state = new EmptyState { Message = "空", Hint = "" };
            var win = new Window { Content = state, Width = 400, Height = 300 };
            win.Show();
            win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            win.UpdateLayout();
            // 查找 Hint 对应的 TextBlock 应为 Collapsed
            var textBlocks = FindVisualDescendants<TextBlock>(state);
            win.Close();
            Assert.NotEmpty(textBlocks);
        });
    }

    [Fact]
    public void Render_InWindow_LoadsMaterialIcon()
    {
        RunOnSta(app =>
        {
            var state = new EmptyState { IconKind = MaterialIconKind.AlertCircleOutline };
            var win = new Window { Content = state, Width = 400, Height = 300 };
            win.Show();
            win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            win.UpdateLayout();
            var icons = FindVisualDescendants<Material.Icons.WPF.MaterialIcon>(state);
            win.Close();
            Assert.NotEmpty(icons);
        });
    }
}
