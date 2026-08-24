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
    private const string SingleInstanceMutexName = @"Global\Kanban.MainAPP.SingleInstance";
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(30);
    private readonly IHost _host;
    private Mutex? _singleInstanceMutex;
    private bool _isFirstInstance;
    private string? _singleInstanceError;
    private bool _hostStarted;
    private int _exitStarted;

    public App()
    {
        AcquireSingleInstance();
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

            if (_singleInstanceError is not null)
            {
                Log($"无法建立全局单实例互斥体：{_singleInstanceError}");
                HandyControl.Controls.MessageBox.Show(
                    _singleInstanceError,
                    MainAPP.Resources.Strings.M130,
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown();
                return;
            }

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
            _hostStarted = true;
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
            Shutdown();
            return;
        }
        base.OnStartup(e);
        Log("OnStartup 结束");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (Interlocked.Exchange(ref _exitStarted, 1) != 0)
        {
            base.OnExit(e);
            return;
        }

        var shutdownCompleted = true;
        try
        {
            // Secondary instances never start the host, but still dispose the host built by the constructor.
            if (!_isFirstInstance)
            {
                try { _host.Dispose(); }
                catch (Exception ex) { Debug.WriteLine($"[OnExit] secondary host dispose failed: {ex}"); }
            }
            else
            {
                var shutdownTask = Task.Run(ShutdownCoreAsync);
                if (shutdownTask.Wait(ExitTimeout))
                {
                    var errors = shutdownTask.GetAwaiter().GetResult();
                    if (errors.Count > 0)
                    {
                        HandyControl.Controls.MessageBox.Show(
                            Strings.M355 + "\n\n" + string.Join("\n", errors),
                            Strings.M356,
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                    }
                }
                else
                {
                    shutdownCompleted = false;
                    Debug.WriteLine($"[OnExit] shutdown exceeded {ExitTimeout.TotalSeconds:0}s; process exit will release resources");
                }
            }
        }
        catch (Exception ex)
        {
            try { Serilog.Log.Error(ex, "OnExit 异常"); }
            catch (Exception logEx) { Debug.WriteLine($"[OnExit] Serilog 记录失败: {logEx.Message}"); }
        }
        finally
        {
            // If cleanup timed out, do not release the mutex or close logging while the cleanup task may still use them.
            if (shutdownCompleted)
            {
                SafeReleaseMutex();
                Serilog.Log.CloseAndFlush();
            }

            base.OnExit(e);
        }
    }

    private async Task<List<string>> ShutdownCoreAsync()
    {
        List<string> errors = [];
        try
        {
            if (!_hostStarted)
                return errors;

            try { await _host.Services.GetRequiredService<Services.ApplicationStartupCoordinator>().DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { errors.Add($"停止启动协调器失败: {ex.Message}"); }

            var isRemote = _host.Services.GetRequiredService<IRuntimeMode>().IsRemote;
            if (!isRemote)
            {
                try
                {
                    await _host.Services.GetRequiredService<PlcDataAcquisitionService>().StopAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    errors.Add(string.Format(Strings.F068, ex.Message));
                }

                try
                {
                    await _host.Services.GetRequiredService<Services.ProductionDailyReportService>().StopAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    errors.Add(string.Format(Strings.F191, ex.Message));
                }
            }

            try
            {
                // Remote 模式必须在 Host 释放 SignalR 之前把设备配置推送到 Collector。
                await _host.Services.GetRequiredService<DeviceRepository>().SaveAllAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                errors.Add(string.Format(Strings.F210, ex.Message));
            }

            try
            {
                _host.Services.GetRequiredService<AppSettings>().Save();
            }
            catch (Exception ex)
            {
                errors.Add(string.Format(Strings.F119, ex.Message));
            }

            try
            {
                await _host.StopAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                errors.Add($"停止 Host 失败: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            errors.Add($"退出清理失败: {ex.Message}");
        }
        finally
        {
            try
            {
                if (_host is IAsyncDisposable asyncHost)
                    await asyncHost.DisposeAsync().ConfigureAwait(false);
                else
                    _host.Dispose();
            }
            catch (Exception ex)
            {
                errors.Add($"释放 Host 失败: {ex.Message}");
            }
            _hostStarted = false;
        }

        return errors;
    }

    private static void Log(string message)
    {
        Serilog.Log.Information("{Message}", message);
    }

    private void AcquireSingleInstance()
    {
        try
        {
            _singleInstanceMutex = new Mutex(false, SingleInstanceMutexName);
            try
            {
                _isFirstInstance = _singleInstanceMutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                _isFirstInstance = true;
                Debug.WriteLine("[App] 接管已废弃的全局单实例互斥体");
            }
        }
        catch (Exception ex)
        {
            _singleInstanceError = $"无法建立 MainAPP 全局单实例互斥体：{ex.Message}";
            Debug.WriteLine($"[App] {_singleInstanceError}");
        }
    }

    /// <summary>
    /// 安全释放单实例互斥锁：处理未持有所有权的情况。
    /// </summary>
    /// <remarks>
    /// Mutex.ReleaseMutex 仅在当前线程通过 WaitOne 取得所有权时合法，
    /// 否则抛 ApplicationException: "Object synchronization method was called from an unsynchronized block of code."
    /// AcquireSingleInstance 只在 WaitOne 成功或接管 abandoned mutex 时标记为首实例；
    /// 其他路径只 Dispose 句柄，由 OS 回收互斥体。
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
