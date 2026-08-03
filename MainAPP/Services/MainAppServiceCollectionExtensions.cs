using Kanban.Client;
using Kanban.Core.Data;
using Kanban.Core.Services;
using Kanban.Core.Models;
using Kanban.Core.Entities;
using Kanban.Core.DependencyInjection;
using Kanban.Core.Mapping;
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

        // ──────────── Remote 模式数据链路（展示端瘦身） ────────────
        // KanbanDataClient 来自共享库 Kanban.Client（构造解耦：只收 HubUrl 字符串，不依赖 AppSettings/WPF）
        services.AddSingleton<KanbanDataClient>(sp => new KanbanDataClient(
            sp.GetRequiredService<AppSettings>().CollectorHubUrl,
            sp.GetRequiredService<ILogger<KanbanDataClient>>()));
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
