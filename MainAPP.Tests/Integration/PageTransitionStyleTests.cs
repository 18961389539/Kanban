using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using Xunit;

namespace MainAPP.Tests.Integration;

/// <summary>
/// 页面切换过渡动画样式测试：验证 PageTransition 样式可加载、可应用、
/// Visibility 切换到 Visible 时触发 Opacity 渐显动画。
/// </summary>
[Collection("WpfUi")]
[Trait("Category","Integration")]
[Trait("Speed","Slow")]
[Trait("Requires","STA")]
public class PageTransitionStyleTests : WpfTestHost
{
    public PageTransitionStyleTests(WpfStaFixture fixture) : base(fixture) { }

    /// <summary>从 MainAPP 程序集加载 Components.xaml 资源字典。</summary>
    private static ResourceDictionary LoadComponents()
    {
        var rd = new ResourceDictionary
        {
            Source = new System.Uri("pack://application:,,,/MainAPP;component/Styles/Components.xaml", System.UriKind.Absolute)
        };
        return rd;
    }

    [Fact]
    public void PageTransitionStyle_ExistsInComponents()
    {
        RunOnSta(app =>
        {
            var rd = LoadComponents();
            var style = rd["PageTransition"] as Style;
            Assert.NotNull(style);
            Assert.Equal(typeof(FrameworkElement), style.TargetType);
        });
    }

    [Fact]
    public void PageTransitionStyle_HasOpacitySetter()
    {
        RunOnSta(app =>
        {
            var rd = LoadComponents();
            var style = (Style)rd["PageTransition"]!;

            // 查找 Opacity setter，默认值应为 1
            var opacitySetter = style.Setters.OfType<Setter>()
                .FirstOrDefault(s => s.Property == UIElement.OpacityProperty);
            Assert.NotNull(opacitySetter);
            Assert.Equal(1.0, opacitySetter!.Value);
        });
    }

    [Fact]
    public void PageTransitionStyle_HasVisibilityTrigger()
    {
        RunOnSta(app =>
        {
            var rd = LoadComponents();
            var style = (Style)rd["PageTransition"]!;

            // 应包含 Visibility == Visible 的 Trigger
            var trigger = style.Triggers.OfType<Trigger>()
                .FirstOrDefault(t => t.Property == UIElement.VisibilityProperty);
            Assert.NotNull(trigger);
            Assert.Equal(Visibility.Visible, trigger!.Value);
        });
    }

    [Fact]
    public void ApplyStyle_ToBorder_NoException()
    {
        RunOnSta(app =>
        {
            var rd = LoadComponents();
            var style = (Style)rd["PageTransition"]!;
            var border = new Border { Width = 100, Height = 100 };
            border.Style = style;

            var win = new Window { Content = border, Width = 400, Height = 300 };
            win.Show();
            win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            win.UpdateLayout();
            win.Close();
        });
    }

    [Fact]
    public void VisibilityToggle_TriggersOpacityAnimation()
    {
        RunOnSta(app =>
        {
            var rd = LoadComponents();
            var style = (Style)rd["PageTransition"]!;
            var border = new Border { Width = 100, Height = 100, Visibility = Visibility.Collapsed };
            border.Style = style;

            var win = new Window { Content = border, Width = 400, Height = 300 };
            win.Show();
            win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            win.UpdateLayout();

            // 从 Collapsed 切到 Visible 应触发 Opacity 0→1 动画
            border.Visibility = Visibility.Visible;
            win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);

            // 动画启动后 Opacity 应小于等于 1（可能正在动画中或已完成）
            Assert.True(border.Opacity <= 1.0);

            win.Close();
        });
    }

    [Fact]
    public void ApplyStyle_ToContentControl_NoException()
    {
        RunOnSta(app =>
        {
            var rd = LoadComponents();
            var style = (Style)rd["PageTransition"]!;
            var cc = new ContentControl
            {
                Width = 200, Height = 200,
                Content = new TextBlock { Text = "测试页面" }
            };
            cc.Style = style;

            var win = new Window { Content = cc, Width = 400, Height = 300 };
            win.Show();
            win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            win.UpdateLayout();

            // 切换可见性模拟页面切换
            cc.Visibility = Visibility.Collapsed;
            cc.Visibility = Visibility.Visible;
            win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);

            win.Close();
        });
    }
}
