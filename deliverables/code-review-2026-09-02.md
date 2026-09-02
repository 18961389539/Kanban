# Kanban 代码审查报告

- **日期**：2026-09-02
- **范围**：工作区未提交改动（41 文件，排除本地化后约 800 行真实业务改动）+ 既有架构与热路径
- **基线**：`c2691c3` Defer work order page heavy work to navigation lifecycle
- **编译状态**：`dotnet build MainAPP/MainAPP.csproj` → **0 警告 0 错误**
- **验证方式**：P0 级结论全部逐行核对源码；P1/P2 为抽样核对 + 全仓 grep 交叉验证

---

## 一、总结论

当前代码库整体工程质量**高于平均水平**，尤其在事件退订纪律、采集循环异常隔离、MVVM 分层这三件事上做得扎实（详见第六节）。本次未提交的性能优化改动质量很高，没有引入新 bug。

但有 **4 个 P0** 集中在同一类问题上：**跨线程共享可变状态的同步契约不完整**。这类问题的共同特征是——作者在某几条路径上正确地加了锁，但漏掉了其他访问路径，于是锁形同虚设。

| 级别 | 数量 | 主题 |
|---|---|---|
| P0 | 4 | 线程安全契约缺口、原子快照被破坏、Channel 单读者契约违反 |
| P1 | 9 | 资源释放、日志风暴、内存可见性、业务逻辑下沉 |
| P2 | 8 | 冗余代码、装箱开销、算法复杂度 |

---

## 二、工作区改动定性

### 本地化大 diff 不是噪音，也无需人工 review

| 文件 | +/- | 性质 |
|---|---|---|
| `LocalizationCatalog.cs` | 1728 / 1728 | pt-BR 列由英文兜底批量替换为真实葡语译文 |
| `Localization.csv` | 1728 / 1772 | 同上 + 净删 44 条死键 |
| `Strings.pt-BR.resx` | 1728 / 1772 | 由 CSV 生成的产物 |
| `Kanban.Web/Localization.cs` | 349 / 349 | 同上子集 |

判定：**一次性批量机翻回填 + 死键清理，无逻辑风险**。三处资源同步一致性已由 `CsvLocalizationTests.cs` 把关。

> ⚠️ 建议：这类生成物改动应**单独提交**，与业务改动分离。当前混在同一个工作区里，会让 reviewer 的注意力被 3500 行噪音稀释。

### 业务改动评估（质量高）

`ProductionLineViewModel.cs`（+299）这批优化是本轮最大亮点，思路正确且实现干净：
- 脏标记 + 500ms Background 合批，把 `6N 次/帧` 的 O(N²) 重算降为一次 O(N) 遍历
- `ListCollectionView` 替代 getter 每次 `ToList()`，ItemsSource 引用恒定 → 虚拟化不失效
- `Dictionary<DeviceRuntime, LineDeviceItem>` 反查，O(N) 线性查找降为 O(1)
- `INavigationPageLifecycle` 页面不可见时停定时器、保脏标记、进入时统一 flush

`WorkOrderManagerViewModel` 的 `ComputeScheduleConflicts` 双重循环合并经逐条比对**语义完全等价**，`ObservableCollectionSyncHelper` 的 `Contains` → `HashSet` 使整趟 O(N²)→O(N)，`WorkOrderService` 批量作用域把 N 次 Upsert 塌缩成 1 次 Reset——**这几处没问题，不要动**。

---

## 三、P0 — 必须修

### P0-1 `AppSettings.Shifts` 被线程池线程无锁枚举（崩溃 / 静默错数据）

**读侧**（线程池线程）— `OverviewViewModel.cs:630`：
```csharp
await Task.Run(() => QueryData(devices, refreshVersion));
```
`QueryData` 内部 7 处直接枚举：`OverviewViewModel.cs:556, 559, 967, 1033, 1210, 1223, 1231`

**写侧**（UI 线程）— `SettingsViewModel.cs:1007-1014`：
```csharp
lock (target.ShiftsLock)
{
    target.Shifts.Clear();
    foreach (var s in source.Shifts) target.Shifts.Add(...);
}
```

**问题**：作者防住了采集线程（`PlcDataAcquisitionService.cs:1185` 有 `lock (_appSettings.ShiftsLock)`），但**漏掉了 ViewModel 的 `Task.Run` 后台查询线程**。概览页默认 60s 定时刷新，用户点"保存设置"的瞬间 `Shifts.Clear()` 与后台枚举并发 → `InvalidOperationException: 集合已修改`，或读到 `Clear()` 后、Add 完成前的中间态。

**实际表现**：异常被 `RefreshOnceAsync:642` 的 `catch (Exception ex)` 兜住 → "保存设置后概览页随机弹一次查询失败"，排查时极易误判为网络抖动。

**未加锁读取点（需一并处理）**：
`OverviewViewModel.cs:556/559/967/1033/1210/1223/1231`、`HistoryQueryViewModel.cs:318/580`（580 也在 `Task.Run` 路径上）、`HomeViewModel.cs:1322`、`AlarmCenterViewModel.cs:693`、`OeeQueryViewModel.cs:238`、`LastShiftComparisonProvider.cs:59`、`ProductionLineViewModel.cs:636`、`LineDeviceItem.cs:170`

**改法**：在 `AppSettings` 加快照访问器，把上述 13 处改为调用它：
```csharp
public IReadOnlyList<ShiftConfig> GetShiftsSnapshot()
{
    lock (ShiftsLock) return Shifts.ToList();
}
```
**不要**把 `lock` 加到 ViewModel 里——那会把同步责任扩散到 13 个调用点，下一处新代码又会漏。

---

### P0-2 `AppSettings` 非 Shifts 字段裸写 + `ConnectionProfiles` 双锁互斥失效

**写侧**（UI 线程，全程无锁）— `SettingsViewModel.CopySettings:984-1003`，16 个字段逐个赋值，只有 `Shifts` 在锁里：
```csharp
target.PlcConfig = source.PlcConfig.CreateSnapshot();
target.ConnectionProfiles = source.CreateConnectionProfilesSnapshot();  // ← 整体换引用
target.PollingIntervalMs = source.PollingIntervalMs;
target.HistoryWriteIntervalScans = source.HistoryWriteIntervalScans;
target.PlcBatchReadMaxLength = source.PlcBatchReadMaxLength;
target.PlcBatchReadMaxGapSlots = source.PlcBatchReadMaxGapSlots;
```

**读侧**（采集线程，每轮都读）：
`PlcDataAcquisitionService.cs:536`（`HistoryWriteIntervalScans`）、`:632`（`PollingIntervalMs`）、`PlcScanPipeline.cs:188/226/234`（Batch 参数）

**最严重的是 `ConnectionProfiles`**：`PlcRuntimeSession.RefreshFromSettings:311` 在**自己的 `_sync` 锁**内 `foreach (var profile in _settings.ConnectionProfiles)`，而该方法由每采集周期都调用的 `EnsureConnectedAll()` 触发。`_sync` 与 UI 线程写侧**根本不是同一把锁 → 两把锁不提供任何互斥**。

`ApplicationStartupCoordinator.cs:179` 确认本地模式下采集服务与 UI **同进程**，此竞态 100% 可达。后果：集合修改异常，或某个 profile 被静默跳过采集。

**改法**：
1. `AppSettings` 增加 `public object UpdateLock { get; } = new();`
2. `CopySettings` 全部写入包进 `lock (target.UpdateLock)`
3. `PlcRuntimeSession.RefreshFromSettings` 在**进入 `_sync` 之前**先 `lock (_settings.UpdateLock)` 取快照

> ⚠️ **锁顺序**：`UpdateLock` 必须在 `_sync` 外层，且取完快照再进 `_sync`，否则 A-B-BA 死锁。

---

### P0-3 `CreateDeepSnapshot` 锁外序列化 → 深拷贝不再是原子快照

`Kanban.Collector.Core/Data/DeviceRepository.cs:495-506`：
```csharp
lock (_collectionLock)
{
    foreach (var device in Devices) NormalizeChildIds(device);
    snapshot = Devices.ToList();      // ← 只拷引用！
}
var json = JsonSerializer.Serialize(snapshot, JsonOptions);   // ← 锁外遍历对象图
```

**问题**：`Devices.ToList()` 拷的是**引用**，序列化时遍历的仍是采集线程 / `RemoteRuntimeSink` 正在写的同一批 `Device` 对象及其 `Alarms`/`Defects`/`Sources` 子集合。锁外序列化会：
- 抛 `InvalidOperationException: Collection was modified`
- 或产出**半新半旧**的 JSON

调用点 `DeviceRepository.cs:121`（远程推送）会把这份错配置经 `_remoteStore.SaveDevicesAsync` **写成 Collector 的权威配置**——错误被固化到磁盘，且不会有任何异常提示。

原实现"锁内双次 JSON"慢，但换来了真正的原子性。保存是低频操作，几十 ms 完全可以接受，**用锁持有时间换正确性这笔交易不划算**。

**改法**（把锁内耗时从两次序列化降为一次）：
```csharp
private List<Device> CreateDeepSnapshot()
{
    string json;
    lock (_collectionLock)
    {
        foreach (var device in Devices) NormalizeChildIds(device);
        json = JsonSerializer.Serialize(Devices, JsonOptions);   // 锁内只保留这一次
    }
    // 反序列化是纯 CPU、不触碰 Devices，放锁外
    return JsonSerializer.Deserialize<List<Device>>(json, JsonOptions) ?? [];
}
```
> 注意：`SaveAll():197-211` 用的是同一个"锁内浅拷 + 锁外序列化"模式（既有代码，非本次引入）。注释里写的"与 SaveAll 的锁内浅拷贝模式一致"说明作者是有意对齐的，**但这个范本本身就是错的**，建议一并修，别让新代码去对齐一个错误模板。

---

### P0-4 `DefectHistoryStore.Flush()` 违反 `SingleReader` 契约

`Kanban.Collector.Core/Services/DefectHistoryStore.cs:27` 声明 `SingleReader = true`，但：
```csharp
// :61-65
public void Flush()
{
    while (_channel.Reader.Count > 0)
        FlushPendingAsync(CancellationToken.None).GetAwaiter().GetResult();
}
```
`Flush()` 由**任意调用线程**执行（测试、停机路径），与 `FlushLoopAsync` 后台线程并发 `TryRead`。`SingleReader=true` 走无锁快路径，并发读会导致**丢项、重复投递、内部索引损坏**——且这类损坏是静默的，不抛异常。

**次生问题**：`Flush()` 只保证"通道已空"，不保证"已落库"。竞争窗口内后台循环已 `TryRead` 走数据、正在 `SaveChangesAsync`，此时 `Count == 0`，`Flush()` 立刻返回，调用方断言时数据还没进 SQLite → `DefectHistoryStoreTests`（`:33`、`:83`）**间歇性失败**。

**改法**：
```csharp
private readonly SemaphoreSlim _flushGate = new(1, 1);

public void Flush()
{
    _flushGate.Wait();
    try
    {
        while (_channel.Reader.Count > 0)
            FlushPendingAsync(CancellationToken.None).GetAwaiter().GetResult();
    }
    finally { _flushGate.Release(); }
}
```
`FlushLoopAsync` 中 `FlushPendingAsync` 调用处同样包 `_flushGate.WaitAsync(ct)`。若嫌 `SingleReader` 的契约难以维持，直接改 `SingleReader = false`（代价是每次读多一次 interlocked）。

---

## 四、P1 — 应该修

| # | 位置 | 问题 | 改法 |
|---|---|---|---|
| P1-1 | `DefectHistoryStore.cs:124-131` | `Dispose` 只 `Cancel()`，没有 `Writer.TryComplete()`，也没设拒绝路径 → Dispose 后 `Append` 的 `TryWrite` **仍返回 true**，数据进通道但无人消费，**静默丢失** | 加 `_disposed` 检查 + `LogWarning`；`Dispose` 补 `TryComplete()` |
| P1-2 | `DefectHistoryStore.cs:52-56` | 通道溢出时**每条**丢弃记录都 `LogWarning`。真溢出时（磁盘满/DB 锁死）每秒数百条，把磁盘和 I/O 拖垮——与本次优化目标正好相反 | 采样：`if (n == 1 \|\| n % 1000 == 0)` 才记 |
| P1-3 | `DefectHistoryStoreTests.cs:27` 等 | `new DefectHistoryStore(...)` 无 `using`、无 `Dispose`，而 `finally` 块（`:46-50`）会 `Directory.Delete(root, true)`。后台线程可能在删除**之后**再建 DbContext → 删除抛 `IOException`（被 catch 吞）→ **测试污染 + 假通过** | 改 `using var store = ...`；`PlcToHistoryIntegrationTests.cs` 的 7 处未 `using` 的 `new HistoryService(...)`（`:168/265/314/377/439/511/572`）一并处理 |
| P1-4 | `BulkObservableCollection.cs:28, 35-36, 59` | `_mutated` 是普通字段无内存屏障，而类文档（`:19-21`）要求"调用方须持 SyncRoot 锁"，但两个调用方**都没做到**：`WorkOrderService.cs:591`、`RemoteRuntimeSink.cs:572` | 最小改动 `volatile bool`；更彻底的是让 `BeginBulkUpdate` 自己 `Monitor.Enter(SyncRoot)`，把文档约定变成代码保证 |
| P1-5 | `PlcDataAcquisitionService.cs:84-132` | ①`_profileFailureRounds` 只增不清理，档案删除后条目永久残留；②进冷却时计数清零（`:110`）意味着重试失败后要**再失败 3 轮**才重新冷却，故障 PLC 有一半时间每轮都付完整超时 | 末尾按 `GetConfiguredReadProfileIds()` 差集清理；进冷却时**保留**计数不清零 |
| P1-6 | `PlcRuntimeSession.cs:32-45` | `_signature` + `_signatureVersion` 双字段非原子写入，并发读者可能读到"新 version + 旧 signature"→ **跳过 `session.Configure()` 用旧配置连 PLC**。当前三处访问点（`:168/307/322`）都在 `lock (_sync)` 内所以安全，但这是**隐性未写进注释的契约** | 合成 `volatile (long Version, string Signature)? _sig` 单引用原子发布 |
| P1-7 | `PlcAddressParser.cs:220-232` + 4 个 codec | `ClearCache()` 注释称"配置变更后必须调用，否则旧地址结果会被沿用"。但 `Parse` 是**纯函数**（键是输入本身，输入变了键就变），**不存在旧结果被沿用的可能**。整套机制（4 个 `ClearParseCache`、5 个静态字典、3 处调用点）是为不存在的问题付出的复杂度 | 保留一个用于内存回收的 `ClearCache()` 但改正注释；或干脆整套删除，少 60 行 |
| P1-8 | 6 处 ViewModel 内同步文件 IO | `HistoryQueryViewModel.cs:542/556`、`RuntimeMonitoringViewModel.cs:344`、`DeviceManagerViewModel.cs:843/867`、`WorkOrderManagerViewModel.cs:1082` 在 **UI 线程同步读写盘** | 项目内已有正确范式：`OverviewViewModel.cs:315` 的 `await Task.Run(() => File.WriteAllText(...))`。抽 `IFileExportService` 注入，顺带消除 6 处重复的 `try/catch + NotifyError` 样板 |
| P1-9 | 巨型 VM 中的业务逻辑 | `OverviewViewModel.QueryData`（683-1071，**389 行**，占该文件 26%）、`AlarmCenterViewModel.RefreshStats`（736-895，160 行）、`SettingsViewModel.Validate`（1018-1076，59 行业务规则）、`WorkOrderManagerViewModel` 的 7 个 static 业务判定（731-769） | 分别抽 `IOverviewDashboardService` / `IAlarmStatisticsService` / `SettingsValidator`（项目里已有 `ShiftValidator`、`RecipeValidator` 同款模式）/ `WorkOrderRules`。详见第五节 |

---

## 五、巨型 ViewModel 拆解建议（P1-9 展开）

不是所有大文件都是坏味。样板占比实测：

| 文件 | 总行 | 属性/命令样板 | 判定 |
|---|---|---|---|
| `HomeViewModel` | 1340 | 120 | ✅ **主要是属性样板，可原谅** |
| `DeviceDetailViewModel` | 1162 | 81 | ✅ 主要是样板，且校验/审计/图表已外包 |
| `DeviceManagerViewModel` | 1199 | 21 | ⚠️ 业务逻辑为主，但分层是对的 |
| `OverviewViewModel` | 1490 | 58 | ❌ **业务逻辑撑起来的，该抽** |
| `WorkOrderManagerViewModel` | 1311 | 76 | ❌ 业务逻辑 + 7 个 static 判定 |
| `SettingsViewModel` | 1085 | 11 | ❌ **样板最少，几乎纯业务** |
| `AlarmCenterViewModel` | 1014 | 53 | ❌ 统计逻辑 160 行 |

**最高优先级：`OverviewViewModel.QueryData`（683-1071）**

内含：按小时桶聚合、基线差分、状态时长、报警配对、OEE 三率、全厂加权 OEE、MTBF、峰值谷值（1318-1344）、缺陷帕累托（1345-1388）、复盘结论（1389-1471）。

抽法：
```csharp
// 新建 IOverviewDashboardService
Task<OverviewDashboardResult> BuildAsync(
    IReadOnlyList<Device> devices, DateTime from, DateTime to, CancellationToken ct);
```
VM 只保留 `ApplyKpis()`（905-949）+ 8 行 `ObservableCollectionSyncHelper.Sync`（1010-1017）+ 图表签名比对。
`_metricsService`/`_analysisService`/`_reviewDataService`/`_chartService` 四个依赖随之从 VM 消失，**VM 降到约 600 行**。

---

## 六、P2 — 可选

| # | 位置 | 问题 | 改法 |
|---|---|---|---|
| 1 | `HistoryQueryViewModel.cs:402-421` | 手写了 20 行 `UiDispatcher.Dispatch` 的等价物，注释里自己写着"与 HomeViewModel/OverviewViewModel 同模式"——明知重复仍复制 | 换成 `UiDispatcher.Dispatch(() => {...})`，20 行 → 4 行 |
| 2 | `AlarmCenterViewModel.cs:484-492` | 内联重写了 `DeviceFilterHelper.Refresh`，且多了个 `OrderBy(d => d.Name)` → **与其余三页排序不一致**（隐性 UI 不一致 bug） | 改用共享助手，统一排序口径 |
| 3 | `HomeViewModel.cs:515` vs `1329-1339` | **全仓唯一的只订阅不退订**：`_liveTimer.Tick += OnLiveTimerTick` 无对应 `-=`。其余 4 个 DispatcherTimer 全部对称（AlarmCenter:439、ProductionLine:661、DataSourceMonitoring:899、RuntimeMonitoring:871）。单例 + 关停才 Dispose，实际影响可忽略，但 Stop() 后已排队的 Tick 可能访问已 Dispose 的 `_lastShiftProvider` | 补一行 `_liveTimer.Tick -= OnLiveTimerTick;` |
| 4 | `RemoteAuditService.cs:95, 118` | `.ConfigureAwait(false).GetAwaiter().GetResult()` 包装 SignalR 调用，**服务本身无守卫**。当前不爆只因唯一调用方套了 `Task.Run`。对比 `RemoteHistoryService.cs:533` 有注释并包了 `Task.Run` | 照抄 `WorkOrderService.cs:177-182` 的 `EnsureSyncApiAllowed()` 范式 |
| 5 | `DefectHistoryStore.cs:73-82` | `WaitToReadAsync` 外层套 `WaitAsync(1s)`，`catch (TimeoutException)` 分支里的 `if (reader.Count > 0)` 是**死代码**（超时时 Count 恒为 0） | 删掉 `WaitAsync` 和 `TimeoutException` 分支，简化循环体 |
| 6 | `ObservableCollectionSyncHelper.cs:52` | `Func<T, object> identityKey`，调用方传 5 元 ValueTuple → **每次装箱 + 装箱哈希** | 改泛型 `ReuseExisting<T, TKey>(..., Func<T, TKey> key, IEqualityComparer<TKey>? cmp)` |
| 7 | `ObservableCollectionSyncHelper.cs:64-79` | `ReuseExisting` 遇到 `source` 中同键重复项（同设备同名同等级同 EventTime 的重复报警是可能的），会把二者都替换为**同一实例** → UI 出现重复行 | `existingByKey` 改为 `Dictionary<object, Queue<T>>`，命中即 `Dequeue` |
| 8 | `WorkOrderManagerViewModel.cs:740-747` | `ComputeScheduleConflicts` O(n²)（双层 for + break，剪枝有效） | 先 `OrderBy(PlannedStart)` 后单趟扫描维护最大 PlannedEnd，O(n log n)。当前量级非瓶颈 |

---

## 七、明确做得好的部分（不要改）

1. **MVVM 纪律干净** — `MainAPP/ViewModels/` 全目录扫描：`MessageBox.` **0 处**、`Dispatcher` **0 处**、`new Window/ShowDialog` **0 处**。所有弹窗走 `IDialogService`。

2. **事件退订纪律优秀** — 30+ 处订阅只有 1 处漏（P2-3）。`DirtyTracker`、`DeviceDetailViewModel` 双重退订、`DeviceManagerViewModel`、`ProductionLineViewModel`、`SettingsViewModel` 全部对称。且统一用**命名方法**而非 lambda，`HomeViewModel.cs:496` 有注释解释原因（lambda 每次新建委托实例，`-=` 不生效）——这是有意识的工程决策。

3. **采集循环不会因为异常死掉** — `PlcDataAcquisitionService.cs:604-628` 只重抛 OOM / AppDomainUnload / ThreadAbort，其余记日志后进下一轮；四组扫描各自套 `TryScan`（675-719）二次隔离，通信异常触发 `MarkCommunicationFailures()`，业务异常只记日志。`PlcScanPipeline` 内各 Scan 的 `catch when (IsCommunicationException(ex))` 与之配合良好。

4. **空 catch 全仓仅 4 处，且全在无副作用路径** — `ProductionHistoryWriter.cs:57`、`HistoryStorageDiagnostics.cs:76`、`PlcDataAcquisitionService.cs:340`、`AuditService.cs:284`。**不构成问题**。

5. **`PlcRuntimeSession` 锁粒度正确** — `RefreshFromSettings`（294-341）在 `lock (_sync)` 内只做字典增删，**把 Dispose 挪到锁外**（337-340），避免持锁做 IO。

6. **采集核心零 `async void`、零裸 `.Result`/`.Wait()`** — `PlcDataAcquisitionService` / `PlcScanPipeline` / `PlcRuntimeSession` 三文件 grep 均为 0。`StopAsync` 有 15s 超时保护且超时后刻意不 Dispose cts（326-336 注释说明轮询线程可能仍在使用）。

7. **SQL 优化到位** — `DefectHistoryStore.QueryWindowBounds` / `QueryHourlyBounds` 用原生窗口函数替代 EF `GroupBy+First` 翻译（EF 在 SQLite 上生成相关子查询，2 天 27 万行实测 6.4s → 窗口函数 1.1s）。`QueryDefectSnapshotsPaged` 加了 `ThenByDescending(Id)` 稳定次级键，注释明确说明原因。

8. **`BulkObservableCollectionTests.cs`** — 6 个用例覆盖核心契约，尤其 `BulkScope_ExceptionInsideScope_StillRaisesResetOnDispose` 考虑到了 `using` 的 finally 语义。**缺一个并发用例**（对应 P1-4），建议补。

9. **`WorkOrderRepository.cs:105-118 / 329-343`** — 两处批量作用域包裹正确，且**外层都持有 `_collectionLock`**，符合 `BulkObservableCollection` 的锁约定（对比 `WorkOrderService`/`RemoteRuntimeSink` 就没做到）。

---

## 八、修复建议顺序

| 序 | 项 | 预估 | 理由 |
|---|---|---|---|
| 1 | **P0-3** `CreateDeepSnapshot` 序列化挪回锁内 | 5 分钟 | 改动最小（挪一行），但风险最高（可能写坏 Collector 权威配置） |
| 2 | **P0-1** `GetShiftsSnapshot()` | 1 小时 | 13 个调用点，但改动机械、收敛在一个文件内 |
| 3 | **P0-4** `DefectHistoryStore.Flush` 加闸门 | 30 分钟 | 消除测试间歇失败 |
| 4 | **P0-2** `AppSettings.UpdateLock` | 2 小时 | 收益大但需仔细处理锁顺序（UpdateLock 必须在 `_sync` 外层） |
| 5 | **P1-1 ~ P1-4** DefectHistoryStore 与 BulkObservableCollection 相关 | 2 小时 | 一组相关改动，建议一起做 |
| 6 | **P1-9** `OverviewViewModel.QueryData` 抽服务 | 半天 | 收益最大但改动面大，建议配套单元测试 |
| 7 | 其余 P1 → P2 | 酌情 | |

> 建议 P0-3 单独一个 commit 先合，它是一行改动的高性价比修复。
