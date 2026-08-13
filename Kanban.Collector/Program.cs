using Kanban.Collector.Hubs;
using Kanban.Collector.Services;
using Kanban.Core.DependencyInjection;
using Kanban.Core.Data;
using Kanban.Core.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
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
        // 用 WaitOne(0) 而非 new Mutex(true,...) + createdNew：前者在前实例崩溃（互斥遗留 abandoned）
        // 时能正常接管所有权启动，后者会误判"已在运行"拒绝启动。
        using var singleInstanceMutex = TryAcquireSingleton(@"Global\Kanban.Collector.SingleInstance");
        if (singleInstanceMutex is null)
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
                retainedFileCountLimit: 14,
                fileSizeLimitBytes: 20_000_000))
            .CreateLogger();

        try
        {
            // 固定 ContentRoot/WebRoot 为 exe 目录：服务或异目录启动时 cwd 可能不在 exe 旁，
            // 否则 wwwroot（WASM 静态托管产物）会解析到错误路径导致 404。
            var appOptions = new WebApplicationOptions
            {
                Args = args,
                ContentRootPath = AppContext.BaseDirectory,
                WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
            };
            var builder = WebApplication.CreateBuilder(appOptions);
            // 单端口部署：默认监听 0.0.0.0:5129（与 KanbanHubPaths.DefaultPort / 屏端默认地址一致）。
            // 不设此值时 Kestrel 默认 http://localhost:5000，发布形态会与客户端默认地址（127.0.0.1:5129）错位。
            // 部署方可用 ASPNETCORE_URLS / --urls 覆盖。
            builder.WebHost.UseUrls($"http://0.0.0.0:{Kanban.Contracts.KanbanHubPaths.DefaultPort}");
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
            builder.Services.AddSingleton<ShiftProgressProvider>();
            builder.Services.AddSingleton<MetaPublisher>();
            builder.Services.AddSingleton<CollectorHealthState>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<MetaPublisher>()); // 同一实例作为定时发布器
            builder.Services.AddHostedService<CollectorWorker>();
            // 历史保留策略：启动后 30s 首次清理，之后每 24h 一次（KANBAN_HISTORY_RETENTION_DAYS 可调）
            builder.Services.AddHostedService<HistoryRetentionService>();

            // ──────────── SignalR 服务端 ────────────
            // MessagePack 二进制序列化：多屏订阅 500ms 快照时显著降低带宽与 CPU
            builder.Services.AddSignalR(options =>
            {
                // 订阅方法（SubscribeSnapshotsAsync 等）是 async Task 长驻循环（推流直到连接断开），
                // 而 MaximumParallelInvocationsPerClient 默认 1 = 同一连接串行执行 Hub 方法：
                // 长驻订阅会永久占用唯一执行槽，导致同一连接上的查询 Invoke（QueryHistoryAsync/
                // QueryHistoryBatchAsync）在服务端排队永不执行、客户端挂起。
                // 调大并行数，让查询/管理调用与订阅流并行处理。
                options.MaximumParallelInvocationsPerClient = 16;
            }).AddMessagePackProtocol();
            // /health/live 只反映进程存活；/health/ready 走业务级 readiness（初始化/采集活性/库可写）
            builder.Services.AddHealthChecks()
                .AddTypeActivatedCheck<CollectorReadinessCheck>("collector_readiness");
            // readiness 的采集活性与恢复文件积压检查取真实数据源（可选委托；测试可省略）
            builder.Services.AddSingleton<Func<int>>(sp => () => sp.GetRequiredService<DeviceRepository>().GetDevicesSnapshot().Count);
            builder.Services.AddSingleton<Func<HistoryDiagnosticsSnapshot>>(
                sp => sp.GetRequiredService<HistoryService>().GetDiagnosticsSnapshot);

            // CORS：允许浏览器展示端（Blazor WASM / 其他前端）跨源连接 Hub。
            // 内网信任模型：放行任意 Origin（WebSocket 不携带凭据，无需 AllowCredentials）。
            // 若日后需要限定来源，可改为 WithOrigins("http://10.x.x.x") 白名单。
            builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
                policy.SetIsOriginAllowed(_ => true).AllowAnyHeader().AllowAnyMethod()));

            var app = builder.Build();

            app.UseCors();

            // 健康检查：
            // - /healthz：兼容旧探针（进程存活语义，保持对外契约）
            // - /health/live：进程存活（Kestrel 可响应即 Healthy）
            // - /health/ready：业务就绪（初始化完成 + 采集活性 + 历史库可写；采集停止时返回 503）
            app.MapHealthChecks("/healthz");
            app.MapHealthChecks("/health/live", new HealthCheckOptions
            {
                Predicate = _ => false, // 无注册检查 → 恒 Healthy（纯存活探针）
            });
            app.MapHealthChecks("/health/ready", new HealthCheckOptions
            {
                Predicate = check => check.Name == "collector_readiness",
                ResultStatusCodes =
                {
                    [HealthStatus.Healthy] = StatusCodes.Status200OK,
                    [HealthStatus.Degraded] = StatusCodes.Status200OK,
                    [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
                },
            });

            // 单端口部署：Collector 同时静态托管 WASM 展示端产物（wwwroot/ 下）。
            // 浏览器访问 http://host:5129/ 直接打开看板，与 Hub 同源，无需 CORS/双端口/双防火墙规则。
            app.UseDefaultFiles();
            // .dat 无默认 MIME（WASM 运行时资源如 icudt_*.dat 会 404），显式注册
            var contentTypeProvider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
            contentTypeProvider.Mappings[".dat"] = "application/octet-stream";
            app.UseStaticFiles(new Microsoft.AspNetCore.Builder.StaticFileOptions
            {
                ContentTypeProvider = contentTypeProvider,
            });
            // SPA 回退：WASM 端多页面路由（/history 等）直接访问/刷新时无物理文件，
            // 回退到 index.html 由 Blazor Router 接管（静态资源仍优先命中，不受影响）。
            app.MapFallbackToFile("index.html");

            // 运行指标（运维可观测）：采集/推送/事件计数 + 订阅者峰值 + 错误计数（text/plain）
            app.MapGet("/metrics", () => Kanban.Collector.Services.CollectorMetrics.Render());

            app.MapHub<KanbanHub>(Kanban.Contracts.KanbanHubPaths.HubPath);
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

    /// <summary>
    /// 单实例互斥获取（健壮版）：非阻塞尝试取得命名互斥所有权。
    /// - 前实例崩溃遗留（AbandonedMutexException）→ 接管所有权，正常启动；
    /// - 已有实例持有 → 返回 null，调用方退出；
    /// - Global\ 前缀权限不足（UnauthorizedAccessException）→ 返回 null 保守退出，
    ///   避免无保护运行导致双写 SQLite/抢端口（宁可误杀也不裸奔）。
    /// </summary>
    private static Mutex? TryAcquireSingleton(string name)
    {
        var mutex = new Mutex(false, name);
        try
        {
            if (mutex.WaitOne(TimeSpan.Zero))
                return mutex; // 取得所有权
            mutex.Dispose();
            return null; // 已有实例持有
        }
        catch (AbandonedMutexException)
        {
            return mutex; // 前实例崩溃，已接管所有权
        }
        catch (UnauthorizedAccessException)
        {
            mutex.Dispose();
            return null;
        }
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
