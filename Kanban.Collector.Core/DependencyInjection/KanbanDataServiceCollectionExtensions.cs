using Kanban.Core.Data;
using Kanban.Core.Mapping;
using Kanban.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kanban.Core.DependencyInjection;

/// <summary>
/// 采集/存储核心服务的共享 DI 注册。
/// MainAPP（AddMainAppCoreServices）与 Kanban.Collector（Program.RegisterCoreServices）此前各写一份，
/// 本次抽取为单一入口，避免以后新增服务时漏注册。调用方仍可自行覆盖接口映射
/// （如 MainAPP 在调用后把 IHistoryService 等重定向到 RemoteHistoryQueryService）。
/// </summary>
public static class KanbanDataServiceCollectionExtensions
{
    /// <summary>
    /// 注册采集/存储核心服务（PLC 驱动、采集、历史存储、仓储、配置）。
    /// <param name="appSettings">可选：传入现成实例时使用之（测试宿主预置配置），否则按单例创建。</param>
    /// </summary>
    public static IServiceCollection AddKanbanDataServices(
        this IServiceCollection services,
        AppSettings? appSettings = null)
    {
        services.AddAutoMapper(cfg => cfg.AddProfile<MappingProfile>());

        if (appSettings == null)
            services.AddSingleton<AppSettings>();
        else
            services.AddSingleton(appSettings);

        services.AddSingleton<ProductionBaselineStore>();
        services.AddSingleton<DatabaseProvider>();
        services.AddSingleton<DeviceRepository>();
        services.AddSingleton<IDeviceRepository>(sp => sp.GetRequiredService<DeviceRepository>());
        services.AddSingleton<WorkOrderRepository>();
        services.AddSingleton<IWorkOrderRepository>(sp => sp.GetRequiredService<WorkOrderRepository>());
        services.AddSingleton<DefectHistoryStore>();
        // 配方库：recipes.json 存储 + 下发执行（写 PLC/读回校验/回滚）
        services.AddSingleton<RecipeStore>();
        services.AddSingleton<IRecipeStore>(sp => sp.GetRequiredService<RecipeStore>());
        services.AddSingleton<RecipeApplier>();
        // 操作审计：批量追加写 audit_logs.db + 分页查询 + 30 天滚动清理
        services.AddSingleton<AuditService>();
        services.AddSingleton<IAuditService>(sp => sp.GetRequiredService<AuditService>());
        // 运行模式判定（Local/Remote 统一入口，避免散落 DataMode 判断）
        services.AddSingleton<IRuntimeMode, RuntimeMode>();

        // ──────────── PLC 驱动 / 连接 / 采集 ────────────
        services.AddSingleton<IPlcBrandDescriptor, MitsubishiPlcBrandDescriptor>();
        services.AddSingleton<IPlcBrandDescriptor, SiemensPlcBrandDescriptor>();
        services.AddSingleton<IPlcBrandDescriptor, ModbusTcpPlcBrandDescriptor>();
        services.AddSingleton<IPlcBrandDescriptor, OmronPlcBrandDescriptor>();
        services.AddSingleton<IPlcBrandDescriptor, KeyencePlcBrandDescriptor>();
        services.AddSingleton<IPlcBrandRegistry, PlcBrandRegistry>();
        services.AddSingleton<ISharedPlcDriverFactory, HslSharedPlcDriverFactory>();
        services.AddSingleton<IPlcAddressCodecResolver, PlcAddressCodecResolver>();
        services.AddSingleton<IPlcRuntimeProfileProvider, PlcRuntimeProfileProvider>();
        services.AddSingleton<SharedPlcDriverRouter>();
        services.AddSingleton<IPlcDriver>(sp => sp.GetRequiredService<SharedPlcDriverRouter>());
        services.AddSingleton<PlcConnectionManager>();
        services.AddSingleton<IPlcConnectionManager>(sp => sp.GetRequiredService<PlcConnectionManager>());
        services.AddSingleton<IDeviceAdapter, PlcDeviceAdapter>();
        services.AddSingleton<IDeviceAdapterResolver, DeviceAdapterResolver>();
        services.AddSingleton<IAlarmNotificationChannel, SystemAlarmNotificationChannel>();
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

        // ──────────── 历史存储（默认实现；MainAPP Remote 模式可整体重定向到远程代理） ────────────
        services.AddSingleton<ProductionHistoryWriter>();
        services.AddSingleton<IProductionHistoryWriter>(sp => sp.GetRequiredService<ProductionHistoryWriter>());
        services.AddSingleton<HistoryService>();
        services.AddSingleton<IHistoryService>(sp => sp.GetRequiredService<HistoryService>());
        services.AddSingleton<IHistoryQueryExecutor>(sp => sp.GetRequiredService<HistoryService>());
        services.AddSingleton<ProductionHistoryStore>();
        services.AddSingleton<IProductionHistoryReader>(sp => sp.GetRequiredService<ProductionHistoryStore>());
        services.AddSingleton<AlarmHistoryStore>();
        services.AddSingleton<IAlarmHistoryService>(sp => sp.GetRequiredService<AlarmHistoryStore>());
        services.AddSingleton<StatusTransitionHistoryStore>();
        services.AddSingleton<IStatusTransitionHistoryService>(sp => sp.GetRequiredService<StatusTransitionHistoryStore>());
        services.AddSingleton<HistoryStorageDiagnostics>();

        return services;
    }
}
