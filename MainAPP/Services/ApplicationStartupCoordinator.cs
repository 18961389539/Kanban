using System.Diagnostics;
using Kanban.Collector.Core.Services;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Data;
using System.Windows;
using MainAPP.ViewModels;
using MainAPP.Resources;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace MainAPP.Services;

/// <summary>
/// 承接应用启动阶段的业务初始化，App.xaml.cs 只负责 WPF 生命周期和授权流程。
/// </summary>
public sealed class ApplicationStartupCoordinator(
    IServiceProvider services,
    ApplicationRuntime runtime,
    IDialogService dialog) : IAsyncDisposable
{
    private readonly CancellationTokenSource _shutdownCts = new();
    private int _disposeStarted;

    public async Task<Window> PrepareAsync(CancellationToken cancellationToken = default)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts.Token);
        cancellationToken = linkedCts.Token;
        try
        {
            runtime.SetState(ApplicationRuntimeState.LoadingConfiguration, Strings.M123);
            var settings = services.GetRequiredService<AppSettings>();
            settings.Load();
            services.GetService<IPlcRuntimeSessionManager>()?.RefreshFromSettings();
            Log.Information("AppSettings.Load 完成");

            // 大屏远距可读性：按保存的字号缩放（默认 100%）应用全局语义字号资源。
            // 必须在 MainWindow.Show 之前应用，避免首帧字号跳变。
            FontSizeManager.ApplyScale(settings.UiScale);
            Log.Information("全局字号缩放应用完成 (UiScale={UiScale})", settings.UiScale);

            // 加载产量基线（原寄居 AppSettings，现归位于 ProductionBaselineStore）
            services.GetRequiredService<ProductionBaselineStore>().Load();
            Log.Information("ProductionBaselineStore.Load 完成");

            // 启动时验证配置完整性：在 PLC 连接/采集启动前拦截非法配置，
            // 避免 0 轮询间隔触发 CPU 满载、非法 IP 触发长连接超时、空班次触发误清零等问题。
            // 验证失败时仅弹警告并继续启动（用户仍可进入设置页修改），不阻断启动。
            var configErrors = settings.Validate();
            if (configErrors.Count > 0)
            {
                Log.Warning("配置验证发现 {Count} 个错误", configErrors.Count);
                dialog.Show(
                    Strings.M131 + "\n\n" + string.Join("\n", configErrors) +
                    "\n\n" + Strings.M132,
                    Strings.M121, MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            var isRemote = services.GetRequiredService<IRuntimeMode>().IsRemote;
            if (isRemote)
            {
                try
                {
                    // Remote 首帧也必须使用 Collector 的权威语言/覆盖；否则 XAML 的 x:Static
                    // 文案已在 MainWindow.Show 前按本机降级语言固化，显示后再切换会出现混合语言。
                    await services.GetRequiredService<RemoteDataLinkBootstrapper>()
                        .SynchronizeRemoteLocalizationAsync(cancellationToken);
                }
                catch (Exception exception)
                {
                    Log.Warning(exception, "启动前同步 Collector 本地化失败，继续使用本机降级语言");
                }
            }
            var deviceRepository = services.GetRequiredService<DeviceRepository>();
            if (isRemote)
            {
                // Remote 模式不读取本地 devices.json：Collector 是唯一权威写者，
                // 本地文件只能是旧缓存，读取后可能在连接失败时被误保存回 Collector。
                deviceRepository.ReplaceAll([]);
                Log.Information("Remote 模式：跳过本地 devices.json 加载，等待 Collector 权威配置");
            }
            else
            {
                deviceRepository.LoadAll();
                Log.Information("DeviceRepository.LoadAll 完成");
            }

            // 文件损坏时通知用户（原文件已备份为 devices.json.corrupt）
            // 启动早期（MainWindow 尚未 Show，Growl 容器不存在），使用 HC MessageBox 获得深色主题样式
            if (!string.IsNullOrEmpty(deviceRepository.LoadErrorMessage))
            {
                dialog.Show(
                    deviceRepository.LoadErrorMessage, Strings.M122,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                Log.Warning("DeviceRepository.LoadAll 警告: {Message}", deviceRepository.LoadErrorMessage);
            }

            // CollectionView 在 ViewModel 构造时订阅空集合，LoadAll 后需手动刷新
            services.GetRequiredService<DeviceManagerViewModel>().DeviceList.RefreshDeviceList();
            Log.Information("DeviceList 刷新完成");

            // 配方库启动加载（与设备/工单同模式；Remote 模式随后 StartDataLinkAsync 拉取覆盖）
            var recipeStore = services.GetRequiredService<IRecipeStore>();
            if (isRemote)
            {
                recipeStore.ReplaceAll([]);
                Log.Information("Remote 模式：跳过本地 recipes.json 加载，等待 Collector 权威配方");
            }
            else
            {
                recipeStore.LoadAll();
                Log.Information("RecipeStore.LoadAll 完成");
            }

            runtime.SetState(ApplicationRuntimeState.MigratingDatabase, Strings.M127);
            var databaseProvider = services.GetRequiredService<DatabaseProvider>();
            // 数据库表结构初始化：必须在 MainWindow.Show() 之前完成，
            // 否则 ViewModel 在 Loaded/Dispatcher.BeginInvoke 中立即查询会命中空库，
            // 抛出 "no such table: AlarmEvents/StatusTransitions/ProductionLogs"。
            // 不使用 EnsureDeleted（会清空历史数据），启动时通过 EF Core Migrate 增量更新 schema。
            // Remote 模式：Collector 是唯一写者，本地库不创建（查询全走 SignalR 代理，
            // 工单列表由 Meta 推送填充）——完整落实"单写者"架构，避免空库冗余。
            if (!isRemote)
            {
                databaseProvider.EnsureCreatedAll();
                databaseProvider.EnsureWalModeEnabled();
                Log.Information("四库 EF Core Migrate + WAL 完成 (Show 前)");

                // 工单仓储启动期加载：在 EnsureCreatedAll 之后（schema 已就绪）
                services.GetRequiredService<WorkOrderRepository>().LoadAll();
                Log.Information("WorkOrderRepository.LoadAll 完成");
            }
            else
            {
                Log.Information("Remote 模式：跳过本地库创建与工单加载（Collector 为唯一写者）");
            }

            runtime.IsDatabaseReady = true;
            runtime.SetState(ApplicationRuntimeState.Ready, Strings.M126);

            // 获取 MainWindow（DI 会传递构造 MainWindowViewModel → 各子 ViewModel →
            // PlcConnectionManager/PlcDataAcquisitionService/HistoryService，含 HslCommunication 与 EF Core 首次 JIT）
            var mainWindow = services.GetRequiredService<MainWindow>();
            Log.Information("MainWindow 实例获取完成 (含 ViewModel 树 + 重型服务 JIT)");
            return mainWindow;
        }
        catch (Exception exception)
        {
            runtime.SetFailure(exception);
            throw;
        }
    }

    public async Task StartRuntimeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        runtime.SetState(ApplicationRuntimeState.StartingAcquisition, Strings.M124);
        try
        {
            var settings = services.GetRequiredService<AppSettings>();

            // Remote 模式：不启动本地 PLC 采集，改为连接 Collector 采集服务进程
            if (services.GetRequiredService<IRuntimeMode>().IsRemote)
            {
                await services.GetRequiredService<RemoteDataLinkBootstrapper>().StartAsync(cancellationToken);
                // Remote 模式同样支持按设定时刻自动生成上一自然日日报（历史查询经 SignalR 路由到 Collector）
                services.GetRequiredService<ProductionDailyReportService>().Start();
                runtime.IsAcquisitionRunning = true;
                runtime.SetState(ApplicationRuntimeState.Running, Strings.M125);
                return;
            }

            await Task.Run(() =>
            {
                var stopwatch = Stopwatch.StartNew();
                services.GetRequiredService<AlarmHistoryStore>().CleanupOldAlarmEvents();
                services.GetRequiredService<ProductionHistoryStore>().CleanupOldProductionLogs();
                services.GetRequiredService<StatusTransitionHistoryStore>().CleanupOldStatusTransitions();
                services.GetRequiredService<DefectHistoryStore>().CleanupOldSnapshots();
                services.GetRequiredService<WorkOrderRepository>().CleanupOldWorkOrders();
                services.GetRequiredService<PlcDataAcquisitionService>().Start();
                services.GetRequiredService<ProductionDailyReportService>().Start();
                Log.Information("历史清理和 PLC 采集启动完成，耗时 {ElapsedMs}ms", stopwatch.ElapsedMilliseconds);
            });
            runtime.IsAcquisitionRunning = true;
            runtime.SetState(ApplicationRuntimeState.Running, Strings.M129);
        }
        catch (Exception exception)
        {
            runtime.SetFailure(exception);
            runtime.SetState(ApplicationRuntimeState.Degraded, Strings.M128);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            return;

        _shutdownCts.Cancel();
        await services.GetRequiredService<RemoteDataLinkBootstrapper>().DisposeAsync();
        _shutdownCts.Dispose();
    }
}
