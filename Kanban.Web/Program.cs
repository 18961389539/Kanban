using Kanban.Client;
using Kanban.Web;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// ──────────── 共享 SignalR 客户端（JSON 协议，Collector 双协议并存） ────────────
// Collector 地址动态派生：Web 与 Collector 同机部署（默认端口 5129）。
// 局域网场景：手机用本机 IP 访问 Web（如 http://192.168.1.6:5186）时，
// BaseAddress.Host 即为该主机 IP，自动连同一主机的 Collector——零配置跨设备。
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

await builder.Build().RunAsync();
