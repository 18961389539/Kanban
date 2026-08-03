using Kanban.Contracts.Dtos;

namespace Kanban.Contracts.Abstractions;

/// <summary>
/// SignalR 强类型 Hub 的客户端回调契约（服务端 → 客户端推送方向）。
/// MainAPP 的 HubConnection 以泛型 <see cref="HubConnectionExtensions"/> 强类型订阅这些方法。
/// </summary>
public interface IKanbanHubClient
{
    /// <summary>推送设备实时快照（约 200~500ms 一次，全量快照）</summary>
    Task OnSnapshot(DeviceSnapshotDto snapshot);

    /// <summary>推送报警边沿事件</summary>
    Task OnAlarmEvent(AlarmEventDto alarmEvent);

    /// <summary>推送状态转换边沿事件</summary>
    Task OnStatusEvent(StatusEventDto statusEvent);

    /// <summary>推送低频元数据包（约 5s 一次：全部设备当前工单 + 班次进度）</summary>
    Task OnMeta(MetaStateDto meta);
}

/// <summary>
/// SignalR Hub 方法契约——**监控域**（客户端 → 服务端调用方向，展示/查询/订阅）。
/// 仅作为方法名与签名约定（配合 nameof 使用），由 Collector 的 KanbanHub 实现。
/// 注意：SignalR 不支持 CancellationToken 参数（服务端通过 HubCallerContext.Abort 感知断开），
/// 因此此处方法签名一律不含 CancellationToken。
/// 管理写操作（设备/工单/采集设置）见 <see cref="IKanbanAdminServer"/>——按能力域拆分，
/// 新功能按"监控 or 管理"落位，避免单接口膨胀成上帝接口。
/// </summary>
public interface IKanbanHubServer
{
    /// <summary>连接后先拉取当前全部设备快照</summary>
    Task<IReadOnlyList<DeviceSnapshotDto>> GetCurrentSnapshotsAsync();

    /// <summary>订阅实时快照流（订阅后服务端持续 OnSnapshot）</summary>
    Task SubscribeSnapshotsAsync();

    /// <summary>订阅报警事件流，afterSeq = 客户端已消费最大序号（0 = 从头订阅）</summary>
    Task SubscribeAlarmEventsAsync(long afterSeq);

    /// <summary>订阅状态事件流，afterSeq = 客户端已消费最大序号（0 = 从头订阅）</summary>
    Task SubscribeStatusEventsAsync(long afterSeq);

    /// <summary>历史查询（Unary）</summary>
    Task<HistoryQueryResponse> QueryHistoryAsync(HistoryQueryRequest request);

    /// <summary>拉取设备配置（Remote 模式屏端零配置：设备列表从此获取，不依赖本地 devices.json）</summary>
    Task<IReadOnlyList<DeviceConfigDto>> GetDevicesAsync();

    /// <summary>查询设备当前工单（Running 优先，无则回退最新 Pending；无工单返回 null）。</summary>
    Task<WorkOrderDto?> GetCurrentWorkOrderAsync(string deviceId);

    /// <summary>查询当前班次进度（按班次配置与当前时间计算，无班次配置时 IsInShift=false）。</summary>
    Task<ShiftProgressDto> GetShiftProgressAsync();

    /// <summary>订阅低频元数据流（约 5s 一次 OnMeta：全部设备当前工单 + 班次进度；替代轮询 Invoke）</summary>
    Task SubscribeMetaAsync();

    /// <summary>
    /// 服务端版本握手（Collector 程序集信息版本）。客户端用于升级兼容性校验：
    /// 版本不一致时提示"客户端版本过旧/服务已升级"，替代升级后无征兆的运行时异常。
    /// </summary>
    Task<string> GetServerVersionAsync();

    /// <summary>看板标题（Collector settings.json 的 AppTitle；屏端零配置——从服务端拉取而非逐屏配置）。</summary>
    Task<string> GetTitleAsync();

    /// <summary>
    /// 界面语言（Collector settings.json 的 Language，AppLanguage 枚举值 0=中文/1=English/2=日本語）。
    /// 屏端零配置——多语言由服务端统一控制，所有屏端跟随，避免逐屏配置。
    /// </summary>
    Task<int> GetLanguageAsync();
}

/// <summary>
/// SignalR Hub 方法契约——**管理域**（客户端 → 服务端调用方向，写操作）。
/// 与 <see cref="IKanbanHubServer"/> 由同一 KanbanHub 实现；独立接口让管理写操作与展示查询
/// 的能力边界清晰（Collector 单写者入口），新管理功能加在这里，不污染监控接口。
/// </summary>
public interface IKanbanAdminServer
{
    /// <summary>同步设备配置（Remote 模式：MainAPP 设备管理页保存时推给 Collector 落盘 devices.json）</summary>
    Task SaveDevicesAsync(IReadOnlyList<DeviceConfigDto> devices);

    /// <summary>新增/更新工单（Remote 模式：Collector 落库 work_orders.db，返回带 Id 的落库结果）</summary>
    Task<WorkOrderDto> UpsertWorkOrderAsync(WorkOrderDto workOrder);

    /// <summary>删除工单（Remote 模式：Collector 落库）</summary>
    Task DeleteWorkOrderAsync(int workOrderId);

    /// <summary>
    /// 采集设置同步（Remote 模式：MainAPP 设置页保存时把采集相关参数推给 Collector 落盘 settings.json 并热生效）。
    /// 解决"Remote 模式下设置改了采集进程无感知"的配置分裂问题。
    /// </summary>
    Task SaveCollectorSettingsAsync(CollectorSettingsDto settings);
}
