using Kanban.Client;
using Kanban.Web;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// ──────────── 共享 SignalR 客户端（JSON 协议，Collector 双协议并存） ────────────
// Collector 地址：内网看板服务默认 5129 端口；生产环境按需改为服务器 IP（如 http://192.168.1.10:5129/hubs/kanban）
const string CollectorHubUrl = "http://127.0.0.1:5129/hubs/kanban";

builder.Services.AddSingleton(sp => new KanbanDataClient(
    CollectorHubUrl,
    sp.GetRequiredService<ILogger<KanbanDataClient>>(),
    useMessagePack: false));

// ──────────── 看板内存状态（连接 + 快照订阅 + 渲染节流的数据源） ────────────
builder.Services.AddSingleton<DashboardState>();

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

await builder.Build().RunAsync();
