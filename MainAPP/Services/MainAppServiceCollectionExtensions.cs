using Kanban.Client;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Services;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.DependencyInjection;
using Kanban.Collector.Core.Mapping;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MainAPP.Services;

/// <summary>
/// 应用基础设施注册。生产宿主和测试宿主共享此注册入口，环境差异只覆盖设置和对话框实现。
/// </summary>
public static class MainAppServiceCollectionExtensions
{
    public static IServiceCollection AddMainAppCoreServices(
        this IServiceCollection services,
        AppSettings? appSettings = null)
    {
        // ──────────── 采集/存储核心服务（单一共享入口，与 Kanban.Collector 复用，避免漏注册） ────────────
        services.AddKanbanDataServices(appSettings);

        // ──────────── WPF 绑定同步（UI 进程专属，Core 去 WPF 化收口） ────────────
        // Core 仓库集合（Devices/Runtimes/WorkOrders/Recipes）由后台采集线程写入、UI 线程绑定；
        // EnableCollectionSynchronization 是 WPF API，只在 MainAPP 注册（无头 Collector 走
        // AddKanbanDataServices 不注册）。随宿主启动执行，早于任何视图实例化/绑定。
        services.AddHostedService<WpfCollectionBindingRegistrar>();

        // ──────────── 报警声音通知（UI 进程专属，Core 去 WPF 化收口） ────────────
        // SystemSounds 位于 WPF 的 WindowsBase，实现放 MainAPP；Collector 不注册（静默）。
        services.AddSingleton<IAlarmNotificationChannel, SystemAlarmNotificationChannel>();

        // ──────────── 用户与权限（RBAC）────────────
        services.AddSingleton<UserStore>();
        services.AddSingleton<UserSession>();
        services.AddSingleton<IAuthorizationService, AuthorizationService>();

        services.AddSingleton<LicenseManager.Services.LicenseStore>();
        services.AddSingleton<LicenseManager.Services.TrialRegistryBackup>();
        services.AddSingleton<LicenseManager.Services.TrialTracker>();
        services.AddSingleton<LicenseManager.Services.ActivationAttemptTracker>();
        services.AddSingleton<LicenseManager.Services.LicenseGate>();
        services.AddTransient<LicenseManager.ViewModels.ActivationViewModel>();

        // ──────────── 工单 / 复盘 / 报表（MainAPP 侧业务服务） ────────────
        services.AddSingleton<IWorkOrderService, WorkOrderService>();
        services.AddSingleton<IProductionReviewDataService, ProductionReviewDataService>();
        services.AddSingleton<IProductionReviewMetricsService, ProductionReviewMetricsService>();
        services.AddSingleton<IProductionReviewAlarmAnalysisService, ProductionReviewAlarmAnalysisService>();
        services.AddSingleton<IProductionReviewStatusTimelineService, ProductionReviewStatusTimelineService>();
        services.AddSingleton<IProductionReviewHealthScoreService, ProductionReviewHealthScoreService>();
        services.AddSingleton<IProductionReviewAnalysisService>(sp => new ProductionReviewAnalysisService(
            sp.GetRequiredService<IProductionReviewDataService>(),
            sp.GetRequiredService<DefectHistoryStore>(),
            sp.GetRequiredService<WorkOrderRepository>(),
            sp.GetRequiredService<IProductionReviewAlarmAnalysisService>(),
            sp.GetRequiredService<IProductionReviewStatusTimelineService>(),
            sp.GetRequiredService<IProductionReviewHealthScoreService>()));
        services.AddSingleton<IProductionReviewCsvExportService, ProductionReviewCsvExportService>();
        services.AddSingleton<IProductionReviewChartService, ProductionReviewChartService>();
        services.AddSingleton<IProductionReviewPdfService, ProductionReviewPdfService>();
        services.AddSingleton<ProductionDailyReportService>();
        services.AddSingleton<GpuUsageMonitor>();
        services.AddSingleton<SystemResourceMonitor>();
        services.AddSingleton<IDeviceSelectionService, DeviceSelectionService>();
        services.AddSingleton<DeviceConfigIOService>();
        services.AddSingleton<DevicePlcCommandHandler>();
        services.AddSingleton<AlarmCsvIOService>();
        services.AddSingleton<DefectCsvIOService>();
        services.AddSingleton<CounterAlarmCsvIOService>();
        services.AddSingleton<DataSourceCsvIOService>();
        services.AddSingleton<RecipeJsonIOService>();

        // ──────────── Remote 模式数据链路（展示端瘦身） ────────────
        // KanbanDataClient 来自共享库 Kanban.Client（构造解耦：只收 HubUrl 字符串，不依赖 AppSettings/WPF）
        services.AddSingleton<KanbanDataClient>(sp => new KanbanDataClient(
            sp.GetRequiredService<AppSettings>().CollectorHubUrl,
            sp.GetRequiredService<ILogger<KanbanDataClient>>()));
        services.AddSingleton<KanbanAdminClient>(sp => new KanbanAdminClient(
            sp.GetRequiredService<AppSettings>().CollectorHubUrl,
            sp.GetRequiredService<ILogger<KanbanDataClient>>()));
        services.AddSingleton<IKanbanAdminClient>(sp => sp.GetRequiredService<KanbanAdminClient>());
        services.AddSingleton<IRemoteDeviceConfigurationStore, RemoteDeviceConfigurationStore>();
        services.AddSingleton<IRemoteRecipeStore, RemoteRecipeStore>();
        services.AddSingleton<IRemoteWorkOrderStore, RemoteWorkOrderStore>();
        services.AddSingleton<RemoteAuditService>();
        services.AddSingleton<IAuditService>(sp =>
            sp.GetRequiredService<IRuntimeMode>().IsRemote
                ? sp.GetRequiredService<RemoteAuditService>()
                : sp.GetRequiredService<AuditService>());
        services.AddSingleton<RemoteRuntimeSink>();
        // 历史查询路由代理：Local 委托 HistoryService（SQLite），Remote 走 SignalR。
        // ⚠️ 刻意行为：以下 6 个接口**无条件**重定向到 RemoteHistoryQueryService（后注册覆盖
        // AddKanbanDataServices 中的本地实现，依赖 MS DI"后注册胜出"语义）。
        // 代理内部按 IRuntimeMode.IsRemote 分派：Local → 本地 HistoryService；Remote → SignalR 查询。
        // 因此本地实现（HistoryService/ProductionHistoryStore/AlarmHistoryStore 等）仍须保留注册，
        // 作为代理的依赖（Local 分派目标）。新增历史接口时须在此同步重定向。
        services.AddSingleton<RemoteHistoryQueryService>();
        services.AddSingleton<IHistoryService>(sp => sp.GetRequiredService<RemoteHistoryQueryService>());
        services.AddSingleton<IProductionHistoryReader>(sp => sp.GetRequiredService<RemoteHistoryQueryService>());
        services.AddSingleton<IAlarmHistoryService>(sp => sp.GetRequiredService<RemoteHistoryQueryService>());
        services.AddSingleton<IStatusTransitionHistoryService>(sp => sp.GetRequiredService<RemoteHistoryQueryService>());
        services.AddSingleton<IWorkOrderProductionBatchQuery>(sp => sp.GetRequiredService<RemoteHistoryQueryService>());
        services.AddSingleton<IDefectHistoryReader>(sp => sp.GetRequiredService<RemoteHistoryQueryService>());

        return services;
    }
}
