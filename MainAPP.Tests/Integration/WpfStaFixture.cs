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
    /// 审查修复 2026-08-13：此前"手动注入自造画刷替代 Brushes.xaml"——语义色 Success/Warning/Danger
    /// 用直接颜色而非生产 HC Dark 变体，导致 ThresholdConverters 测试断言的是测试自造值，
    /// 生产 Brushes.xaml 颜色变更完全不可检测。改为加载生产 Brushes.xaml/Effects.xaml
    /// （顺序与 App.xaml 一致：HC SkinDefault → Theme → Brushes），测试改为与资源字典引用比较。
    /// </remarks>
    private static void InjectResources(Application app)
    {
        var rd = app.Resources;
        // HC 主题（提供 DarkPrimaryBrush 等 HC 内置资源，含 DarkSuccess/Warning/Danger 变体）
        rd.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/HandyControl;component/Themes/SkinDefault.xaml", UriKind.Absolute)
        });
        rd.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/HandyControl;component/Themes/Theme.xaml", UriKind.Absolute)
        });

        // ── 加载生产资源字典（Brushes/Effects 与 App.xaml 同源，保证测试与生产同一套资源） ──
        string[] dictionaries = { "Brushes", "Effects", "Converters", "TrackerControl", "Texts", "Cards", "Components", "ProductionLineResources", "HomeResources" };
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
