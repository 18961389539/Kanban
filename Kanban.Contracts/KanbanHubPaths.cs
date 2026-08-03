namespace Kanban.Contracts;

/// <summary>
/// SignalR Hub 部署常量（单端口拓扑：Collector 同时托管 Hub 与 WASM 看板页面）。
/// 端口/hub 路径全仓库唯一来源——WASM 自动派生地址、Collector 路由注册、客户端默认地址
/// 均引用此处，避免魔法值散落（历史：5129 与 /hubs/kanban 曾在 3 处硬编码）。
/// </summary>
public static class KanbanHubPaths
{
    /// <summary>Collector SignalR Hub 路径（与 Kanban.Collector/Program.cs MapHub 一致）。</summary>
    public const string HubPath = "/hubs/kanban";

    /// <summary>默认监听/访问端口（Collector 单端口部署：Hub + WASM 页面同端口）。</summary>
    public const int DefaultPort = 5129;
}
