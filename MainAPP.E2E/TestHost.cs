using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using MainAPP.ViewModels;
using MainAPP.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;

namespace MainAPP.E2E;

/// <summary>
/// E2E 测试专用 Host：复刻 App.xaml.cs 的 DI 注册逻辑，但：
/// 1. 使用临时目录的 AppSettings（覆盖默认注册），避免污染真实 %APPDATA%/Kanban
/// 2. 跳过单实例 Mutex（App 构造中的 Mutex 会阻塞并行测试）
/// 3. 跳过 PlcDataAcquisitionService.Start（避免真实 PLC 连接尝试）
/// 4. 初始化临时 SQLite 数据库（EnsureCreated）
/// 5. 在共享 STA 线程上实例化 MainWindow 和 4 个 View（与 App.xaml.cs 一致）
/// </summary>
public sealed class TestHost : IDisposable
{
    public string TempDir { get; }
    public AppSettings AppSettings { get; }
    public IHost Host { get; private set; } = null!;
    public Application Application { get; }
    public Dispatcher Dispatcher { get; }

    // 单一 STA 线程承载 Application：避免 Material.Icons 并发损坏
    private readonly Thread _staThread;
    private readonly ManualResetEventSlim _ready = new();
    private Exception? _initException;

    public TestHost()
    {
        TempDir = Path.Combine(Path.GetTempPath(), "KanbanE2E_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(TempDir);

        // 临时目录的 AppSettings：所有持久化文件都写入 TempDir
        AppSettings = new AppSettings { ConfigDirectory = TempDir };

        Application? app = null;
        Dispatcher? dispatcher = null;

        _staThread = new Thread(() =>
        {
            try
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                app = new Application();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                InjectResources(app);
                Host = BuildHost(AppSettings);
                Host.StartAsync().GetAwaiter().GetResult();
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

        Application = app!;
        Dispatcher = dispatcher!;

        if (_initException != null)
            throw new InvalidOperationException("TestHost 初始化失败", _initException);
    }

    /// <summary>在 STA 线程上执行测试代码。</summary>
    public void RunOnSta(Action<Application> action)
    {
        Exception? caught = null;
        Dispatcher.Invoke(() =>
        {
            try { action(Application); }
            catch (Exception ex) { caught = ex; }
        }, DispatcherPriority.Normal);
        if (caught != null) throw caught;
    }

    /// <summary>
    /// 在 STA 线程上执行操作（不需要 Application 参数）。
    /// 用于修改绑定的 ObservableCollection（如 AppSettings.Shifts、DeviceRepository.Devices），
    /// 避免已实例化的 View 的 CollectionView 在非 UI 线程触发跨线程异常。
    /// </summary>
    public void Run(Action action)
    {
        Exception? caught = null;
        Dispatcher.Invoke(() =>
        {
            try { action(); }
            catch (Exception ex) { caught = ex; }
        }, DispatcherPriority.Normal);
        if (caught != null) throw caught;
    }

    public T Resolve<T>() where T : notnull => Host.Services.GetRequiredService<T>();

    public MainWindow GetMainWindow() => Host.Services.GetRequiredService<MainWindow>();
    public MainWindowViewModel GetMainWindowViewModel() => Host.Services.GetRequiredService<MainWindowViewModel>();

    /// <summary>
    /// 测试间重置状态：清空设备列表、清空数据库表、重置 AppSettings 为默认值。
    /// 由于 TestHost 作为 ICollectionFixture 共享（Application 是进程级单例），
    /// 每个测试方法开始前必须调用以避免前一个测试的状态污染。
    /// 内部会先 EnsureCreated（首次调用时表可能不存在），再清空数据。
    /// 所有操作通过 Dispatcher.Invoke 封送到 STA 线程执行，
    /// 避免 ObservableCollection 在非 UI 线程修改触发 CollectionView 跨线程异常。
    /// </summary>
    public void ResetState()
    {
        Exception? caught = null;
        Dispatcher.Invoke(() =>
        {
            try
            {
                var repo = Host.Services.GetRequiredService<DeviceRepository>();
                Host.Services.GetRequiredService<OverviewViewModel>().InvalidatePendingRefresh();
                repo.Devices.Clear();
                repo.Runtimes.Clear();
                repo.RuntimeMap.Clear();

                // 确保数据库表已创建（首次调用时），再清空数据
                var db = Host.Services.GetRequiredService<DatabaseProvider>();
                using (var ctx = db.CreateProductionLogContext()) ctx.Database.EnsureCreated();
                using (var ctx = db.CreateAlarmEventContext()) ctx.Database.EnsureCreated();
                using (var ctx = db.CreateStatusTransitionContext()) ctx.Database.EnsureCreated();
                using (var ctx = db.CreateDefectHistoryContext()) ctx.Database.EnsureCreated();
                using (var ctx = db.CreateProductionLogContext())
                {
                    ctx.ProductionLogs.RemoveRange(ctx.ProductionLogs);
                    ctx.SaveChanges();
                }
                using (var ctx = db.CreateAlarmEventContext())
                {
                    ctx.AlarmEvents.RemoveRange(ctx.AlarmEvents);
                    ctx.SaveChanges();
                }
                using (var ctx = db.CreateStatusTransitionContext())
                {
                    ctx.StatusTransitions.RemoveRange(ctx.StatusTransitions);
                    ctx.SaveChanges();
                }
                using (var ctx = db.CreateDefectHistoryContext())
                {
                    ctx.DefectSnapshots.RemoveRange(ctx.DefectSnapshots);
                    ctx.SaveChanges();
                }

                // 重置 MainWindow 页面索引
                var vm = GetMainWindowViewModel();
                vm.SelectedIndex = 0;
                Host.Services.GetRequiredService<OverviewViewModel>().SelectedTimeRange = OverviewTimeRange.Hours24;
                Host.Services.GetRequiredService<OverviewViewModel>().SelectedTimeRange = OverviewTimeRange.Hours24;

                // 删除持久化文件（devices.json、settings.json、baselines.json），确保干净起点
                foreach (var file in new[] { "devices.json", "settings.json", "baselines.json" })
                {
                    var path = AppSettings.GetFilePath(file);
                    if (File.Exists(path)) File.Delete(path);
                    var bak = path + ".bak";
                    if (File.Exists(bak)) File.Delete(bak);
                }
                AppSettings.LoadErrorMessage = null;
            }
            catch (Exception ex) { caught = ex; }
        }, DispatcherPriority.Send);
        if (caught != null) throw caught;
    }


    /// <summary>复刻 App.xaml.cs 的 DI 注册逻辑，但 AppSettings 由参数注入（临时目录）。</summary>
    /// <remarks>使用完全限定名 <c>Microsoft.Extensions.Hosting.Host</c> 避免与
    /// <see cref="Host"/> 属性在静态方法中产生标识符冲突。</remarks>
    private static IHost BuildHost(AppSettings appSettings)
    {
        return Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .UseSerilog((_, lc) => lc
                .MinimumLevel.Information()
                .WriteTo.Async(a => a.File(Path.Combine(Path.GetTempPath(), "KanbanE2E_.log"),
                    rollingInterval: RollingInterval.Day)))
            .ConfigureServices((context, services) =>
            {
                services.AddMainAppCoreServices(appSettings);
                services.AddMainAppPresentationServices();
                services.AddSingleton<IDialogService, StubDialogService>();
            })
            .Build();
    }

    /// <summary>初始化临时 SQLite 数据库（与 App.OnStartup 后台任务一致）。</summary>
    public void InitializeDatabases()
    {
        var db = Host.Services.GetRequiredService<DatabaseProvider>();
        using (var ctx = db.CreateProductionLogContext()) ctx.Database.EnsureCreated();
        using (var ctx = db.CreateAlarmEventContext()) ctx.Database.EnsureCreated();
        using (var ctx = db.CreateStatusTransitionContext()) ctx.Database.EnsureCreated();
        using (var ctx = db.CreateDefectHistoryContext()) ctx.Database.EnsureCreated();
        db.EnsureWalModeEnabled();
    }

    /// <summary>加载设备数据（与 App.OnStartup 一致）。
    /// 通过 STA 线程执行，避免 LoadAll 修改 Devices 集合触发已绑定的 CollectionView 跨线程异常。</summary>
    public void LoadDevices()
    {
        Run(() =>
        {
            var repo = Host.Services.GetRequiredService<DeviceRepository>();
            repo.LoadAll();
            Host.Services.GetRequiredService<DeviceManagerViewModel>().DeviceList.RefreshDeviceList();
        });
    }

    /// <summary>注入 App.xaml 中定义的资源字典（与 WpfStaFixture 一致）。</summary>
    private static void InjectResources(Application app)
    {
        var rd = app.Resources;
        rd["AppIcon"] = new DrawingImage
        {
            Drawing = new DrawingGroup
            {
                Children = new DrawingCollection
                {
                    new GeometryDrawing(Brushes.DarkSlateGray, null, Geometry.Parse("M0,0 L64,0 L64,64 L0,64 Z")),
                    new GeometryDrawing(Brushes.DeepSkyBlue, null, Geometry.Parse("M10,11 L54,11 L54,53 L10,53 Z")),
                    new GeometryDrawing(Brushes.DarkSlateGray, null, Geometry.Parse("M16,17 L48,17 L48,47 L16,47 Z")),
                }
            }
        };
        rd.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/HandyControl;component/Themes/SkinDefault.xaml", UriKind.Absolute)
        });
        rd.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/HandyControl;component/Themes/Theme.xaml", UriKind.Absolute)
        });
        rd.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/MainAPP;component/Styles/Brushes.xaml", UriKind.Absolute)
        });

        // 手动注入画刷（替代 Brushes.xaml，语义色用直接颜色保证 ThresholdConverters 一致性）
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
        rd["DangerBrush"] = brush(Color.FromRgb(0xF8, 0x71, 0x71));
        rd["SuccessBrush"] = brush(Color.FromRgb(0x34, 0xD3, 0x99));
        rd["WarningBrush"] = brush(Color.FromRgb(0xFB, 0xBF, 0x24));
        rd["StatusRunBrush"] = brush(Color.FromRgb(0x34, 0xD3, 0x99));
        rd["StatusRunSoftBrush"] = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99)) { Opacity = 0.15 };
        rd["StatusAlarmBrush"] = brush(Color.FromRgb(0xF8, 0x71, 0x71));
        rd["StatusAlarmSoftBrush"] = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)) { Opacity = 0.15 };
        rd["StatusPauseBrush"] = brush(Color.FromRgb(0xFB, 0xBF, 0x24));
        rd["StatusPauseSoftBrush"] = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)) { Opacity = 0.15 };
        rd["StatusIdleBrush"] = brush(Color.FromRgb(0x9C, 0xA3, 0xAF));
        rd["StatusIdleSoftBrush"] = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)) { Opacity = 0.15 };

        // 与 WpfStaFixture 保持一致：Components.xaml/Cards.xaml 通过 StaticResource 引用这三个画刷，
        // 缺失会在视图渲染时抛 "DependencyProperty.UnsetValue 不是 Background 的有效值"
        rd["PrimarySoftBrush"] = brush(Color.FromRgb(0x26, 0x30, 0x40));
        rd["HoverBrush"] = brush(Color.FromRgb(0x1F, 0x27, 0x33));
        rd["OverlayBackgroundBrush"] = new SolidColorBrush(Color.FromArgb(0x80, 0x0E, 0x11, 0x16));

        rd["CardDropShadow"] = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 14,
            Direction = 270,
            Opacity = 0.35,
            ShadowDepth = 3,
            Color = Colors.Black
        };

        string[] dictionaries = { "Converters", "TrackerControl", "Texts", "Cards", "Components", "ProductionLineResources", "HomeResources" };
        foreach (var name in dictionaries)
        {
            rd.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/MainAPP;component/Styles/{name}.xaml", UriKind.Absolute)
            });
        }

        var growlContainer = new System.Windows.Controls.StackPanel();
        HandyControl.Controls.Growl.SetGrowlParent(growlContainer, true);
        rd["GrowlContainer"] = growlContainer;
    }

    public void Dispose()
    {
        try
        {
            Dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            _staThread.Join();
            Host.StopAsync().GetAwaiter().GetResult();
            Host.Dispose();
            var history = Host.Services.GetService<HistoryService>();
            history?.Dispose();
        }
        catch { }

        try { Directory.Delete(TempDir, true); }
        catch { }
    }

    /// <summary>测试用桩 DialogService，避免 MessageBox/Growl 阻塞 E2E 测试。
    /// YesNo 类确认对话框自动返回 Yes（配方下发/删除等流程需要用户确认的场景在 E2E 中自动放行）。</summary>
    private sealed class StubDialogService : IDialogService
    {
        public MessageBoxResult Show(string message, string title, MessageBoxButton buttons, MessageBoxImage icon)
            => buttons is MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel
                ? MessageBoxResult.Yes
                : MessageBoxResult.OK;
        public void NotifySuccess(string message) { }
        public void NotifyWarning(string message) { }
        public void NotifyError(string message) { }
        public void NotifyInfo(string message) { }
        public string? ShowSaveFileDialog(string title, string defaultFileName, string filter) => null;
        public string? ShowOpenFileDialog(string title, string filter) => null;
        public string? ShowPasswordInput(string title, string message) => null;
        public MainAPP.Models.DeviceConfigError? ShowConfigErrors(System.Collections.Generic.IReadOnlyList<MainAPP.Models.DeviceConfigError> errors) => null;
        public Kanban.Collector.Core.Entities.WorkOrder? ShowWorkOrderEditor(Kanban.Collector.Core.Entities.WorkOrder? template, System.Collections.Generic.IReadOnlyList<(string Id, string Name)>? availableDevices = null) => null;
    }
}
