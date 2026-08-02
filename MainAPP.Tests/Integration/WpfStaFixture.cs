using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using MainAPP.Converters;

namespace MainAPP.Tests.Integration;

/// <summary>
/// WPF UI 测试共享 Fixture：启动一个长期运行的专用 STA 线程作为 WPF Dispatcher，
/// 并在其上创建单例 <see cref="Application"/>、注入资源。
/// </summary>
/// <remarks>
/// 所有 UI 测试通过 <see cref="Invoke"/> 把测试代码投递到该 STA 线程串行执行，
/// 避免多个 STA 线程并发创建 MaterialIcon 触发 Material.Icons 内部字典并发损坏。
/// 配合 [CollectionDefinition("WpfUi")] 使用，同一 Collection 内默认串行执行。
/// </remarks>
public sealed class WpfStaFixture : IDisposable
{
    private readonly Thread _staThread;
    private readonly Dispatcher _dispatcher;
    private readonly Application _app;
    private readonly ManualResetEventSlim _ready = new();
    private Exception? _initException;

    public WpfStaFixture()
    {
        Application? app = null;
        Dispatcher? dispatcher = null;

        _staThread = new Thread(() =>
        {
            try
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                app = new Application();
                // 关键：默认 OnLastWindowClose 会在每个测试关闭 Window 后终止 Dispatcher，
                // 导致后续测试报"应用程序对象正在关闭"。改为显式关闭，由 Dispose 控制。
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                InjectResources(app);
                PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
                _ready.Set();
                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                _initException = ex;
                _ready.Set();
            }
        });
        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.Start();
        _ready.Wait();
        _dispatcher = dispatcher!;
        _app = app!;

        if (_initException != null)
            throw new InvalidOperationException("WpfStaFixture 初始化失败", _initException);
    }

    /// <summary>当前共享 STA 线程的 Dispatcher。</summary>
    public Dispatcher Dispatcher => _dispatcher;

    /// <summary>共享 Application 单例。</summary>
    public Application Application => _app;

    /// <summary>把测试代码投递到共享 STA 线程同步执行。所有异常原样抛回。</summary>
    public void Invoke(Action<Application> action)
    {
        Exception? caught = null;
        _dispatcher.Invoke(() =>
        {
            try { action(_app); }
            catch (Exception ex) { caught = ex; }
        }, DispatcherPriority.Normal);
        if (caught != null) throw caught;
    }

    public void Dispose()
    {
        _dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        _staThread.Join();
    }

    /// <summary>注入 App.xaml 中定义的资源字典（保证视图 FindResource 可用）。</summary>
    /// <remarks>
    /// 混合策略：手动注入画刷和效果（与 Brushes.xaml/Effects.xaml 内容一致，但语义色
    /// SuccessBrush/WarningBrush/DangerBrush 使用直接颜色而非 HC Dark 变体，确保
    /// ThresholdConverters 静态构造获得测试期望的颜色值），再加载实际 XAML 样式字典
    /// （Converters/TrackerControl/Texts/Cards/Components）以提供完整样式资源
    /// （KpiCard、CardListItemStyle、IconButtonWarning 等）。
    /// </remarks>
    private static void InjectResources(Application app)
    {
        var rd = app.Resources;
        // HC 主题（提供 DarkPrimaryBrush 等 HC 内置资源）
        rd.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/HandyControl;component/Themes/SkinDefault.xaml", UriKind.Absolute)
        });
        rd.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/HandyControl;component/Themes/Theme.xaml", UriKind.Absolute)
        });

        // ── 手动注入画刷（替代 Brushes.xaml，语义色用直接颜色保证 ThresholdConverters 测试一致性） ──
        Func<Color, SolidColorBrush> brush = c => new SolidColorBrush(c);
        rd["BackgroundBrush"] = brush(Color.FromRgb(0x0E, 0x11, 0x16));
        rd["RegionBrush"] = brush(Color.FromRgb(0x1A, 0x20, 0x29));
        rd["SecondaryRegionBrush"] = brush(Color.FromRgb(0x21, 0x28, 0x34));
        rd["DarkBrush"] = brush(Color.FromRgb(0x0F, 0x14, 0x19));
        rd["BorderBrush"] = brush(Color.FromRgb(0x2A, 0x32, 0x3F));
        rd["SecondaryBorderBrush"] = brush(Color.FromRgb(0x3A, 0x44, 0x53));
        rd["DividerBrush"] = brush(Color.FromRgb(0x2A, 0x32, 0x3F));
        rd["PrimaryTextBrush"] = brush(Color.FromRgb(0xE5, 0xE7, 0xEB));
        rd["SecondaryTextBrush"] = brush(Color.FromRgb(0x9C, 0xA3, 0xAF));
        rd["ThirdlyTextBrush"] = brush(Color.FromRgb(0x6B, 0x72, 0x80));
        rd["PrimaryBrush"] = brush(Color.FromRgb(0x3B, 0x82, 0xF6));
        rd["ChartBaseSeriesBrush"] = brush(Color.FromRgb(0x60, 0xA5, 0xFA));
        rd["DangerBrush"] = brush(Color.FromRgb(0xF8, 0x71, 0x71));
        rd["SuccessBrush"] = brush(Color.FromRgb(0x34, 0xD3, 0x99));
        rd["WarningBrush"] = brush(Color.FromRgb(0xFB, 0xBF, 0x24));
        // 状态语义色
        rd["StatusRunBrush"] = brush(Color.FromRgb(0x34, 0xD3, 0x99));
        rd["StatusRunSoftBrush"] = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99)) { Opacity = 0.15 };
        rd["StatusAlarmBrush"] = brush(Color.FromRgb(0xF8, 0x71, 0x71));
        rd["StatusAlarmSoftBrush"] = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)) { Opacity = 0.15 };
        rd["StatusPauseBrush"] = brush(Color.FromRgb(0xFB, 0xBF, 0x24));
        rd["StatusPauseSoftBrush"] = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)) { Opacity = 0.15 };
        rd["StatusIdleBrush"] = brush(Color.FromRgb(0x9C, 0xA3, 0xAF));
        rd["StatusIdleSoftBrush"] = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)) { Opacity = 0.15 };
        // 交互态软色 / 覆盖层（与 Brushes.xaml 保持一致，供 Cards.xaml/Components.xaml 中的样式引用）
        rd["PrimarySoftBrush"] = brush(Color.FromRgb(0x26, 0x30, 0x40));
        rd["HoverBrush"] = brush(Color.FromRgb(0x1F, 0x27, 0x33));
        rd["OverlayBackgroundBrush"] = new SolidColorBrush(Color.FromArgb(0x80, 0x0E, 0x11, 0x16));

        // ── 手动注入效果（替代 Effects.xaml） ──
        rd["CardDropShadow"] = new DropShadowEffect
        {
            BlurRadius = 14,
            Direction = 270,
            Opacity = 0.35,
            ShadowDepth = 3,
            Color = Colors.Black
        };

        // ── 加载实际 XAML 样式字典（Converters/TrackerControl/Texts/Cards/Components） ──
        // 跳过 Brushes.xaml 和 Effects.xaml（已在上方手动注入）
        string[] dictionaries = { "Converters", "TrackerControl", "Texts", "Cards", "Components", "ProductionLineResources", "HomeResources" };
        foreach (var name in dictionaries)
        {
            rd.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/MainAPP;component/Styles/{name}.xaml", UriKind.Absolute)
            });
        }

        // 初始化 Growl 容器：测试环境无 MainWindow，需手动设置 GrowlParent
        var growlContainer = new System.Windows.Controls.StackPanel();
        HandyControl.Controls.Growl.SetGrowlParent(growlContainer, true);
        rd["GrowlContainer"] = growlContainer;
    }
}
