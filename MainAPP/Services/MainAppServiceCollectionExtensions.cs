using MainAPP.Data;
using MainAPP.Mapping;
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
        services.AddAutoMapper(cfg => cfg.AddProfile<MappingProfile>());

        services.AddSingleton<LicenseManager.Services.LicenseStore>();
        services.AddSingleton<LicenseManager.Services.TrialRegistryBackup>();
        services.AddSingleton<LicenseManager.Services.TrialTracker>();
        services.AddSingleton<LicenseManager.Services.ActivationAttemptTracker>();
        services.AddSingleton<LicenseManager.Services.LicenseGate>();
        services.AddTransient<LicenseManager.ViewModels.ActivationViewModel>();

        if (appSettings == null)
            services.AddSingleton<AppSettings>();
        else
            services.AddSingleton(appSettings);

        services.AddSingleton<ProductionBaselineStore>();
        services.AddSingleton<DatabaseProvider>();
        services.AddSingleton<DeviceRepository>();
        services.AddSingleton<WorkOrderRepository>();
        services.AddSingleton<IWorkOrderService, WorkOrderService>();
        services.AddSingleton<DefectHistoryStore>();
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
        services.AddSingleton<IAlarmNotificationChannel, SystemAlarmNotificationChannel>();
        services.AddSingleton<ISharedPlcDriverFactory, HslSharedPlcDriverFactory>();
        services.AddSingleton<IPlcAddressCodecResolver, PlcAddressCodecResolver>();
        services.AddSingleton<IPlcRuntimeProfileProvider, PlcRuntimeProfileProvider>();
        services.AddSingleton<SharedPlcDriverRouter>();
        services.AddSingleton<IPlcDriver>(sp => sp.GetRequiredService<SharedPlcDriverRouter>());
        services.AddSingleton<PlcConnectionManager>();
        services.AddSingleton<IDeviceAdapter, PlcDeviceAdapter>();
        services.AddSingleton<IDeviceAdapterResolver, DeviceAdapterResolver>();
        services.AddSingleton<PlcDataAcquisitionService>(sp => new PlcDataAcquisitionService(
            sp.GetRequiredService<IPlcDriver>(),
            sp.GetRequiredService<PlcConnectionManager>(),
            sp.GetRequiredService<AppSettings>(),
            sp.GetRequiredService<IProductionHistoryWriter>(),
            sp.GetRequiredService<IAlarmHistoryService>(),
            sp.GetRequiredService<IStatusTransitionHistoryService>(),
            sp.GetRequiredService<DeviceRepository>(),
            sp.GetRequiredService<ProductionBaselineStore>(),
            sp.GetRequiredService<ILogger<PlcDataAcquisitionService>>(),
            sp.GetRequiredService<IDeviceAdapterResolver>(),
            sp.GetRequiredService<WorkOrderRepository>(),
            sp.GetRequiredService<IAlarmNotificationChannel>(),
            sp.GetRequiredService<DefectHistoryStore>()));
        services.AddSingleton<IPlcDataAcquisitionService>(sp => sp.GetRequiredService<PlcDataAcquisitionService>());
        services.AddSingleton<ProductionHistoryWriter>();
        services.AddSingleton<HistoryService>();
        services.AddSingleton<IProductionHistoryWriter>(sp => sp.GetRequiredService<ProductionHistoryWriter>());
        services.AddSingleton<ProductionHistoryStore>();
        services.AddSingleton<IProductionHistoryReader>(sp => sp.GetRequiredService<ProductionHistoryStore>());
        services.AddSingleton<AlarmHistoryStore>();
        services.AddSingleton<IAlarmHistoryService>(sp => sp.GetRequiredService<AlarmHistoryStore>());
        services.AddSingleton<StatusTransitionHistoryStore>();
        services.AddSingleton<IStatusTransitionHistoryService>(sp => sp.GetRequiredService<StatusTransitionHistoryStore>());
        services.AddSingleton<HistoryStorageDiagnostics>();
        services.AddSingleton<IHistoryService>(sp => sp.GetRequiredService<HistoryService>());
        services.AddSingleton<GpuUsageMonitor>();
        services.AddSingleton<SystemResourceMonitor>();
        services.AddSingleton<IDeviceSelectionService, DeviceSelectionService>();
        services.AddSingleton<DeviceConfigIOService>();
        services.AddSingleton<DevicePlcCommandHandler>();
        services.AddSingleton<AlarmCsvIOService>();

        // ──────────── Remote 模式数据链路（展示端瘦身） ────────────
        services.AddSingleton<KanbanDataClient>();
        services.AddSingleton<RemoteRuntimeSink>();

        return services;
    }
}
