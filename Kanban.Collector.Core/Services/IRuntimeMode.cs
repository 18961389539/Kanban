namespace Kanban.Core.Services;

/// <summary>
/// 运行模式查询（数据模式 Local/Remote 的统一判定入口）。
/// 各 ViewModel/Service 应注入本接口判定 IsRemote，禁止直接散落
/// <c>settings.DataMode == KanbanDataMode.Remote</c> 判断——新增第三种模式时只需改这里。
/// 实现每次读取 AppSettings（懒解析安全：AppSettings.Load 在任何服务构造前完成）。
/// </summary>
public interface IRuntimeMode
{
    KanbanDataMode DataMode { get; }

    /// <summary>Remote 模式（瘦客户端连接 Kanban.Collector）。</summary>
    bool IsRemote => DataMode == KanbanDataMode.Remote;
}

/// <summary><see cref="IRuntimeMode"/> 默认实现：委托 <see cref="AppSettings.DataMode"/>（单例）。</summary>
public sealed class RuntimeMode : IRuntimeMode
{
    private readonly AppSettings _settings;

    public RuntimeMode(AppSettings settings)
    {
        _settings = settings;
    }

    public KanbanDataMode DataMode => _settings.DataMode;
}
