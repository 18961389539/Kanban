using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using LicenseManager.Models;
using LicenseManager.Services;
using LicenseManager.ViewModels;
using LicenseManager.Views;
using Kanban.Client;
using Kanban.Core.Data;
using Kanban.Core.Services;
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
        // 禁止多开：非首个实例直接提示并退出，不启动 Host/采集/PLC，避免所有副作用。
        if (!_isFirstInstance)
        {
            Log("检测到已有实例在运行，禁止多开，准备退出");
            // 启动早期 Growl 容器未就绪，用 HC MessageBox（理由详见下方配置文件损坏处）
            HandyControl.Controls.MessageBox.Show(
                "程序已在运行，不能重复启动。",
                "提示",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        Log("OnStartup 开始");

        // OnStartup 是 async void，未捕获异常会直接终止进程且无错误提示。
        // 用 try/catch 包裹整个启动流程（Host.StartAsync / Load / EnsureCreated / MainWindow.Show 等），
        // 任一步骤抛异常时记录日志、提示用户并优雅退出，避免无提示崩溃。
        try
        {
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
                    LicenseStatus.TrialExpired => $"试用期已过期（{TrialTracker.TrialDays} 天），请输入激活码继续使用。",
                    LicenseStatus.TrialManipulated => "检测到系统时间异常，试用期已失效，请输入激活码继续使用。",
                    LicenseStatus.Expired => "授权已过期，请输入新的激活码。",
                    LicenseStatus.MachineMismatch => "授权与当前机器不匹配，请重新激活。",
                    _ => "请输入激活码以继续使用。",
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

            // 刷新 MainWindow 侧边栏授权状态显示（LicenseGate 已在启动早期检查完毕）
            var mainVm = _host.Services.GetRequiredService<ViewModels.MainWindowViewModel>();
            mainVm.RefreshLicenseStatus();

            // 启动时刚通过激活对话框激活成功 → 弹 Growl 反馈（P1-3：激活成功后主界面反馈授权信息）
            if (justActivated)
            {
                var expireText = licenseGate.CurrentLicense?.IsPermanent == false
                    ? $"· 到期 {licenseGate.CurrentLicense.ExpireDate:yyyy-MM-dd}"
                    : "· 永久授权";
                HandyControl.Controls.Growl.Success(
                    $"激活成功{expireText}。");
            }
            else if (licenseStatus == LicenseStatus.Trial && licenseGate.RemainingTrialDays.HasValue)
            {
                HandyControl.Controls.Growl.Info(
                    $"试用期内，剩余 {licenseGate.RemainingTrialDays} 天。请在设置页输入激活码完成授权。");
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
                // 此时 MainWindow 已 Show（line 143），Growl 容器已就绪，用非模态通知避免阻塞。
                Log($"后台初始化失败: {ex.Message}");
                HandyControl.Controls.Growl.Warning(
                    $"数据库/采集初始化失败，部分功能可能不可用：\n\n{ex.Message}");
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
                $"程序启动失败，即将退出：\n\n{ex.Message}",
                "启动失败",
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
            // 退出全程用 try/catch/finally 保护：任何步骤抛异常都不能阻断 base.OnExit 与互斥锁释放，
            // 否则 Host 托管资源泄漏、互斥锁残留导致下次启动误判为"已存在实例"。
            // Remote 模式：释放 Collector 连接，不执行本地采集停止（本地采集未启动）
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
                    errors.Add($"释放 Collector 连接失败：{ex.Message}");
                }
            }
            else
            {
                // 先停止采集循环，避免保存设备/历史数据时采集线程仍在并发修改 Runtime 状态、入队 HistoryService。
                // StopAsync 会写入离线状态转换记录，避免停机时段被算进上一状态导致重启后 OEE 历史虚高。
                try
                {
                    await _host.Services.GetRequiredService<PlcDataAcquisitionService>().StopAsync();
                }
                catch (Exception ex)
                {
                    errors.Add($"停止采集服务失败：{ex.Message}");
                }

                try
                {
                    await _host.Services.GetRequiredService<Services.ProductionDailyReportService>().StopAsync();
                }
                catch (Exception ex)
                {
                    errors.Add($"自动日报服务停止失败：{ex.Message}");
                }
            }

            try
            {
                // Remote 模式经 SignalR 推送 Collector 落盘（async 等待避免退出时丢数据）
                await _host.Services.GetRequiredService<DeviceRepository>().SaveAllAsync();
            }
            catch (Exception ex)
            {
                errors.Add($"设备数据保存失败：{ex.Message}");
            }
            try
            {
                _host.Services.GetRequiredService<AppSettings>().Save();
            }
            catch (Exception ex)
            {
                errors.Add($"应用设置保存失败：{ex.Message}");
            }

            // P0-3：HistoryService.Dispose 改用 DisposeAsync，避免在 UI 线程同步阻塞最多 3 秒
            try
            {
                await _host.Services.GetRequiredService<HistoryService>().DisposeAsync();
            }
            catch (Exception ex)
            {
                errors.Add($"历史数据写入失败：{ex.Message}");
            }

            if (errors.Count > 0)
            {
                // 退出阶段：用 HC MessageBox 模态阻塞，确保用户在应用关闭前看到保存失败信息
                // （Growl 是非模态通知，应用关闭时会被立即销毁，用户来不及看到）
                HandyControl.Controls.MessageBox.Show(
                    "退出时部分数据保存失败，可能丢失：\n\n" + string.Join("\n", errors),
                    "持久化失败",
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
            // 这里提前释放以确保 Dispose 阶段日志可被捕获，便于排查关闭阶段资源泄漏。
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
