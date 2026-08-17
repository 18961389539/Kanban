using System.Diagnostics;
using MainAPP.Resources;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using LicenseManager.Models;
using LicenseManager.Services;
using LicenseManager.ViewModels;
using LicenseManager.Views;
using Kanban.Client;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace MainAPP;

public partial class App : Application
{
    private readonly IHost _host;
    private Mutex? _singleInstanceMutex;
    private bool _isFirstInstance;

    public App()
    {
        _singleInstanceMutex = new Mutex(true, "Kanban.MainAPP.SingleInstance", out _isFirstInstance);
        var logDir = Path.Combine(AppContext.BaseDirectory, "Logs");
        _host = Host.CreateDefaultBuilder()
            .UseSerilog((context, loggerConfiguration) =>
            {
                Directory.CreateDirectory(logDir);
                loggerConfiguration
                    .MinimumLevel.Information()
                    .Enrich.FromLogContext()
                    .WriteTo.Async(a => a.File(
                        Path.Combine(logDir, "kanban_.log"),
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: 14,
                        fileSizeLimitBytes: 20_000_000,
                        flushToDiskInterval: TimeSpan.FromSeconds(2),
                        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"));
            })
            .ConfigureServices((context, services) =>
            {
                services.AddMainAppCoreServices();
                services.AddMainAppPresentationServices();
            })
            .Build();
        Converters.PlcAddressValidationRule.SetCodecProvider(() =>
            _host.Services.GetRequiredService<IPlcRuntimeProfileProvider>().Current.AddressCodec);
        Log("App 构造完成 (Host 构建完成)");
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        Log("OnStartup 开始");

        // 全局未捕获异常日志：WPF 运行时异常（XamlParseException 等）在启动流程之外发生时不进 OnStartup 的
        // try/catch，此前静默崩溃无日志（如设备管理页资源缺失闪退）。此处统一记录到 Serilog 并阻止默认的
        // 崩溃弹窗（AppDomain 级异常无法拦截进程退出，Dispatcher 级可 Handled=true 保持窗口存活）。
        DispatcherUnhandledException += (_, args) =>
        {
            Serilog.Log.Error(args.Exception, "UI 线程未处理异常（已记录并继续运行）");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                Serilog.Log.Error(ex, "AppDomain 未处理异常（进程将终止）");
        };

        // OnStartup 是 async void，未捕获异常会直接终止进程且无错误提示。
        // 用 try/catch 包裹整个启动流程（Host.StartAsync / Load / EnsureCreated / MainWindow.Show 等），
        // 任一步骤抛异常时记录日志、提示用户并优雅退出，避免无提示崩溃。
        try
        {
            // 界面语言应用：必须在任何 UI（含单实例提示/激活对话框）弹出之前，
            // 否则对话框按默认中文渲染。⚠ 先 Load 再 Apply：AppSettings 构造默认值是 Zh，
            // 未 Load 时 appSettings.Language 永远为 Zh，Apply 永远应用中文。
            var appSettings = _host.Services.GetRequiredService<AppSettings>();
            appSettings.Load();
            Services.Localization.Apply(appSettings.Language);
            // 同步覆盖 Kanban.Collector.Core 共享的连接状态文案（从 WPF resx 取值，三语统一源）
            Kanban.Collector.Core.Localization.ConnectionStatusMessages.Override(
                MainAPP.Resources.Strings.Conn_Connected,
                MainAPP.Resources.Strings.Conn_Disconnected,
                MainAPP.Resources.Strings.Conn_Lost,
                MainAPP.Resources.Strings.Conn_DisconnectedRetry,
                MainAPP.Resources.Strings.Conn_Connecting,
                MainAPP.Resources.Strings.Conn_ConnectingSuffix,
                MainAPP.Resources.Strings.Conn_RemoteConnecting);
            // 同步覆盖 Kanban.Collector.Core 共享的配置校验消息（三语预设，与 Collector 进程一致）
            Kanban.Collector.Core.Localization.ValidationMessages.ApplyLanguage(
                Services.Localization.GetCultureName(appSettings.Language));
            // 配方校验/下发消息（与配置校验消息同机制）
            Kanban.Collector.Core.Localization.RecipeValidationMessages.ApplyLanguage(
                Services.Localization.GetCultureName(appSettings.Language));
            Log($"界面语言已应用：{appSettings.Language}");

            // 禁止多开：非首个实例直接提示并退出，不启动 Host/采集/PLC，避免所有副作用。
            if (!_isFirstInstance)
            {
                Log("检测到已有实例在运行，禁止多开，准备退出");
                // 启动早期 Growl 容器未就绪，用 HC MessageBox（理由详见下方配置文件损坏处）
                HandyControl.Controls.MessageBox.Show(
                    MainAPP.Resources.Strings.M309,
                    MainAPP.Resources.Strings.M036,
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Shutdown();
                return;
            }

            // 授权检查：在 Host.StartAsync 之前拦截，未激活/试用过期时弹激活对话框。
            // 试用期内或已激活 → 继续启动主程序；激活失败或取消 → 直接退出，不启动后台服务。
            var licenseGate = _host.Services.GetRequiredService<LicenseGate>();
            var licenseStatus = licenseGate.CheckStatus();
            Log($"授权检查完成：{licenseStatus}");

            // 标记本次启动是否刚通过激活对话框完成激活（用于 MainWindow 显示后弹 Growl 反馈）。
            // 仅当启动时非 Active、经过对话框激活后变为 Active 时为 true。
            var justActivated = false;

            if (licenseStatus is not (LicenseStatus.Active or LicenseStatus.Trial))
            {
                // 试用过期/被篡改/机器码不匹配/未激活 → 弹激活对话框
                var activationVm = _host.Services.GetRequiredService<ActivationViewModel>();
                var activationDialog = new ActivationDialog(activationVm);

                activationVm.StatusMessage = licenseStatus switch
                {
                    LicenseStatus.TrialExpired => string.Format(Strings.F215, TrialTracker.TrialDays),
                    LicenseStatus.TrialManipulated => MainAPP.Resources.Strings.M304,
                    LicenseStatus.Expired => MainAPP.Resources.Strings.M305,
                    LicenseStatus.MachineMismatch => MainAPP.Resources.Strings.M306,
                    _ => MainAPP.Resources.Strings.M307,
                };

                var result = activationDialog.ShowDialog();
                if (result != true)
                {
                    Log("用户取消激活，程序退出");
                    Shutdown();
                    return;
                }

                licenseStatus = licenseGate.CurrentStatus;
                if (licenseStatus != LicenseStatus.Active)
                {
                    Log($"激活后状态异常：{licenseStatus}，程序退出");
                    Shutdown();
                    return;
                }

                justActivated = true;
            }

            if (licenseStatus == LicenseStatus.Trial && licenseGate.RemainingTrialDays.HasValue)
            {
                Log($"试用期内，剩余 {licenseGate.RemainingTrialDays} 天");
            }

            // 用户登录：在 Host.StartAsync 之后、MainWindow 显示之前。
            // 首次运行 users.json 不存在时 UserStore.Load 会自动创建默认账号（admin/gly, engineer/gcs, operator 免密）；
            // 旧版本升级时 UserStore.Load 的 EnsureOperatorAccount 会补齐缺失的 operator 账号。
            // 非 Viewer 模式默认以管理员（admin）自动登录，全功能页面可直接使用；
            // 极端情况下 admin 缺失时回退 operator 自动登录（保持至少 Operator 权限）。
            // Viewer 模式（屏端大屏）跳过登录，直接以未登录态进入展示页面。
            var userStore = _host.Services.GetRequiredService<UserStore>();
            userStore.Load();

            // 操作审计门面初始化：必须在任何用户登录/保存等可审计操作之前。
            // operatorProvider 提供当前操作人显示名；未登录（Viewer）时为空串。
            Kanban.Collector.Core.Services.AuditLog.Initialize(
                _host.Services.GetService<Kanban.Collector.Core.Services.IAuditService>(),
                () => _host.Services.GetService<Services.UserSession>()?.CurrentUserDisplay ?? string.Empty);

            var appSettingsForLogin = _host.Services.GetRequiredService<AppSettings>();
            if (appSettingsForLogin.RunMode != KanbanRunMode.Viewer)
            {
                var userSession = _host.Services.GetRequiredService<Services.UserSession>();
                // 优先以管理员自动登录（默认账号 admin/gly，首次运行自动创建）；
                var adminUser = userStore.Find("admin");
                if (adminUser is not null)
                {
                    userSession.Login(adminUser);
                    Kanban.Collector.Core.Services.AuditLog.Record("Auth.AutoLogin", "User", adminUser.Username, detail: "启动自动登录");
                    Log($"默认以管理员自动登录：{adminUser.Username}");
                }
                else
                {
                    // 兜底：管理员账号缺失（不应发生）时回退 operator 自动登录，保持至少 Operator 权限。
                    var operatorUser = userStore.Find("operator");
                    if (operatorUser is not null)
                    {
                        userSession.Login(operatorUser);
                        Kanban.Collector.Core.Services.AuditLog.Record("Auth.AutoLogin", "User", operatorUser.Username, detail: "启动自动登录（管理员账号缺失，回退 Operator）");
                        Log($"管理员账号缺失，回退以 Operator 自动登录：{operatorUser.Username}");
                    }
                    else
                    {
                        // 极端情况：admin/operator 账号均缺失（不应发生，EnsureOperatorAccount 已补齐）。
                        // 以未登录态继续，UserSession.CurrentRole 默认回退为 Operator，应用仍可正常使用。
                        Serilog.Log.Warning("admin/operator 账号缺失，以未登录态继续（默认 Operator 权限）");
                    }
                }
            }

            await _host.StartAsync();
            Log("Host.StartAsync 完成");
            var startupCoordinator = _host.Services.GetRequiredService<Services.ApplicationStartupCoordinator>();

            // 业务初始化（配置加载、字号、基线、设备仓储、数据库迁移、工单加载、MainWindow 实例化）
            // 统一委托给 ApplicationStartupCoordinator.PrepareAsync。
            // App.xaml.cs 只负责 WPF 生命周期与授权流程；PrepareAsync 内部按顺序约束执行，
            // 并在每步更新 ApplicationRuntime 状态供 UI 绑定，避免状态机不同步。
            var mainWindow = await startupCoordinator.PrepareAsync();
            mainWindow.Show();
            Log("MainWindow.Show 调用完成 (窗口已可见，但首帧尚未渲染)");

            // Show() 返回后让 UI 线程处理消息队列，使首帧（ContentRendered）尽快渲染，
            // 避免后续同步工作（RefreshLicenseStatus / RefreshNavigationForCurrentUser）阻塞渲染导致黑屏。
            await System.Windows.Threading.Dispatcher.Yield(
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            // 刷新 MainWindow 侧边栏授权状态显示（LicenseGate 已在启动早期检查完毕）
            var mainVm = _host.Services.GetRequiredService<ViewModels.MainWindowViewModel>();
            mainVm.RefreshLicenseStatus();
            // 登录后刷新导航项：按当前用户角色过滤侧边栏可见页面
            mainVm.RefreshNavigationForCurrentUser();

            // 启动时刚通过激活对话框激活成功 → 弹 Growl 反馈授权信息。
            if (justActivated)
            {
                var expireText = licenseGate.CurrentLicense?.IsPermanent == false
                    ? string.Format(Strings.F042, licenseGate.CurrentLicense.ExpireDate)
                    : MainAPP.Resources.Strings.M308;
                HandyControl.Controls.Growl.Success(
                    string.Format(Strings.F163, expireText));
            }
            else if (licenseStatus == LicenseStatus.Trial && licenseGate.RemainingTrialDays.HasValue)
            {
                HandyControl.Controls.Growl.Info(
                    string.Format(Strings.F214, licenseGate.RemainingTrialDays));
            }

            // 历史清理和采集启动放到后台，不阻塞 UI 线程。
            try
            {
                await startupCoordinator.StartRuntimeAsync();
                Log("OnStartup 后台任务全部完成");
            }
            catch (Exception ex)
            {
                // 后台初始化失败不崩溃应用（OnStartup 是 async void，未捕获异常会终止进程），
                // 记录日志并提示用户，窗口保持可用，用户至少能查看/修改配置。
                // 此时 MainWindow 已 Show，Growl 容器已就绪，用非模态通知避免阻塞。
                Log($"后台初始化失败: {ex.Message}");
                HandyControl.Controls.Growl.Warning(
                    string.Format(Strings.F133, ex.Message));
            }
        }
        catch (Exception ex)
        {
            // 启动主流程异常（Host.StartAsync / Load / EnsureCreated / MainWindow.Show 等）：
            // async void 未捕获异常会终止进程，此处捕获后记录日志并提示用户，再优雅退出。
            // 尽可能释放已构造的 Host 资源，避免互斥锁残留导致下次启动误判。
            try { Serilog.Log.Error(ex, "OnStartup 启动失败"); }
            catch (Exception logEx) { System.Diagnostics.Debug.WriteLine($"[OnStartup] Serilog 记录失败: {logEx.Message}"); }
            HandyControl.Controls.MessageBox.Show(
                string.Format(Strings.F178, ex.Message),
                Strings.M130,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            // IHost 实现 IAsyncDisposable，但 .NET 10 中 DisposeAsync 通过 IAsyncDisposable 接口提供。
            // 显式转换为 IAsyncDisposable 后调用 DisposeAsync，避免依赖扩展方法。
            try { (_host as IAsyncDisposable)?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3)); }
            catch (Exception disposeEx) { System.Diagnostics.Debug.WriteLine($"[OnStartup] Host.DisposeAsync 失败: {disposeEx.Message}"); }
            SafeReleaseMutex();
            Shutdown();
            return;
        }
        base.OnStartup(e);
        Log("OnStartup 结束");
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        // 非首个实例（被禁止多开而退出）不做任何持久化，避免空内存数据覆盖首个实例的文件。
        if (!_isFirstInstance)
        {
            SafeReleaseMutex();
            base.OnExit(e);
            return;
        }

        // 退出全程用 try/catch/finally 保护：任何步骤抛异常都不能阻断 base.OnExit 与互斥锁释放，
        // 否则 Host 托管资源泄漏、互斥锁残留导致下次启动误判为"已存在实例"。
        List<string> errors = [];
        try
        {
            // 退出顺序约束（审查修复 2026-08-15）：
            // - 本地模式：先停止采集循环，避免保存设备/历史数据时采集线程仍在并发修改 Runtime 状态、入队 HistoryService。
            //   StopAsync 会写入离线状态转换记录，避免停机时段被算进上一状态导致重启后 OEE 历史虚高。
            // - Remote 模式：先经 SignalR 推送设备配置保存（SaveAllAsync），再释放 Collector 连接；
            //   此前先释放连接再保存，SaveAllAsync 因连接已断开必然失败丢失设备配置。
            if (!_host.Services.GetRequiredService<IRuntimeMode>().IsRemote)
            {
                try
                {
                    await _host.Services.GetRequiredService<PlcDataAcquisitionService>().StopAsync();
                }
                catch (Exception ex)
                {
                    errors.Add(string.Format(Strings.F068, ex.Message));
                }

                try
                {
                    await _host.Services.GetRequiredService<Services.ProductionDailyReportService>().StopAsync();
                }
                catch (Exception ex)
                {
                    errors.Add(string.Format(Strings.F191, ex.Message));
                }
            }

            try
            {
                // Remote 模式经 SignalR 推送 Collector 落盘（async 等待避免退出时丢数据）
                await _host.Services.GetRequiredService<DeviceRepository>().SaveAllAsync();
            }
            catch (Exception ex)
            {
                errors.Add(string.Format(Strings.F210, ex.Message));
            }

            // Remote 模式：释放 Collector 连接（必须在 SaveAllAsync 之后，见上方退出顺序约束）
            if (_host.Services.GetRequiredService<IRuntimeMode>().IsRemote)
            {
                try
                {
                    var client = _host.Services.GetRequiredService<KanbanDataClient>();
                    if (client is IAsyncDisposable disposable)
                        await disposable.DisposeAsync();
                }
                catch (Exception ex)
                {
                    errors.Add(string.Format(Strings.F235, ex.Message));
                }
            }
            try
            {
                _host.Services.GetRequiredService<AppSettings>().Save();
            }
            catch (Exception ex)
            {
                errors.Add(string.Format(Strings.F119, ex.Message));
            }

            // HistoryService.Dispose 改用 DisposeAsync，避免在 UI 线程同步阻塞最多 3 秒。
            try
            {
                await _host.Services.GetRequiredService<HistoryService>().DisposeAsync();
            }
            catch (Exception ex)
            {
                errors.Add(string.Format(Strings.F076, ex.Message));
            }

            if (errors.Count > 0)
            {
                // 退出阶段：用 HC MessageBox 模态阻塞，确保用户在应用关闭前看到保存失败信息
                // （Growl 是非模态通知，应用关闭时会被立即销毁，用户来不及看到）
                HandyControl.Controls.MessageBox.Show(
                    Strings.M355 + "\n\n" + string.Join("\n", errors),
                    Strings.M356,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            await _host.StopAsync();
        }
        catch (Exception ex)
        {
            // 退出阶段任何未预期异常都记录到 Serilog（CloseAndFlush 之前），不阻断 finally 清理
            try { Serilog.Log.Error(ex, "OnExit 异常"); }
            catch (Exception logEx) { System.Diagnostics.Debug.WriteLine($"[OnExit] Serilog 记录失败: {logEx.Message}"); }
        }
        finally
        {
            // 在 Serilog.Log.CloseAndFlush 之前显式释放关键 IDisposable 服务：
            // _host.Dispose 会在最后调用，但其日志在 CloseAndFlush 之后无法落盘，
            // 这里提前释放以确保 Dispose 阶段日志可被捕获，便于追踪关闭阶段资源泄漏。
            foreach (var disposable in new object?[]
            {
                _host.Services.GetService<ViewModels.HomeViewModel>(),
                _host.Services.GetService<IPlcDriver>(), // HslPlcDriver：释放 PLC socket
            })
            {
                if (disposable is IDisposable d)
                {
                    try { d.Dispose(); }
                    catch (Exception ex)
                    {
                        try { Serilog.Log.Warning(ex, "退出阶段显式释放 {Type} 失败", d.GetType().Name); }
                        catch (Exception logEx) { System.Diagnostics.Debug.WriteLine($"[OnExit] Serilog 记录释放 {d.GetType().Name} 失败失败: {logEx.Message}"); }
                    }
                }
            }

            // 退出前冲刷 Serilog 异步缓冲（确保运行期日志落盘）
            Serilog.Log.CloseAndFlush();

            // _host.Dispose 释放所有 DI 注册的 IDisposable 单例（含未在上面显式释放的服务）。
            // 此时 Serilog 已 CloseAndFlush，catch 中的日志无法落盘，用 Debug.WriteLine 兜底输出到调试器。
            try { _host.Dispose(); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[OnExit] _host.Dispose 抛异常: {ex}");
            }

            // 释放单实例互斥锁（仅在持有所有权的首个实例上执行）
            // SafeReleaseMutex 内部处理未持有所有权的情况，避免 ApplicationException 二次崩溃
            SafeReleaseMutex();

            // base.OnExit 必须执行，否则 WPF 关闭流程不完整
            base.OnExit(e);
        }
    }

    private static void Log(string message)
    {
        Serilog.Log.Information("{Message}", message);
    }

    /// <summary>
    /// 安全释放单实例互斥锁：处理未持有所有权的情况。
    /// </summary>
    /// <remarks>
    /// Mutex.ReleaseMutex 仅在当前线程通过 WaitOne 取得所有权时合法，
    /// 否则抛 ApplicationException: "Object synchronization method was called from an unsynchronized block of code."
    /// 构造时 new Mutex(true, ...) 在 createdNew=true 时隐式获得所有权，但异常路径可能跳过获取；
    /// 直接 Dispose 由 OS 回收，无需显式 ReleaseMutex。
    /// </remarks>
    private void SafeReleaseMutex()
    {
        if (_singleInstanceMutex == null)
        {
            return;
        }
        try { _singleInstanceMutex.ReleaseMutex(); }
        catch (ApplicationException) { /* 未持有所有权，无需释放 */ }
        catch (ObjectDisposedException) { /* 已释放 */ }
        finally
        {
            try { _singleInstanceMutex.Dispose(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[SafeReleaseMutex] Dispose 失败: {ex.Message}"); }
        }
    }
}
