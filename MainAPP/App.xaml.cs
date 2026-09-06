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
using Kanban.Collector.Core.Localization;
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
        // try/catch，此前静默崩溃无日志（如设备管理页资源缺失闪退）。此处统一记录到 Serilog；
        // 只有明确可恢复的异常才阻止 WPF 默认故障路径，未知异常不再被无条件吞掉。
        DispatcherUnhandledException += (_, args) =>
        {
            if (IsRecoverableDispatcherException(args.Exception))
            {
                Serilog.Log.Warning(args.Exception, "UI 线程可恢复异常（已记录并继续运行）");
                args.Handled = true;
            }
            else
            {
                Serilog.Log.Error(args.Exception, "UI 线程未处理异常（交由 WPF 默认故障路径）");
                args.Handled = false;
            }
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
            // 未 Load 时 AppSettings 仍使用 CSV 默认语言，Apply 永远应用默认中文。
            var appSettings = _host.Services.GetRequiredService<AppSettings>();
            appSettings.Load();
            Services.Localization.Apply(appSettings.EffectiveLanguageCode);
            var localizationOverride = LocalizationOverrideLoader.Load(
                appSettings.GetFilePath(LocalizationOverrideLoader.FileName),
                MainAPP.Resources.Strings.IsKnownKey,
                MainAPP.Resources.Strings.GetEmbeddedValue);
            if (!localizationOverride.IsValid)
            {
                Log($"本地化覆盖文件无效，已回退内置资源：{string.Join("；", localizationOverride.Errors)}");
            }
            else if (localizationOverride.AppliedCount > 0)
            {
                Log($"已加载本地化覆盖：{localizationOverride.AppliedCount} 项");
            }

            // Core 共享提示也读取同一份启动覆盖表，保证 MainAPP 与 Collector 的错误文案一致。
            Kanban.Collector.Core.Localization.ConnectionStatusMessages.ApplyLanguage(
                appSettings.EffectiveLanguageCode);
            Kanban.Collector.Core.Localization.ValidationMessages.ApplyLanguage(
                appSettings.EffectiveLanguageCode);
            // 配方校验/下发消息（与配置校验消息同机制）
            Kanban.Collector.Core.Localization.RecipeValidationMessages.ApplyLanguage(
                appSettings.EffectiveLanguageCode);
            Log($"界面语言已应用：{appSettings.EffectiveLanguageCode}");

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

            await Dispatcher.InvokeAsync(() =>
            {
                _host!.Services.GetRequiredService<IFirstRunGuideService>().TryShowAfterStartup(mainWindow);
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

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

    private static bool IsRecoverableDispatcherException(Exception exception)
        => exception is OperationCanceledException or FormatException;

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
                    // 审查修复 2026-09-06（P0-1）：关窗超时意味着 Store 排空可能被截断、
                    // pending 数据丢失，生产环境必须可追溯。此前只有 Debug.WriteLine，
                    // Release 下完全静默，事后无法判断"数据丢了还是没丢"。
                    // 此处**不**调用 Log.CloseAndFlush：清理任务仍在后台运行并向 Serilog 写入，
                    // 提前释放 logger 会让它在排空过程中抛 ObjectDisposedException，反而毁掉
                    // 最后一段诊断信息；超时场景下进程退出由 OS 兜底回收资源。
                    try
                    {
                        Serilog.Log.Error("关窗清理超过 {Seconds:0}s 未完成，未排空的数据可能丢失",
                            ExitTimeout.TotalSeconds);
                    }
                    catch (Exception logEx)
                    {
                        Debug.WriteLine($"[OnExit] Serilog 记录失败: {logEx.Message}");
                    }
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

    /// <summary>每个关窗步骤的默认超时上限（实际取 min(上限, 剩余预算)）。</summary>
    private static readonly TimeSpan StepCoordinatorTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StepAcquisitionTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan StepDailyReportTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StepConfigSaveTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan StepSettingsSaveTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StepHostStopTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StepHostDisposeTimeout = TimeSpan.FromSeconds(15);
    /// <summary>
    /// 收尾排空预留：前序步骤（协调器/采集/配置落盘…）共用的预算为此值之外的部分，
    /// 保证 <see cref="StepHostDisposeTimeout"/> 至少能拿到这个时间片，不会因前面慢而被"饿死"。
    /// </summary>
    private static readonly TimeSpan ShutdownDisposeReserve = TimeSpan.FromSeconds(8);

    private async Task<List<string>> ShutdownCoreAsync()
    {
        List<string> errors = [];
        // 审查修复 2026-09-06（P0-1）：ExitTimeout 是硬上限，而真正的数据落盘发生在序列
        // 最后一步——Host 释放时各 Store 执行停机排空（Channel → DB，失败转 recovery.jsonl）。
        // 此前 SaveAllAsync / AppSettings.Save / _host.StopAsync 均**无超时**，任一步卡在
        // SQLite 锁等待或 Remote Hub 往返就会挤爆 30s 预算，导致：排空被截断、pending 数据
        // 全丢（且不会转 recovery，因为转存发生在 flush 失败分支而 flush 根本没跑）、
        // 单实例 Mutex 不释放（下次启动走 AbandonedMutexException 接管）、日志不 Flush，
        // 而生产环境零可见性（超时只有 Debug.WriteLine）。
        // 现改为"剩余预算分配"：前序步骤每步超时 = min(步上限, 剩余预算-reserve)，
        // reserve 不可侵占，确保收尾排空始终有配额（详见 ShutdownBudget）。
        var budget = new ShutdownBudget(
            // 收尾排空在预算耗尽后仍会追加 2s 保底窗口，故最坏总耗时 = total + 2s；
            // 取 ExitTimeout-3s 保证它落在 OnExit 的 Wait 之内，否则 errors 取不回来、用户看不到提示。
            ExitTimeout - TimeSpan.FromSeconds(3),
            ShutdownDisposeReserve);                 // 给收尾排空预留的不可侵占配额
        try
        {
            if (!_hostStarted)
                return errors;

            await RunStep(errors, "停止启动协调器", budget, StepCoordinatorTimeout,
                _ => _host.Services.GetRequiredService<Services.ApplicationStartupCoordinator>().DisposeAsync().AsTask(),
                ex => $"停止启动协调器失败: {ex.Message}").ConfigureAwait(false);

            var isRemote = _host.Services.GetRequiredService<IRuntimeMode>().IsRemote;
            if (!isRemote)
            {
                await RunStep(errors, "停止 PLC 采集", budget, StepAcquisitionTimeout,
                    _ => _host.Services.GetRequiredService<PlcDataAcquisitionService>().StopAsync(),
                    ex => string.Format(Strings.F068, ex.Message)).ConfigureAwait(false);

                await RunStep(errors, "停止日报服务", budget, StepDailyReportTimeout,
                    _ => _host.Services.GetRequiredService<Services.ProductionDailyReportService>().StopAsync(),
                    ex => string.Format(Strings.F191, ex.Message)).ConfigureAwait(false);
            }

            // Remote 模式必须在 Host 释放 SignalR 之前把设备配置推送到 Collector。
            await RunStep(errors, "保存设备配置", budget, StepConfigSaveTimeout,
                _ => _host.Services.GetRequiredService<DeviceRepository>().SaveAllAsync(),
                ex => string.Format(Strings.F210, ex.Message)).ConfigureAwait(false);

            await RunStep(errors, "保存应用设置", budget, StepSettingsSaveTimeout,
                _ => Task.Run(() => _host.Services.GetRequiredService<AppSettings>().Save()),
                ex => string.Format(Strings.F119, ex.Message)).ConfigureAwait(false);

            await RunStep(errors, "停止 Host", budget, StepHostStopTimeout,
                _ => _host.StopAsync(),
                ex => $"停止 Host 失败: {ex.Message}").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            errors.Add($"退出清理失败: {ex.Message}");
        }
        finally
        {
            // Host 释放是资源回收与数据落盘的最后关口：即使预算耗尽也保留 2s 最短窗口，
            // 让各 Store 至少有机会跑一次停机排空，而不是"什么都不做直接退出"。
            // 只有收尾步骤能动用预留配额，因此正常路径下这里至少能拿到 ShutdownDisposeReserve。
            var disposeSlice = budget.FinalSlice(StepHostDisposeTimeout);
            if (disposeSlice <= TimeSpan.Zero)
            {
                errors.Add("关窗预算已耗尽：释放 Host 仅保留 2s 尝试窗口，未排空数据可能丢失");
                disposeSlice = TimeSpan.FromSeconds(2);
            }

            try
            {
                var disposeTask = _host is IAsyncDisposable asyncHost
                    ? asyncHost.DisposeAsync().AsTask()
                    : Task.Run(() => _host.Dispose());
                await disposeTask.WaitAsync(disposeSlice).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                errors.Add($"释放 Host 超时（>{disposeSlice.TotalSeconds:0}s）：Store 排空未完成，未落盘数据可能丢失");
            }
            catch (Exception ex)
            {
                errors.Add($"释放 Host 失败: {ex.Message}");
            }
            _hostStarted = false;
        }

        return errors;
    }

    /// <summary>
    /// 按剩余预算执行单个关窗步骤：超时即放弃本步并记入错误列表，绝不让单步拖垮整个关窗预算。
    /// </summary>
    private static async Task RunStep(
        List<string> errors,
        string label,
        ShutdownBudget budget,
        TimeSpan cap,
        Func<CancellationToken, Task> action,
        Func<Exception, string> formatError)
    {
        var slice = budget.Slice(cap);
        if (slice <= TimeSpan.Zero)
        {
            errors.Add($"{label}未执行：关窗预算已耗尽");
            return;
        }

        try
        {
            await action(CancellationToken.None).WaitAsync(slice).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            errors.Add($"{label}超时（>{slice.TotalSeconds:0}s），已跳过；未落盘数据可能丢失");
        }
        catch (Exception ex)
        {
            errors.Add(formatError(ex));
        }
    }

    /// <summary>
    /// 关窗预算分配器：总预算固定，其中 <c>reserve</c> 专门留给收尾排空，前序步骤不可侵占。
    /// 每步取 min(步上限, 剩余可用)，目的：无论前序步骤多慢，收尾的数据排空步骤始终有时间片。
    /// </summary>
    /// <param name="total">关窗总预算。</param>
    /// <param name="reserve">为收尾步骤预留的配额，仅 <see cref="FinalSlice"/> 可动用。</param>
    private sealed class ShutdownBudget(TimeSpan total, TimeSpan reserve)
    {
        private readonly Stopwatch _watch = Stopwatch.StartNew();

        /// <summary>前序步骤可用时间片：已扣除 <c>reserve</c>。</summary>
        public TimeSpan Slice(TimeSpan cap) => Trim(total - reserve - _watch.Elapsed, cap);

        /// <summary>收尾步骤可用时间片：可动用此前未消耗的 reserve。</summary>
        public TimeSpan FinalSlice(TimeSpan cap) => Trim(total - _watch.Elapsed, cap);

        private static TimeSpan Trim(TimeSpan remaining, TimeSpan cap)
        {
            if (remaining <= TimeSpan.Zero) return TimeSpan.Zero;
            return remaining < cap ? remaining : cap;
        }
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
