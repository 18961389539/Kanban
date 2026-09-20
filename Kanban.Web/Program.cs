using Kanban.Client;
using Kanban.Web;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// ──────────── 共享 SignalR 客户端（优先 MessagePack，JSON 协商回退） ────────────
// Collector 地址解析优先级：wwwroot/appsettings.json 的 Kanban:CollectorHubUrl（显式配置，
// 适配 Collector 部署在独立主机/非默认端口）→ 留空时自动派生：与页面同主机同端口
// （单端口部署：从 http://host:5129/ 打开看板时页面与 Hub 同源，无需跨端口；
// dev/测试实例跑非默认端口（如 5130）时同样派生到同端口，不再隐式回落到默认端口 5129）。
var baseUri = new Uri(builder.HostEnvironment.BaseAddress);
var configuredHubUrl = builder.Configuration["Kanban:CollectorHubUrl"];
var collectorHubUrl = string.IsNullOrWhiteSpace(configuredHubUrl)
    ? $"http://{baseUri.Host}:{baseUri.Port}{Kanban.Contracts.KanbanHubPaths.HubPath}"
    : configuredHubUrl;

builder.Services.AddSingleton(sp => new KanbanDataClient(
    collectorHubUrl,
    sp.GetRequiredService<ILogger<KanbanDataClient>>(),
    useMessagePack: true));

// ──────────── 管理域写通道（独立 Hub 路径，与只读监控连接分离） ────────────
// 仅产线总览页的「全部设备 OEE 清零」使用；管理 Hub 由 Collector 单写者持有 PLC 连接，
// 因此屏端的清零请求必须经此通道转发，不能（也无法）在浏览器侧直接写 PLC。
builder.Services.AddSingleton(sp => new KanbanAdminClient(
    collectorHubUrl,
    sp.GetRequiredService<ILogger<KanbanDataClient>>(),
    useMessagePack: true));

// ──────────── 看板内存状态（连接 + 快照订阅 + 渲染节流的数据源） ────────────
builder.Services.AddSingleton<DashboardState>();

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

var host = builder.Build();

// ──────────── 全局未处理异常治理 ────────────
// fire-and-forget 任务（长驻订阅等）断开时的 fault 若未观察，会触发 Blazor 全局错误 UI 且难定位。
// 兜底订阅全局异常事件，保证任何漏网异常都有控制台日志可查（现场第一诊断入口）。
var logger = host.Services.GetRequiredService<ILogger<Program>>();
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    logger.LogError(e.ExceptionObject as Exception, "未处理的全局异常（AppDomain.UnhandledException）");
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    logger.LogError(e.Exception, "未观察的 Task 异常（UnobservedTaskException，多为 fire-and-forget 漏观察）");
    e.SetObserved();
};

await host.RunAsync();
