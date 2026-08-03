using System.Diagnostics;
using Kanban.Client;
using Kanban.Core.Services;
using Kanban.Core.Models;
using Kanban.Core.Data;
using Kanban.Core.Entities;
using Kanban.Core.Mapping;
using System.Windows;
using MainAPP.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace MainAPP.Services;

/// <summary>
/// 承接应用启动阶段的业务初始化，App.xaml.cs 只负责 WPF 生命周期和授权流程。
/// </summary>
public sealed class ApplicationStartupCoordinator(
    IServiceProvider services,
    ApplicationRuntime runtime)
{
    public async Task<Window> PrepareAsync()
    {
        try
        {
            runtime.SetState(ApplicationRuntimeState.LoadingConfiguration, "正在加载配置");
            var settings = services.GetRequiredService<AppSettings>();
            settings.Load();
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
                HandyControl.Controls.MessageBox.Show(
                    "配置文件存在以下问题，部分功能可能不可用：\n\n" + string.Join("\n", configErrors) +
                    "\n\n建议进入「设置」页修改后保存。",
                    "配置验证警告", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            var deviceRepository = services.GetRequiredService<DeviceRepository>();
            deviceRepository.LoadAll();
            Log.Information("DeviceRepository.LoadAll 完成");

            // 文件损坏时通知用户（原文件已备份为 devices.json.corrupt）
            // 启动早期（MainWindow 尚未 Show，Growl 容器不存在），使用 HC MessageBox 获得深色主题样式
            if (!string.IsNullOrEmpty(deviceRepository.LoadErrorMessage))
            {
                HandyControl.Controls.MessageBox.Show(
                    deviceRepository.LoadErrorMessage, "配置文件损坏",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                Log.Warning("DeviceRepository.LoadAll 警告: {Message}", deviceRepository.LoadErrorMessage);
            }

            // CollectionView 在 ViewModel 构造时订阅空集合，LoadAll 后需手动刷新
            services.GetRequiredService<DeviceManagerViewModel>().RefreshDeviceList();
            Log.Information("DeviceList 刷新完成");

            runtime.SetState(ApplicationRuntimeState.MigratingDatabase, "正在升级历史数据库");
            var databaseProvider = services.GetRequiredService<DatabaseProvider>();
            // 数据库表结构初始化：必须在 MainWindow.Show() 之前完成，
            // 否则 ViewModel 在 Loaded/Dispatcher.BeginInvoke 中立即查询会命中空库，
            // 抛出 "no such table: AlarmEvents/StatusTransitions/ProductionLogs"。
            // 不使用 EnsureDeleted（会清空历史数据），启动时通过 EF Core Migrate 增量更新 schema。
            // Remote 模式：Collector 是唯一写者，本地库不创建（查询全走 SignalR 代理，
            // 工单列表由 Meta 推送填充）——完整落实"单写者"架构，避免空库冗余。
            if (!services.GetRequiredService<IRuntimeMode>().IsRemote)
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
            runtime.SetState(ApplicationRuntimeState.Ready, "数据库已就绪");

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

    public async Task StartRuntimeAsync()
    {
        runtime.SetState(ApplicationRuntimeState.StartingAcquisition, "正在启动数据采集");
        try
        {
            var settings = services.GetRequiredService<AppSettings>();

            // Remote 模式：不启动本地 PLC 采集，改为连接 Collector 采集服务进程
            if (services.GetRequiredService<IRuntimeMode>().IsRemote)
            {
                await StartRemoteDataLinkAsync();
                runtime.IsAcquisitionRunning = true;
                runtime.SetState(ApplicationRuntimeState.Running, "运行中（远程采集）");
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
            runtime.SetState(ApplicationRuntimeState.Running, "运行中");
        }
        catch (Exception exception)
        {
            runtime.SetFailure(exception);
            runtime.SetState(ApplicationRuntimeState.Degraded, "部分功能不可用");
            throw;
        }
    }

    /// <summary>
    /// Remote 模式：连接 Kanban.Collector，订阅快照/事件并灌回本地内存状态。
    /// 同时挂接设备/工单写操作的远程持久化钩子（Collector 作为唯一写者落盘/落库）。
    /// </summary>
    private async Task StartRemoteDataLinkAsync()
    {
        var client = services.GetRequiredService<KanbanDataClient>();
        var sink = services.GetRequiredService<RemoteRuntimeSink>();

        client.ConnectionStateChanged += (_, connected) =>
        {
            // 桥接到 PlcConnectionManager 状态，复用全局连接状态横幅（MainWindowViewModel 绑定）
            if (connected)
                services.GetRequiredService<PlcConnectionManager>().SyncRemoteConnected("采集服务已连接");
            else
                services.GetRequiredService<PlcConnectionManager>().MarkDisconnected(DisconnectionReason.ReadFailure);
        };
        // 自动重连阶段：横幅显示"正在连接采集服务（第 N 次）"，命中 IsPlcConnecting 黄色分支
        client.Reconnecting += (_, _) =>
        {
            var attempt = services.GetRequiredService<KanbanDataClient>().ConsecutiveFailures;
            services.GetRequiredService<PlcConnectionManager>().SyncRemoteReconnecting(attempt);
        };

        await client.ConnectAsync();
        // 回调注册必须在连接建立之后（KanbanDataClient.On* 依赖 _connection）
        sink.Start();

        // 版本握手：升级兼容性观测——Collector 版本与本地记录不一致时打警告（方法签名变化前可提前发现）
        try
        {
            var serverVersion = await client.GetServerVersionAsync();
            Log.Information("已连接采集服务 {Url}，服务端版本 {ServerVersion}", client.HubUrl, serverVersion);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "获取采集服务版本失败（不影响连接）");
        }

        // 设备/工单写操作 → Collector（唯一写者），MainAPP 不再直接写 devices.json / work_orders.db。
        // 钩子为异步签名（AsyncRelayCommand 调用，避免 UI 线程阻塞等待网络）。
        var deviceRepo = services.GetRequiredService<DeviceRepository>();
        deviceRepo.RemotePersistenceHook = devices =>
            client.SaveDevicesAsync(DeviceMapper.ToDtos(devices));
        var workOrderRepo = services.GetRequiredService<WorkOrderRepository>();
        workOrderRepo.RemoteUpsertHook = async wo =>
            WorkOrderMapper.ToEntity(await client.UpsertWorkOrderAsync(WorkOrderMapper.ToDto(wo)));
        workOrderRepo.RemoteDeleteHook = async id =>
        {
            await client.DeleteWorkOrderAsync(id);
            return true;
        };

        // 屏端零配置：设备列表从 Collector 拉取（屏端无 devices.json 也能启动）。
        // 失败（服务未就绪等）时保留本地已加载配置，不影响启动。
        try
        {
            var remoteDevices = await client.GetDevicesAsync();
            if (remoteDevices.Count > 0)
            {
                var entities = DeviceMapper.ToEntities(remoteDevices);
                deviceRepo.ReplaceAll(entities);
                Log.Information("Remote 设备配置已从采集服务加载：{Count} 台", entities.Count);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "远程设备配置加载失败，使用本地配置");
        }

        Log.Information("Remote 模式数据链路已建立：{Url}", services.GetRequiredService<AppSettings>().CollectorHubUrl);
    }
}
