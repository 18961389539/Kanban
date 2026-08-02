using Kanban.Collector.Hubs;
using Kanban.Collector.Services;
using Kanban.Core.DependencyInjection;
using Kanban.Core.Data;
using Kanban.Core.Services;
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
        // 单实例保护：防误启双进程抢端口/双写 SQLite（与 MainAPP 的 Mutex 模式一致）。
        // 用 Global\ 前缀：Windows 服务跑在 Session 0，与交互会话（屏端控制台）互斥检测生效。
        using var singleInstanceMutex = new Mutex(true, @"Global\Kanban.Collector.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance)
        {
            Console.Error.WriteLine("Kanban.Collector 已在运行（单实例保护），本实例退出。");
            return;
        }

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
            // Windows 服务宿主：作为服务安装后由 SCM 拉起（开机自启 + 崩溃自动重启策略由脚本配置）；
            // 未安装服务时以控制台方式正常运行，行为不变。
            builder.Host.UseWindowsService(options => options.ServiceName = "KanbanCollector");
            builder.Logging.AddSerilog(Log.Logger);

            // ──────────── 采集/存储核心（来自 Kanban.Collector.Core） ────────────
            RegisterCoreServices(builder.Services);

            // ──────────── 服务进程自身 ────────────
            builder.Services.AddSingleton<EventBroadcaster>();
            builder.Services.AddSingleton<SnapshotAggregator>();
            builder.Services.AddSingleton<SnapshotPublisher>();
            builder.Services.AddSingleton<HistoryQueryHandler>();
            builder.Services.AddSingleton<CollectorDiagnosticsProvider>();
            builder.Services.AddSingleton<ConfigSyncHandler>();
            builder.Services.AddHostedService<CollectorWorker>();

            // ──────────── SignalR 服务端 ────────────
            // MessagePack 二进制序列化：多屏订阅 500ms 快照时显著降低带宽与 CPU
            builder.Services.AddSignalR().AddMessagePackProtocol();
            builder.Services.AddHealthChecks();

            // CORS：允许浏览器展示端（Blazor WASM / 其他前端）跨源连接 Hub。
            // 内网信任模型：放行任意 Origin（WebSocket 不携带凭据，无需 AllowCredentials）。
            // 若日后需要限定来源，可改为 WithOrigins("http://10.x.x.x") 白名单。
            builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
                policy.SetIsOriginAllowed(_ => true).AllowAnyHeader().AllowAnyMethod()));

            var app = builder.Build();

            // 健康检查：运维/看板客户端探测进程存活（GET /healthz）
            app.UseCors();
            app.MapHealthChecks("/healthz");

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
    /// 注册采集/存储核心服务（与 MainAPP.AddMainAppCoreServices 共享 AddKanbanDataServices 单一入口）。
    /// </summary>
    private static void RegisterCoreServices(IServiceCollection services)
        => services.AddKanbanDataServices();
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
