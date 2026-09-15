using Kanban.Client;
using Kanban.Collector.Core.Services;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
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
        services.AddSingleton<RemoteDataLinkBootstrapper>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IUserHelpService, UserHelpService>();
        services.AddSingleton<IFirstRunGuideService, FirstRunGuideService>();
        services.AddSingleton<ILoginDialogService, LoginDialogService>();
        services.AddSingleton<SettingsViewModel>();
        // 登录窗口及其 ViewModel 必须每次重新创建：Window 关闭后不可再次显示，
        // LoginViewModel 也不能残留上一次的 LoginSucceeded/SelectedUser/ErrorMessage 状态。
        services.AddTransient<LoginViewModel>();
        services.AddTransient<LoginWindow>();
        services.AddSingleton<UserManagerViewModel>();
        services.AddSingleton<DataSourceMonitoringViewModel>();
        services.AddSingleton<RuntimeMonitoringViewModel>(sp => new RuntimeMonitoringViewModel(
            sp.GetRequiredService<PlcConnectionManager>(),
            sp.GetRequiredService<PlcDataAcquisitionService>(),
            sp.GetRequiredService<AppSettings>(),
            sp.GetRequiredService<DeviceRepository>(),
            // 走 IHistoryService（Remote 模式重定向到 RemoteHistoryQueryService）；
            // 之前注入具体类 HistoryService，Remote 模式下本页会静默读取本地空库（审查修复 2026-09-03）。
            sp.GetRequiredService<IHistoryService>(),
            sp.GetRequiredService<SystemResourceMonitor>(),
            sp.GetRequiredService<IDialogService>(),
            sp.GetService<IPlcAddressCodecResolver>(),
            sp.GetService<IPlcRuntimeProfileProvider>(),
            sp.GetService<KanbanDataClient>(),
            sp.GetRequiredService<IRuntimeMode>()));
        return services;
    }

    private static IServiceCollection AddDevicePresentationModule(this IServiceCollection services)
    {
        services.AddSingleton<IDeviceSetupWizardService, DeviceSetupWizardService>();
        services.AddSingleton<DeviceManagerViewModel>();
        // 配方管理页 VM（独立导航页）：构造依赖全部已注册，DI 自动解析
        services.AddSingleton<RecipeManagerViewModel>();
        services.AddSingleton<HomeViewModel>(sp => new HomeViewModel(
            sp.GetRequiredService<DeviceRepository>(), sp.GetRequiredService<PlcConnectionManager>(),
            sp.GetRequiredService<AppSettings>(), sp.GetRequiredService<IPlcDataAcquisitionService>(),
            sp.GetRequiredService<IDeviceSelectionService>(), sp.GetRequiredService<WorkOrderRepository>(),
            sp.GetRequiredService<IDialogService>(), sp.GetRequiredService<IWorkOrderService>(),
            sp.GetRequiredService<IRuntimeMode>(),
            remoteRuntimeSink: sp.GetService<RemoteRuntimeSink>(),
            alarmSessionMute: sp.GetRequiredService<IAlarmSessionMute>(),
            alarmHistoryService: sp.GetRequiredService<IHistoryService>(),
            defectHistoryReader: sp.GetService<IDefectHistoryReader>()));
        services.AddSingleton<ProductionLineViewModel>(sp => new ProductionLineViewModel(
            sp.GetRequiredService<DeviceRepository>(), sp.GetRequiredService<IDeviceSelectionService>(),
            sp.GetRequiredService<IPlcDataAcquisitionService>(), sp.GetRequiredService<AppSettings>(),
            sp.GetRequiredService<IDialogService>(),
            // 一键全设备 OEE 清零：复用设备参数页的 PLC 命令处理器，另接连接态/权限用于 CanExecute
            sp.GetRequiredService<DevicePlcCommandHandler>(),
            sp.GetRequiredService<PlcConnectionManager>(),
            sp.GetRequiredService<UserSession>()));
        services.AddSingleton<AlarmCenterViewModel>(sp => new AlarmCenterViewModel(
            sp.GetRequiredService<IHistoryService>(), sp.GetRequiredService<DeviceRepository>(),
            sp.GetRequiredService<IDialogService>(),
            appSettings: sp.GetRequiredService<AppSettings>(),
            alarmSessionMute: sp.GetRequiredService<IAlarmSessionMute>()));
        services.AddSingleton<OverviewViewModel>(sp => new OverviewViewModel(
            sp.GetRequiredService<DeviceRepository>(),
            sp.GetRequiredService<AppSettings>(), sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<IDeviceSelectionService>(),
            sp.GetRequiredService<IProductionReviewPdfService>(),
            sp.GetRequiredService<WorkOrderRepository>(),
            sp.GetRequiredService<IProductionReviewCsvExportService>(),
            sp.GetRequiredService<IProductionReviewChartService>(),
            sp.GetRequiredService<IOverviewDashboardService>()));
        services.AddSingleton<DeviceDetailViewModel>(sp => new DeviceDetailViewModel(
            sp.GetRequiredService<DeviceRepository>(), sp.GetRequiredService<IHistoryService>(),
            sp.GetRequiredService<IDeviceSelectionService>(), sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DeviceDetailViewModel>>(),
            sp.GetRequiredService<WorkOrderRepository>(), sp.GetRequiredService<IWorkOrderService>(),
            sp.GetRequiredService<AppSettings>(),
            sp.GetService<IDataSourceSnapshotStore>()));
        return services;
    }

    private static IServiceCollection AddHistoryPresentationModule(this IServiceCollection services)
    {
        services.AddSingleton<HistoryQueryViewModel>();
        return services;
    }

    private static void AddWorkOrderPresentationModule(this IServiceCollection services)
    {
        services.AddSingleton<WorkOrderManagerViewModel>(sp => new WorkOrderManagerViewModel(
            sp.GetRequiredService<WorkOrderRepository>(), sp.GetRequiredService<IWorkOrderService>(),
            sp.GetRequiredService<DeviceRepository>(), sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<UserSession>(),
            sp.GetService<ISnEventStore>()));
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
        RegisterPage<UserManagerView, UserManagerViewModel>(services, NavigationPageCatalog.UserManager);
        services.AddSingleton<AuditQueryViewModel>();
        RegisterPage<AuditQueryView, AuditQueryViewModel>(services, NavigationPageCatalog.Audit);
        RegisterPage<RecipeManagerView, RecipeManagerViewModel>(services, NavigationPageCatalog.RecipeManager);
        RegisterPage<DataSourceMonitoringView, DataSourceMonitoringViewModel>(services, NavigationPageCatalog.DataSourceMonitoring);
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
                // 延迟解析：页面首次进入时才创建 ViewModel（TViewModel 仍是单例，首次创建后复用）
                () => sp.GetRequiredService<TViewModel>()));
    }
}
