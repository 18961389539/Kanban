using System.Diagnostics;
using Kanban.Core.Services;
using Kanban.Core.Models;
using Kanban.Core.Data;
using Kanban.Core.Entities;
using System.Windows;
using Kanban.Core.Data;
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
            FontSizeManager.ApplyScale(settings.UiScale);

            services.GetRequiredService<ProductionBaselineStore>().Load();
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
            if (!string.IsNullOrEmpty(deviceRepository.LoadErrorMessage))
            {
                HandyControl.Controls.MessageBox.Show(
                    deviceRepository.LoadErrorMessage, "配置文件损坏",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            services.GetRequiredService<DeviceManagerViewModel>().RefreshDeviceList();

            runtime.SetState(ApplicationRuntimeState.MigratingDatabase, "正在升级历史数据库");
            var databaseProvider = services.GetRequiredService<DatabaseProvider>();
            databaseProvider.EnsureCreatedAll();
            databaseProvider.EnsureWalModeEnabled();
            services.GetRequiredService<WorkOrderRepository>().LoadAll();
            runtime.IsDatabaseReady = true;
            runtime.SetState(ApplicationRuntimeState.Ready, "数据库已就绪");

            return services.GetRequiredService<MainWindow>();
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
            if (settings.DataMode == KanbanDataMode.Remote)
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

        Log.Information("Remote 模式数据链路已建立：{Url}", services.GetRequiredService<AppSettings>().CollectorHubUrl);
    }
}
