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

    /// <summary>配方下发进度推送（ApplyRecipeAsync 执行期间逐项推送，最终结果仍由 Invoke 返回值承载）</summary>
    Task OnRecipeApplyProgress(RecipeApplyProgressDto progress);

    /// <summary>Collector 本地化配置变化后推送语言代码和覆盖快照。</summary>
    Task OnLocalizationChanged(LocalizationChangedDto localization);
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

    /// <summary>
    /// 产量窗口服务端分析：全量在 Collector 侧做差分与 15 分钟抽样，屏端只收 KPI + 压缩点。
    /// </summary>
    Task<ProductionWindowAnalysisDto> QueryProductionWindowAnalysisAsync(HistoryQueryRequest request);

    /// <summary>
    /// 报警窗口服务端统计：全量在 Collector 侧做计数/排行，屏端只收 KPI + Top + 最近事件。
    /// </summary>
    Task<AlarmWindowStatsDto> QueryAlarmWindowStatsAsync(HistoryQueryRequest request);

    /// <summary>
    /// 状态窗口服务端分析：时长 / 按天 / 甘特段。旧 Collector 无此方法时屏端回退分页全量。
    /// </summary>
    Task<StatusWindowAnalysisDto> QueryStatusWindowAnalysisAsync(HistoryQueryRequest request);

    /// <summary>
    /// 复盘窗口服务端分析：产量/状态/报警/缺陷在 Collector 侧聚合。旧 Collector 无此方法时屏端回退批量全量。
    /// </summary>
    Task<ReviewWindowAnalysisDto> QueryReviewAnalysisAsync(HistoryQueryRequest request);

    /// <summary>
    /// 批量历史查询（Unary）：多个子查询一次往返，服务端对每个子查询做全量翻页聚合。
    /// 用于生产复盘等「多设备 × 多类型」批查场景，避免逐设备逐页串行往返的分钟级延迟。
    /// </summary>
    Task<BatchHistoryQueryResponse> QueryHistoryBatchAsync(BatchHistoryQueryRequest request);

    /// <summary>
    /// SN 序列号追溯查询（Unary）：按 SN 精确 / 工单明细 / 设备+时间范围 反查逐件事件。
    /// 服务端 Count + Skip/Take 分页；与采集侧共用同一存储（sn_events.db）。
    /// </summary>
    Task<SnEventQueryResponse> QuerySnEventsAsync(SnEventQueryRequest request);

    /// <summary>拉取设备配置（Remote 模式屏端零配置：设备列表从此获取，不依赖本地 devices.json）</summary>
    Task<IReadOnlyList<DeviceConfigDto>> GetDevicesAsync();

    /// <summary>查询设备当前工单（Running 优先，无则回退最新 Pending；无工单返回 null）。</summary>
    Task<WorkOrderDto?> GetCurrentWorkOrderAsync(string deviceId);

    /// <summary>
    /// 查询工单产量聚合（合格/不良/达成率）。按工单时间窗口差分，与 WPF 工单进度口径一致。
    /// 工单不存在时返回 OkCount=0 的空摘要。
    /// </summary>
    Task<WorkOrderProductionSummaryDto> GetWorkOrderProductionSummaryAsync(int workOrderId);

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

    /// <summary>过道电视是否轮播（展示模式或显示设置勾选）。旧 Collector 无此方法时屏端保持关闭。</summary>
    Task<bool> GetDisplayCarouselEnabledAsync();

    /// <summary>
    /// 旧版界面语言枚举值（兼容旧版屏端）。新屏端应调用 GetLanguageCodeAsync。
    /// </summary>
    Task<int> GetLanguageAsync();

    /// <summary>
    /// 界面语言文化代码（Collector settings.json 的 LanguageCode）。
    /// 屏端零配置——多语言由服务端统一控制，新增 CSV 语言无需修改 Hub 契约。
    /// </summary>
    Task<string> GetLanguageCodeAsync();

    /// <summary>读取 Collector 启动时加载的本地化覆盖表（Remote 展示端只读）。</summary>
    Task<IReadOnlyList<LocalizationOverrideDto>> GetLocalizationOverridesAsync();

    /// <summary>工单列表（只读；WEB 只读管理页数据源。写操作仍走 IKanbanAdminServer）。</summary>
    Task<IReadOnlyList<WorkOrderDto>> GetWorkOrdersAsync();

    /// <summary>采集设置快照（只读；WEB 设置页展示数据源，写操作仍走 SaveCollectorSettingsAsync）。</summary>
    Task<CollectorSettingsDto> GetCollectorSettingsAsync();

    /// <summary>审计日志分页查询（只读；服务端 Count + Skip/Take，与历史查询同构）。</summary>
    Task<AuditLogQueryResponse> QueryAuditLogsAsync(AuditLogQueryRequest request);

    /// <summary>拉取全部配方（只读；管理页/看板展示数据源）。</summary>
    Task<IReadOnlyList<RecipeDto>> GetRecipesAsync();

    /// <summary>
    /// 查询当前活跃报警状态快照（Unary）：采集端 ActiveAlarmStates 表的 IsActive=true 行。
    /// 前端活跃报警墙直查真源，取代"回溯历史事件推断活跃状态"的旧逻辑。
    /// </summary>
    Task<IReadOnlyList<ActiveAlarmStateDto>> QueryActiveAlarmStatesAsync(string? deviceId = null);

    /// <summary>
    /// 拉取 Collector 运行诊断快照（运行监控页 Remote 模式）。
    /// 审查修复 2026-09-05（P2）：此前未在契约接口声明，客户端以字符串字面量
    /// "GetDiagnosticsAsync" 调用（KanbanDataClient.GetDiagnosticsAsync），服务端改名/移除后
    /// 编译期零感知、仅运行时报"方法不存在"。声明后客户端改用 nameof(IKanbanHubServer.GetDiagnosticsAsync)。
    /// </summary>
    Task<CollectorDiagnosticsDto> GetDiagnosticsAsync();
}

/// <summary>
/// SignalR Hub 方法契约——**管理域**（客户端 → 服务端调用方向，写操作）。
/// 与 <see cref="IKanbanHubServer"/> 由同一 KanbanHub 实现；独立接口让管理写操作与展示查询
/// 的能力边界清晰（Collector 单写者入口），新管理功能加在这里，不污染监控接口。
/// </summary>
public interface IKanbanAdminServer
{
    /// <summary>写入远程审计；操作人由服务端连接上下文补充。</summary>
    Task RecordAuditAsync(AuditLogRecordRequest request);

    /// <summary>同步设备配置（Remote 模式：MainAPP 设备管理页保存时推给 Collector 落盘 devices.json）</summary>
    Task SaveDevicesAsync(IReadOnlyList<DeviceConfigDto> devices);

    /// <summary>查询 Collector 是否存在可回滚的设备配置备份。</summary>
    Task<bool> HasDeviceBackupAsync();

    /// <summary>
    /// 从 Collector 自有的 devices.json.bak 恢复设备配置并返回恢复后的权威快照。
    /// 返回 null 表示 Collector 当前没有可用备份；空列表是合法的"删除全部设备"配置。
    /// </summary>
    Task<IReadOnlyList<DeviceConfigDto>?> RollbackDevicesAsync();

    /// <summary>新增/更新工单（Remote 模式：Collector 落库 work_orders.db，返回带 Id 的落库结果）</summary>
    Task<WorkOrderDto> UpsertWorkOrderAsync(WorkOrderDto workOrder);

    /// <summary>删除工单（Remote 模式：Collector 落库）</summary>
    Task DeleteWorkOrderAsync(int workOrderId);

    /// <summary>
    /// 采集设置同步（Remote 模式：MainAPP 设置页保存时把采集相关参数推给 Collector 落盘 settings.json 并热生效）。
    /// 解决"Remote 模式下设置改了采集进程无感知"的配置分裂问题。
    /// </summary>
    Task SaveCollectorSettingsAsync(CollectorSettingsDto settings);

    /// <summary>同步全部配方（Remote 模式：MainAPP 配方管理保存时推给 Collector 落盘 recipes.json）。
    /// 参数用 List&lt;RecipeDto&gt;：MessagePack 对 IReadOnlyList&lt;T&gt; 只读包装无 formatter，SignalR 会序列化失败。</summary>
    Task SaveRecipesAsync(List<RecipeDto> recipes);

    /// <summary>
    /// 下发配方到指定设备（Remote 模式：写 PLC 由持有连接的 Collector 执行，返回逐项结果，失败已回滚）。
    /// </summary>
    Task<RecipeApplyResultDto> ApplyRecipeAsync(string deviceId, string recipeId);

    /// <summary>
    /// 一键清零全部设备的 OEE（Remote 模式：PLC 清零触发位由持有连接的 Collector 写入，
    /// 软件侧产量/时间/报警累计与产量基线同时在 Collector 侧清零）。
    /// 危险写操作：调用方负责二次确认；返回写入成功的设备数与参与总数。
    /// </summary>
    Task<OeeResetAllResultDto> ResetAllOeeAsync();
}
