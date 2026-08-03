using Kanban.Client;
using Kanban.Core.Services;
using Kanban.Core.Models;
using Kanban.Core.Data;
using Kanban.Core.Entities;
using MainAPP.Models;
using MainAPP.ViewModels;
using MainAPP.Views;
using Microsoft.Extensions.DependencyInjection;

namespace MainAPP.Services;

public static class MainAppPresentationServiceCollectionExtensions
{
    public static IServiceCollection AddMainAppPresentationServices(this IServiceCollection services)
    {
        services.AddApplicationPresentationModule();
        services.AddDevicePresentationModule();
        services.AddHistoryPresentationModule();
        services.AddWorkOrderPresentationModule();
        services.AddNavigationPresentationModule();
        return services;
    }

    private static IServiceCollection AddApplicationPresentationModule(this IServiceCollection services)
    {
        services.AddSingleton<ApplicationRuntime>();
        services.AddSingleton<IApplicationRuntime>(sp => sp.GetRequiredService<ApplicationRuntime>());
        services.AddSingleton<ApplicationStartupCoordinator>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<RuntimeMonitoringViewModel>(sp => new RuntimeMonitoringViewModel(
            sp.GetRequiredService<PlcConnectionManager>(),
            sp.GetRequiredService<PlcDataAcquisitionService>(),
            sp.GetRequiredService<AppSettings>(),
            sp.GetRequiredService<DeviceRepository>(),
            sp.GetRequiredService<HistoryService>(),
            sp.GetRequiredService<SystemResourceMonitor>(),
            sp.GetService<IPlcAddressCodecResolver>(),
            sp.GetService<IPlcRuntimeProfileProvider>(),
            sp.GetService<KanbanDataClient>(),
            sp.GetRequiredService<IRuntimeMode>()));
        return services;
    }

    private static IServiceCollection AddDevicePresentationModule(this IServiceCollection services)
    {
        services.AddSingleton<DeviceManagerViewModel>();
        services.AddSingleton<HomeViewModel>(sp => new HomeViewModel(
            sp.GetRequiredService<DeviceRepository>(), sp.GetRequiredService<PlcConnectionManager>(),
            sp.GetRequiredService<AppSettings>(), sp.GetRequiredService<IPlcDataAcquisitionService>(),
            sp.GetRequiredService<IDeviceSelectionService>(), sp.GetRequiredService<WorkOrderRepository>(),
            sp.GetRequiredService<IDialogService>(), sp.GetRequiredService<IWorkOrderService>(),
            sp.GetRequiredService<IRuntimeMode>()));
        services.AddSingleton<ProductionLineViewModel>(sp => new ProductionLineViewModel(
            sp.GetRequiredService<DeviceRepository>(), sp.GetRequiredService<IDeviceSelectionService>(),
            sp.GetRequiredService<IPlcDataAcquisitionService>(), sp.GetRequiredService<AppSettings>()));
        services.AddSingleton<AlarmCenterViewModel>(sp => new AlarmCenterViewModel(
            sp.GetRequiredService<IHistoryService>(), sp.GetRequiredService<DeviceRepository>(),
            sp.GetRequiredService<IDialogService>()));
        services.AddSingleton<OverviewViewModel>(sp => new OverviewViewModel(
            sp.GetRequiredService<DeviceRepository>(),
            sp.GetRequiredService<AppSettings>(), sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<IDeviceSelectionService>(),
            sp.GetRequiredService<IProductionReviewPdfService>(),
            sp.GetRequiredService<IDefectHistoryReader>(),
            sp.GetRequiredService<WorkOrderRepository>(),
            sp.GetRequiredService<IProductionReviewAnalysisService>(),
            sp.GetRequiredService<IProductionReviewDataService>(),
            sp.GetRequiredService<IProductionReviewCsvExportService>(),
            sp.GetRequiredService<IProductionReviewChartService>(),
            sp.GetRequiredService<IProductionReviewMetricsService>()));
        services.AddSingleton<DeviceDetailViewModel>(sp => new DeviceDetailViewModel(
            sp.GetRequiredService<DeviceRepository>(), sp.GetRequiredService<IHistoryService>(),
            sp.GetRequiredService<IDeviceSelectionService>(), sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DeviceDetailViewModel>>(),
            sp.GetRequiredService<WorkOrderRepository>(), sp.GetRequiredService<IWorkOrderService>()));
        return services;
    }

    private static IServiceCollection AddHistoryPresentationModule(this IServiceCollection services)
    {
        services.AddSingleton<HistoryQueryViewModel>();
        return services;
    }

    private static IServiceCollection AddWorkOrderPresentationModule(this IServiceCollection services)
    {
        services.AddSingleton<WorkOrderManagerViewModel>(sp => new WorkOrderManagerViewModel(
            sp.GetRequiredService<WorkOrderRepository>(), sp.GetRequiredService<IWorkOrderService>(),
            sp.GetRequiredService<DeviceRepository>(), sp.GetRequiredService<IDialogService>()));
        return services;
    }

    private static IServiceCollection AddNavigationPresentationModule(this IServiceCollection services)
    {
        RegisterPage<HomeView, HomeViewModel>(services, NavigationPageCatalog.Home);
        RegisterPage<ProductionLineView, ProductionLineViewModel>(services, NavigationPageCatalog.ProductionLine);
        RegisterPage<AlarmCenterView, AlarmCenterViewModel>(services, NavigationPageCatalog.AlarmCenter);
        RegisterPage<DeviceManagerView, DeviceManagerViewModel>(services, NavigationPageCatalog.DeviceManager);
        RegisterPage<WorkOrderManagerView, WorkOrderManagerViewModel>(services, NavigationPageCatalog.WorkOrder);
        RegisterPage<HistoryQueryView, HistoryQueryViewModel>(services, NavigationPageCatalog.HistoryQuery);
        RegisterPage<OverviewView, OverviewViewModel>(services, NavigationPageCatalog.Overview);
        RegisterPage<SettingsView, SettingsViewModel>(services, NavigationPageCatalog.Settings);
        RegisterPage<RuntimeMonitoringView, RuntimeMonitoringViewModel>(services, NavigationPageCatalog.RuntimeMonitoring);
        RegisterPage<DeviceDetailView, DeviceDetailViewModel>(services, NavigationPageCatalog.DeviceDetail);
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();
        return services;
    }

    private static void RegisterPage<TView, TViewModel>(IServiceCollection services, NavigationPageDefinition definition)
        where TView : class
        where TViewModel : class
    {
        services.AddSingleton<TView>();
        services.AddSingleton<INavigationPageModule>(sp =>
            new NavigationPageModule<TView, TViewModel>(
                definition,
                () => sp.GetRequiredService<TView>(),
                sp.GetRequiredService<TViewModel>()));
    }
}
