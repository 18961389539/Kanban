using Kanban.Collector.Hubs;
using Kanban.Collector.Services;
using MainAPP.Data;
using MainAPP.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Kanban.Collector;

/// <summary>
/// Kanban.Collector 采集服务进程入口。
/// 职责：承载 PLC 驱动、采集、历史落库，并通过 SignalR 向展示端（MainAPP）推送实时数据。
/// </summary>
public static class Program
{
    public static async Task Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.Async(a => a.File(
                Path.Combine(CollectorPaths.LogDirectory, "collector_.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14))
            .CreateLogger();

        try
        {
            var builder = WebApplication.CreateBuilder(args);
            builder.Logging.AddSerilog(Log.Logger);

            // ──────────── 采集/存储核心（来自 Kanban.Collector.Core） ────────────
            RegisterCoreServices(builder.Services);

            // ──────────── 服务进程自身 ────────────
            builder.Services.AddSingleton<EventBroadcaster>();
            builder.Services.AddSingleton<SnapshotAggregator>();
            builder.Services.AddSingleton<SnapshotPublisher>();
            builder.Services.AddSingleton<HistoryQueryHandler>();
            builder.Services.AddHostedService<CollectorWorker>();

            // ──────────── SignalR 服务端 ────────────
            builder.Services.AddSignalR();

            var app = builder.Build();
            app.MapHub<KanbanHub>("/hubs/kanban");
            await app.RunAsync();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "采集服务异常退出");
            throw;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    /// <summary>
    /// 注册采集/存储核心服务（参照 MainAPP.AddMainAppCoreServices 的采集部分，去掉 UI/复盘/报表）。
    /// </summary>
    private static void RegisterCoreServices(IServiceCollection services)
    {
        services.AddAutoMapper(cfg => cfg.AddProfile<MainAPP.Mapping.MappingProfile>());
        services.AddSingleton<AppSettings>();
        services.AddSingleton<ProductionBaselineStore>();
        services.AddSingleton<DatabaseProvider>();
        services.AddSingleton<DeviceRepository>();
        services.AddSingleton<WorkOrderRepository>();
        services.AddSingleton<DefectHistoryStore>();
        services.AddSingleton<ISharedPlcDriverFactory, HslSharedPlcDriverFactory>();
        services.AddSingleton<IPlcAddressCodecResolver, PlcAddressCodecResolver>();
        services.AddSingleton<IPlcRuntimeProfileProvider, PlcRuntimeProfileProvider>();
        services.AddSingleton<SharedPlcDriverRouter>();
        services.AddSingleton<IPlcDriver>(sp => sp.GetRequiredService<SharedPlcDriverRouter>());
        services.AddSingleton<PlcConnectionManager>();
        services.AddSingleton<IDeviceAdapter, PlcDeviceAdapter>();
        services.AddSingleton<IDeviceAdapterResolver, DeviceAdapterResolver>();
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
        services.AddSingleton<IAlarmNotificationChannel, SystemAlarmNotificationChannel>();
    }
}

/// <summary>
/// 数据目录约定（与 MainAPP 保持同一套 %APPDATA%/Kanban 布局，环境变量 KANBAN_DATA_DIR 可覆盖）。
/// </summary>
public static class CollectorPaths
{
    public static string DataRoot { get; } =
        Environment.GetEnvironmentVariable("KANBAN_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kanban");

    public static string ConfigDirectory => Path.Combine(DataRoot, "Config");

    public static string LogDirectory => Path.Combine(ConfigDirectory, "Logs");
}
