# Kanban 全解决方案代码审查报告

- 日期：2026-09-05
- 基线：`7c0dd6e`（工作区另有 12 个文件的未提交改动，已一并纳入）
- 范围：MainAPP / Kanban.Collector(.Core) / Kanban.Contracts / Kanban.Client / Kanban.Web / PlcSimulator / 测试工程 / 工程与依赖卫生
- 方法：模式扫描（Grep/Glob）→ 逐文件开读确认上下文 → 关键判断用 .NET 运行时源码或交叉引用二次验证

> 说明：本仓库此前已多轮 review，代码中大量 `审查修复 2026-xx-xx` 注释表明历史项已闭环。本报告只列**当前仍然真实存在**的问题。

---

## 一、结论速览

| 严重度 | 数量 | 一句话 |
|---|---|---|
| **P0** | 1 | Local 模式下产线页后台线程直接操作 DispatcherTimer，必抛异常 |
| **P1** | 6 | 缺陷快照静默丢失且监控不可见、MessagePack 顺序耦合、分层破坏、两处越界、双轮询并发、测试 flaky |
| **P2** | 24 | 资源/生命周期/性能/契约/可观测性/工程卫生 |

---

## 二、P0 — 必须立即修复

| # | 位置 | 问题 | 后果 | 修复建议 |
|---|---|---|---|---|
| P0-1 | `MainAPP/ViewModels/ProductionLineViewModel.cs:386-413`（订阅点 `:346`，集合声明 `:96`，定时器 `:73/:89`） | **后台线程直接访问 DispatcherTimer + 非线程安全集合**。`OnRuntimePropertyChanged` 订阅 `item.Runtime.PropertyChanged`，而 Runtime 属性在 PLC 采集后台线程被修改。该处理器未做任何线程封送，直接执行 `_dirtyTransientItems.Add(item)`（`:410`）与 `_kpiTimer.IsEnabled` / `_kpiTimer.Start()`（`:413`）。而 `PageRefreshTimer.IsEnabled` 就是 `DispatcherTimer.IsEnabled`（`MainAPP/Helpers/PageRefreshTimer.cs:38`），跨线程访问必抛 `InvalidOperationException` | Local/PLC 模式下每个采集周期每台设备触发 3~7 次 → **后台线程持续抛异常，KPI 永不刷新**；同时 `_dirtyTransientItems`（普通 `HashSet`）与 UI 定时器 tick（`:126-129` 读取并 `Clear()`）并发读写，存在集合损坏风险。Remote 模式因快照经 `Dispatcher.InvokeAsync` 落地而不触发，故该缺陷**只在 Local 模式暴露** | 入口整体封送：`UiDispatcher.Dispatch(() => { _dirtyTransientItems.Add(item); if (!_kpiTimer.IsEnabled) _kpiTimer.Start(); })` |

**同类对照证据（证明这是遗漏而非设计）**：同一订阅在 `MainAPP/ViewModels/DeviceDetailViewModel.cs:527-533` 有完全对称的实现，且带明确注释：

```csharp
// Runtime 属性在 PLC 采集后台线程被修改，必须切回 UI 线程才能更新绑定集合
if (_kpiRefreshScheduled) return;
_kpiRefreshScheduled = true;
DispatchOnUi(() => { ... });
```

两处订阅同一个事件、同一个 Runtime 对象，一处封送一处不封送 —— 直接比对即可确认 P0-1 是缺陷。

---

## 三、P1 — 明显缺陷，本迭代应修

| # | 位置 | 问题类别 | 问题描述 | 后果 | 修复建议 |
|---|---|---|---|---|---|
| P1-1 | `Kanban.Collector.Core/Services/DefectHistoryStore.cs:23-28`（`FullMode = DropOldest`）+ `:67-74`（溢出分支） | 数据丢失 / 可观测性 | **溢出告警是死代码，缺陷快照静默丢失且监控完全不可见**。`BoundedChannel` 在 `DropOldest` 下 `TryWrite` **恒返回 true**（丢弃最旧项后入队成功），因此 `:67` 的 `if (!TryWrite)` 分支永不执行，`_overflowCount` 恒为 0，`:73` 的告警日志永不输出。且通道未注册 `itemDropped` 回调 | 缺陷历史（质量追溯关键数据）在通道满时被静默丢弃，运维侧零感知、零指标 | 注册 `itemDropped` 回调计数告警（参照 `AuditService.cs:46-52` 的二参构造 + `OnEntryDropped`）；或改 `Wait` 模式 + 转存恢复文件 |
| P1-2 | `Kanban.Contracts/**` 全部 DTO（无 `[MessagePackObject]`/`[Key]`）+ `Kanban.Collector/Program.cs:127`、`Kanban.Client/KanbanDataClient.cs:115` | 契约兼容性 | **MessagePack Contractless：wire 顺序 = C# 声明顺序**。全仓 `grep '\[Key('` 零命中，两端均用 `ContractlessStandardResolver` | 任一端先升级、在 DTO 中间插入/删除/重排字段 → 数组索引仍对齐但**值静默错位**，不抛异常。多端独立部署下是高危演进陷阱 | 跨端 DTO 显式 `[MessagePackObject(true)]` + `[Key(n)]` 固定索引；或约定"只追加到末尾"并加契约版本号 |
| P1-3 | `MainAPP/MainAPP.csproj:107` + `MainAPP/App.xaml.cs:11-13` | 分层破坏 | 桌面客户端直接 `ProjectReference → Kanban.Collector.Core`，并 `using Kanban.Collector.Core.Data`（EF 持久层）、`.Services`、`.Models` | 客户端耦合服务端持久层（含 EF Migrations），绕过 `Kanban.Client` 抽象；重构与独立部署受阻 | 共享模型收敛到 `Kanban.Contracts`，MainAPP 仅经 `Kanban.Client` 通信 |
| P1-4 | `MainAPP/Services/RemoteHistoryQueryService.cs:438`、`:472` | 数组越界 | `result[chunk[i]] = MapDtos<ProductionLog>(response.Results[i])` 与 `response.Results[i]` **未校验 `Results.Count`** 即按下标索引 | 服务端对批量子查询返回条数不足（截断/部分失败）时抛 `IndexOutOfRangeException`，中断工单产量聚合回退路径 | 与同文件 `:724`（`if (response.Results.Count != chunk.Length) throw ...`）保持一致，先校验数量再索引 |
| P1-5 | `Kanban.Collector.Core/Services/PlcDataAcquisitionService.cs:304`（`Start`）+ `:337-359`（`StopAsync` 超时分支） | 生命周期 / 并发 | `StopAsync` 等待轮询任务 15s 超时后仅 `LogWarning` 即返回，此时 `IsRunning` 已置 `false`、轮询任务仍卡在同步 PLC IO 未退出。此后再次 `Start()` 时 `:304` 的 `if (IsRunning) return;` **不再拦截** → 新建第二个轮询任务，与旧循环**双轮询并发** | 两个循环并发写同一批 `DeviceRuntime` 与历史库 → 计数翻倍、OEE 失真 | 超时后置 `IsStopping/StartForbidden` 标志阻止重入；或保留 cts 引用、待任务真正完成后再允许启动 |
| P1-6 | `MainAPP.UIAutomation`（77 处）、`MainAPP.Tests`（37 处）、`MainAPP.E2E`（2 处） | 测试质量 | 共 **116 处** `Thread.Sleep` / `Task.Delay` 轮询式等待 | 计时依赖 → 间歇性失败、CI 不稳定、失败难复现 | 改用 `Wait.Until(condition, timeout)` 轮询或测试假信号源 |

---

## 四、P2 — 坏味与可维护性

### 4.1 线程与生命周期

| # | 位置 | 问题 | 建议 |
|---|---|---|---|
| P2-1 | `PlcDataAcquisitionService.cs:309` | `Task.Run(() => PollingLoopAsync(cts.Token), cts.Token)` 把 token 当**调度器取消参数**；若任务启动前被取消，委托根本不执行但 `IsRunning` 已为 `true` → 静默"假采集" | 仅传循环体参数，并加启动后状态校验 |
| P2-2 | `PlcDataAcquisitionService.cs:337-338` + `:599-603` | `StopAsync` 在调用线程遍历设备写 `StatusWord = Offline`，与 in-flight 轮询迭代并发写同一字段 | 先 Cancel 并 `await` 循环真正退出后再改状态 |
| P2-3 | `Kanban.Collector/Services/MetaPublisher.cs:63, 90-93` | `StartAsync` 返回 `Task.CompletedTask` 却 fire-and-forget 启动 `_runTask`；`RunAsync` 仅 catch `OperationCanceledException` | 按 `IHostedService` 契约 `return RunAsync(ct)`，或加兜底 catch 并记录 |
| P2-4 | `DataSourceSnapshotStore.cs:55`、`ProductionHistoryWriter.cs:46`、`SnEventStore.cs:89`、`AuditService.cs:53`、`DefectHistoryStore.cs:46` | flush 后台任务 fire-and-forget，**无 `ContinueWith(OnlyOnFaulted)` 故障观察**（与 `_pollingTask` 的处理不对称） | 补 `OnlyOnFaulted` 兜底日志 |
| P2-5 | `MainAPP/Services/ProductionDailyReportService.cs:283`（`StopAsync` `:52-67`） | `Dispose() => StopAsync().GetAwaiter().GetResult()`，而 `StopAsync` 内 `await worker.WaitAsync(3s)` **未 `ConfigureAwait(false)`**；若在 UI 线程 Dispose 则死锁 | 加 `ConfigureAwait(false)`，或在 UI 路径外释放 |
| P2-6 | `MainAPP/Services/WorkOrderService.cs:276, 297, 330, 352, 401, 437, 471` | Local 模式同步包装器在 UI 线程 `GetAwaiter().GetResult()`，是否死锁**依赖被调异步方法内部是否 `ConfigureAwait(false)`** —— 脆弱耦合 | 统一 `Task.Run(() => Core(...)).GetAwaiter().GetResult()` |

### 4.2 资源泄漏

| # | 位置 | 问题 | 建议 |
|---|---|---|---|
| P2-7 | `MainAPP/Services/RemoteRuntimeSink.cs:610-621` | `TrackTask` 只 `Add` 从不移除已完成任务，集合随运行时间无限增长 | 完成时移除，或只保留未完成任务 |
| P2-8 | `MainAPP/Views/OverviewView.xaml.cs:41-58, 72-76` | `OnDataContextChanged` 退订旧 VM 事件时**未 Stop** `_chartRefreshTimer`（仅在 `OnVmExited:74` Stop）→ 定时器残留空转 | 在旧值分支一并 `Stop()` |
| P2-9 | `MainAPP/ViewModels/HistoryQueryViewModel.cs:684, 692` | CTS 重赋值/置空前只 `Cancel()` 未 `Dispose()` | 重赋值前 `_autoQueryCts?.Dispose()` |
| P2-10 | `MainAPP/ViewModels/DeviceWorkOrderViewModel.cs:144, 153, 157` | `cts` 在取消/早返回路径未 Dispose | `try/finally` 包裹 |
| P2-11 | `PlcSimulator/SimLog.cs:43-58` | `Initialize` 重复调用时覆盖 `_logWriter` 未释放旧实例 | 开头先 Dispose 旧实例 |

### 4.3 性能

| # | 位置 | 问题 | 建议 |
|---|---|---|---|
| P2-12 | `DataSourceSnapshotStore.cs:212-296` | `ReplayRecoveryAsync` 每批处理**整文件读两遍 + 全量重写**，循环至扫完 → O(N²)。对比 `ProductionHistoryWriter.cs:221-348` 的 O(N) 实现，二者不一致 | 一次读入内存、内存切片、仅末次原子回写 |
| P2-13 | `ProductionHistoryWriter.cs:143-164, 394-399` | `FlushPendingAsync`/`InsertBatchIdempotent` 是 `async` 却用同步 `BeginTransaction`/`SaveChanges`/`Commit`，且**完全忽略 `ct`** | 改 Async API 并透传 ct |
| P2-14 | `MainAPP/ViewModels/DataSourceMonitoringViewModel.cs:648, 689` | 对 `Queue<T>` 调 `.Last()` → 全量枚举 O(n)，每数据点触发 → O(n²) | 缓存"最新时间戳"字段 |
| P2-15 | `MainAPP/ViewModels/RuntimeMonitoringViewModel.cs:844-857` | UI 线程定时器上对全部设备/地址执行 `CollectValidationErrors` + `codec.Parse` | 移到 `Task.Run` 后回 UI 赋值 |
| P2-16 | `MainAPP/ViewModels/HistoryQueryViewModel.cs:567` | 构造路径 `Task.Run(File.ReadAllText).GetAwaiter().GetResult()` 同步阻塞读盘 | 改异步加载 + `IsLoading` 占位 |
| P2-17 | `DataSourceSnapshotStore.cs:434, 447`；`DefectHistoryStore.cs:85, 89`；`SnEventStore.cs:180` | 查询路径 `FlushAsync().GetAwaiter().GetResult()` 在 HTTP 请求线程同步阻塞等 flush 闸门 | 改异步，或接受"仅返回已提交数据"去掉同步阻塞 |
| P2-18 | `PlcDataAcquisitionService.cs:1203, 1385` | 每次轮询（200ms）都加 `ShiftsLock` + `UpdateLock` 并重算配置签名 | 仅在签名实际变化时 `Configure` |

### 4.4 并发

| # | 位置 | 问题 | 建议 |
|---|---|---|---|
| P2-19 | `Kanban.Collector/Services/SnapshotPublisher.cs:56-71, 105-141`；`DeviceRepository.cs:345-349` | 500ms 定时器线程直接遍历共享可变子集合（`device.Alarms/Sources/Defects` 实时实例），与采集/配置同步的结构性修改并发 | 发布前 `.ToList()` 快照或加读锁 |
| P2-20 | `Kanban.Collector.Core/Services/WorkOrderRepository.cs:178` vs `:267-296` | `SyncMemoryCollection` 仅部分调用方加锁；Remote 模式 SignalR 回调线程（`:178`）无锁调用 | 在方法内部统一加 `_collectionLock` |

### 4.5 契约与 API 设计

| # | 位置 | 问题 | 建议 |
|---|---|---|---|
| P2-21 | `Kanban.Client/KanbanDataClient.cs:313` | `InvokeAsync<...>("GetDiagnosticsAsync", ct)` 字符串字面量，`IKanbanHubServer` 未声明该方法 | 接口声明 + `nameof(...)` 调用 |
| P2-22 | `Kanban.Client/KanbanDataClient.cs:208-236` | `OnSnapshot/OnAlarmEvent/OnStatusEvent/OnMeta/...` 返回 `void`，**不可退订** | 统一返回 `IDisposable` |
| P2-23 | `Kanban.Client/KanbanDataClient.cs:242-246` | `OnRecipeApplyProgress` 未走 `RegisterHandlerOnce` 幂等去重（与 `:208-236` 不一致） | 复用幂等机制 |
| P2-24 | `Kanban.Contracts/Enums/DeviceStatus.cs:9-12` 等 | 枚举按底层 int 序列化，中间插入/重排会使旧端得到错位值且无异常 | 只追加不重排；未知值兜底 Unknown |

### 4.6 时区

| # | 位置 | 问题 | 建议 |
|---|---|---|---|
| P2-25 | `Kanban.Web/DashboardState.cs:67,121,131,514`(Now) vs `:174,188`(UtcNow) | 同类混用本地/UTC，当前各字段不跨 Kind 比较故无 bug，但极易被后续改动写成跨 Kind 比较（差 8h）且难察觉 | 统一 UTC，用 `DateTimeOffset` 明确语义 |
| P2-26 | `Kanban.Web/Services/StatusAnalysis.cs:50,76`；`Pages/HistoryQuery.Status.cs:81`、`Alarm.cs:101`、`Oee.cs:93`、`Filters.cs:86` | 隐式假设 `EventTime`/`from`/`to` 均为 Local Kind；服务端若改存 UTC 或 WASM 处于不同时区则整体偏移 8h，无 Kind 校验报错 | 边界显式约定 Kind（建议 UTC），入口 `SpecifyKind` 或断言 |

### 4.7 可观测性与健壮性

| # | 位置 | 问题 | 建议 |
|---|---|---|---|
| P2-27 | `Kanban.Collector.Core/Services/HistoryService.cs:198-202`（Timer `:82`） | WAL checkpoint 失败仅 `LogWarning`，readiness 探针不降级 → 磁盘异常时服务仍报 Healthy | 连续失败计入 `CollectorHealthState` 使 `/health/ready` 降级 |
| P2-28 | `PlcSimulator/DeviceSimulator.cs:1991, 1999, 2007` | `TryReadInt/TryReadBool/TryReadIntNullable` `catch { return 0/false/null; }` 静默吞异常，读取失败与真实 0/false 不可区分 | 至少 Warning 一次，或保留上次有效值/标记离线 |

### 4.8 工程与依赖卫生

| # | 位置 | 问题 | 建议 |
|---|---|---|---|
| P2-29 | `Directory.Build.props:6` | `TreatWarningsAsErrors=false` | 改 `true`，已知项用 `#pragma` 显式抑制 |
| P2-30 | `lib/HslCommunication/HslCommunication.dll`（`.gitignore:9` 白名单） | 第三方库二进制 vendoring 入库（`git ls-files` 确认全仓仅此 1 个 dll 被跟踪） | 改 NuGet 或 submodule，移除白名单 |
| P2-31 | `MainAPP.Tests/MainAPP.Tests.csproj:26` | 测试工程直接引用 `Kanban.Collector` 宿主做集成测试，跨分层 | 经 `Kanban.Client`/契约接口测试 |
| P2-32 | 全仓 | **无 Metrics 管线**（`AddOpenTelemetry`/`Meter<>`/`IMetrics` 零命中）。采集吞吐、OEE 计算延迟、SignalR 连接数、落库队列深度均无指标，排障只能靠日志 | 引入 `System.Diagnostics.Metrics` + OpenTelemetry，Collector 暴露 `/metrics` |
| P2-33 | 巨型类 | `PlcDataAcquisitionService.cs` 1424 行、`HomeViewModel.cs` 1400、`WorkOrderManagerViewModel.cs` 1370、`DeviceSimulator.cs` 2071、`ChartService.cs` 1280、`DeviceDetailViewModel.cs` 1228、`DeviceManagerViewModel.cs` 1203、`SettingsViewModel.cs` 1095 | 按职责拆分（协议解析/轮询/入库/状态机）；VM 抽出服务层与子 VM |
| P2-34 | `MainAPP.UIAutomation/ExceptionPathFlowTests.cs`（4×`[Fact(Skip)]`）、`HistoryQueryFlowTests.cs:119`、`LicenseFlowTests.cs:86` | 永久 Skip、仅"手动运行时移除"，无 env 门控 → 异常路径长期零覆盖 | 改 env/特性开关可重启用，或拆到独立可选测试包 |
| P2-35 | `git diff --cached` → `MainAPP.E2E/*` | 新增 `private static readonly int[] s_visiblePageIndexes = [0..12]` 硬编码侧边栏顺序 | 改用 `NavigationPageCatalog` 顺序断言，去除魔法下标 |

---

## 五、已验证**无问题**的项（避免重复排查）

| 项 | 验证方式与结论 |
|---|---|
| `Wait` 模式通道是否阻塞生产者 | **已用 .NET 运行时源码核实**（`BoundedChannel.cs:363-435`）：`TryWrite` 在 `Wait` 模式下队列满时**直接 return false，不阻塞**（仅 `WriteAsync` 会等待）。故 `ProductionHistoryWriter:77-82`、`SnEventStore`、`DataSourceSnapshotStore` 的"溢出转存恢复文件"分支**均为有效代码，无误报**。⚠️ 反直觉点：`DropOldest`/`DropWrite`/`DropNewest` 反而使 `TryWrite` **返回 true 并静默丢数据** —— P1-1 正是栽在这里 |
| 构建产物入库 | `git ls-files`：bin/obj/cache **0 个**；dll 仅 `lib/HslCommunication.dll` 1 个（有白名单） |
| 硬编码密钥/连接串 | 全仓源码 grep `password|connectionstring|secret|apikey|token=` 排除 bin/obj 后**零命中**；`appsettings.json` 的 `CollectorHubUrl` 为空占位 |
| 敏感信息落日志 | 无命中 |
| `lock` 内 `await` | 全仓扫描**零命中** |
| 空 `catch {}` / `async void` 滥用 | 生产代码无空 catch；`async void` 仅 `App.xaml.cs:62 OnStartup`，已 try/catch + 全局 `DispatcherUnhandledException`/`AppDomain.UnhandledException` 兜底 |
| 测试占位断言 | `Assert.True(true)` / `async void` 测试方法**零命中** |
| CPM 版本一致性 | `Directory.Packages.props` 每包单一版本，无 `VersionOverride` 滥用 |
| 目标框架统一 | 全为 `net10.0` / `net10.0-windows` / `net10.0-browser`，无 net8/net9 混用 |
| 技术债标记 | 首代码 TODO/FIXME/HACK **仅 1 处**（`MainAPP.UIAutomation/HistoryQueryFlowTests.cs:130`），其余在 vendored 的 `oxyplot/` |
| 健康检查 | `Kanban.Collector/Program.cs:130-182` 三级探针（存活/存活/业务就绪）设计完善，采集停止返 503 |
| SQLite 配置 | `DatabaseProvider` 统一 WAL + `busy_timeout=5000` + `synchronous=NORMAL` + `mmap_size` + 定期 checkpoint，readiness 含写探针 |
| 连接管理 | `PlcConnectionManager` 双锁分工、指数退避 1s→30s、半连接风暴防护正确；`SharedPlcDriverRouter` 引用计数 + 固定锁顺序，无死锁 |
| Blazor WASM | 无事件泄漏、`StateHasChanged` 6 处均在定时器/回调内非渲染期、页面 `Dispose` 正确退订、`EChart.razor` JS 互操作释放正确 |
| PlcSimulator | 整体质量高，用 `Random.Shared`（线程安全）、`TcpRelay` graceful shutdown 完善、`Next(0)` 边界均有守卫 |
| 结构化日志 | 已引入 Serilog |

---

## 六、建议修复顺序

1. **P0-1**（Local 模式 KPI 全废，一处 `UiDispatcher.Dispatch` 即可闭环）
2. **P1-1**（缺陷数据静默丢失 + 监控盲区，加 `itemDropped` 回调即可）
3. **P1-4**（两处越界，照抄同文件 `:724` 的校验）
4. **P1-2 / P1-3**（契约与分层，改动面大，建议单独立项排期）
5. **P1-5**（加启动重入闸门）
6. **P1-6**（116 处 Sleep，按测试工程分批改造）
7. P2 按 4.1 → 4.2 → 4.3 顺序推进

---

## 附：审查后实施状态（2026-09-05，未提交）

| 项 | 状态 | 说明 |
|---|---|---|
| P0-1 产线页跨线程 | ✅ 已修 | `UiDispatcher.Dispatch` 整体封送；属性名分派留在调用线程做早退过滤 |
| P1-1 缺陷快照静默丢失 | ✅ 已修 | 通道改构造内创建 + 注册 `itemDropped` 回调，新增 `OverflowCount` 诊断属性 |
| P1-4 批量查询越界 | ✅ 已修 | 两处补 `Results.Count != chunk.Count` 校验（照 `:724` 范式） |
| P1-5 停止超时重入 | ✅ 已修 | 新增 `_startDisabled`/`_abandonedPollingTask` 闸门，遗留任务退出后自动解除 |
| P1-6 测试 116 处 Sleep | ⏸ 未做 | 大批机械改动，建议单独按测试工程分批推进 |
| P1-2 MessagePack 契约 | ⏸ 未做 | 全 DTO 加 `[Key]`，改动面大，建议专项排期（需同时升级所有端） |
| P1-3 分层破坏 | ⏸ 未做 | MainAPP 解耦 Collector.Core，架构级重构，专项排期 |
| P2-1 Task.Run token | ✅ 已修 | 随 P1-5 一并处理（去掉调度取消 token 第二参） |
| P2-2 停止并发写状态 | ✅ 已修 | StopAsync 改为 Cancel → await 退出 → 确认退出后才 `LogOfflineTransition` |
| P2-3 MetaPublisher | ✅ 已修 | 挂 `ContinueWith(OnlyOnFaulted)`（不能 `return RunAsync`，会卡住宿主启动） |
| P2-4 flush 任务故障观察 | ✅ 已修 | 新增 `Services/BackgroundTaskRunner.cs`，5 个 Store 统一接入 |
| P2-5 ConfigureAwait | ✅ 已修 | `ProductionDailyReportService.StopAsync` |
| P2-7 TrackTask 泄漏 | ✅ 已修 | 完成即移除；续延显式走 `TaskScheduler.Default` |
| P2-8 OverviewView 定时器 | ✅ 已修 | 旧 VM 解绑分支补 `Stop()` |
| P2-9 / P2-10 CTS 释放 | ✅ 已修 | `HistoryQueryViewModel` ×2、`DeviceWorkOrderViewModel`（改 try/finally） |
| P2-11 SimLog 句柄 | ✅ 已修 | `Initialize` 重复调用先释放旧 writer |
| P2-19 快照发布竞态 | ✅ 已修 | 组装移入 `DeviceRepository.SyncRoot`，增量判定/扇出留在锁外 |
| P2-21 GetDiagnosticsAsync | ✅ 已修 | 契约接口补声明，客户端改用 `nameof` |
| P2-12 O(N²) 回放 / P2-13 异步 SQLite / P2-14~18 | ⏸ 未做 | 恢复/IO 路径改动需配套测试；性能类收益有限，建议专项会话处理 |

**核实为误报（未改，报告中原文保留但应视为已排除）**：
- P2-20 `WorkOrderRepository.SyncMemoryCollection` 方法**内部已自带** `_collectionLock`，Remote 回调路径（`:178`）无漏锁。
- P2-23 `OnRecipeApplyProgress` 文档化返回 `IDisposable` 订阅句柄并强调"不再需要时释放"，与其它 `On*` 的全局单例语义不同是**刻意设计**。

**编译验证**：Kanban.Contracts / Kanban.Client / Kanban.Collector.Core / Kanban.Collector 四项目 `--no-restore` 编译**全绿（0 警告 0 错误）**。MainAPP 因 oxyplot 子树 SDK 需联网解析、PlcSimulator 因正在运行的进程占用 DLL，本会话无法编译——两处改动靠精读确认，请在本机执行 `dotnet build Kanban.slnx` 复核；测试工程同理需本机跑。

## 附：本次审查的一个环境限制

在本次会话的 shell 中，`APPDATA` 环境变量为空，导致 NuGet 全线失败：

```
NuGet.targets(782,5): error : Value cannot be null. (Parameter 'path1')
```

**该错误影响解决方案下全部 19 个项目，且在临时新建的空控制台项目上同样复现**，属沙箱环境变量缺失，与项目代码无关。因此本轮**未能采集到编译期 warning 统计**，需要在正常终端执行：

```bash
dotnet build Kanban.slnx -v m
```

补做编译告警扫描（结合 P2-29 的 `TreatWarningsAsErrors` 启用评估）。
