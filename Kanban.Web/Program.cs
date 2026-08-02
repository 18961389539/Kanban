using Kanban.Client;
using Kanban.Web;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// ──────────── 共享 SignalR 客户端（JSON 协议，Collector 双协议并存） ────────────
// Collector 地址动态派生：Web 与 Collector 同机部署（默认端口 5129）。
// 单端口部署：从 http://host:5129/ 打开看板时 BaseAddress.Host 即主机 IP，自动连同源 Hub。
// dev 模式（5186）同样派生到同一主机的 :5129——零配置跨环境。
// 若 Collector 部署在独立主机，改这里为固定地址（如 http://192.168.1.10:5129/hubs/kanban）。
var baseUri = new Uri(builder.HostEnvironment.BaseAddress);
var collectorHubUrl = $"http://{baseUri.Host}:5129/hubs/kanban";

builder.Services.AddSingleton(sp => new KanbanDataClient(
    collectorHubUrl,
    sp.GetRequiredService<ILogger<KanbanDataClient>>(),
    useMessagePack: false));

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
