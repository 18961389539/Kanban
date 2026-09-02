# Kanban 性能改进建议

日期：2026-09-01 ｜ 范围：Kanban.Collector.Core / Kanban.Collector / MainAPP（WPF） ｜ 仅审查，未改代码

## 修复进度（2026-09-01 追加）

全部 6 项 P0 已修复：

| # | 项 | 状态 | 改动文件 |
|---|---|---|---|
| P0-1 | 产线页 KPI O(N²) 刷新链 | ✅ 已修 | `MainAPP/ViewModels/ProductionLineViewModel.cs` |
| P0-3 | PLC 地址解析零缓存 | ✅ 已修 | `PlcAddressParser.cs` + 4 个品牌 codec |
| P0-4 | 每次读写触发全局锁/JSON 序列化 | ✅ 已修 | `PlcRuntimeSession.cs` + `DeviceAdapter.cs` |
| P0-2 | 筛选/排序卡死（虚拟化失效） | ✅ 已修（第二轮） | `ProductionLineViewModel.cs` + 测试适配 |
| P0-5 | 单设备故障拖垮整轮 | ✅ 已修（第二轮） | `PlcDataAcquisitionService.cs` + `PlcScanPipeline.cs` |
| P0-6 | Remote 保存卡顿（锁内 JSON 往返） | ✅ 已修（第二轮） | `DeviceRepository.cs` |

**第二轮验证**：Collector.Core 编译 0 警告 0 错误；MainAPP 编译 0 错误；
产线页相关 4 测试类 **18 测试全绿**（ProductionLineViewModelTests + 3 个渲染类）；
采集链路 5 测试类 **156 测试全绿**（PlcDataAcquisitionService/PlcConnectionManager/AlarmStateTracker/
BaselineReset/Concurrency）。MainAPP.exe 已重建（15:29），**重启 MainAPP 生效**。

### P0-2 修复内容（筛选/排序虚拟化失效）

`FilteredLineDevices` 从"每次 getter 新 List"改为 **`ListCollectionView`**（构造时
`new ListCollectionView(LineDevices)`，单一实例绑定 ItemsSource，引用恒定 → VirtualizingWrapPanel 不重建容器）：
- 过滤：`FilterDevice` 谓词合并关键词搜索（OrdinalIgnoreCase）+ 状态筛选，Refresh 时读字段最新状态重跑。
- 排序：`CustomSort` 三个 IComparer（AlarmFirst 报警>待机>其它 + AlarmTime 降序 / OeeDesc / OutputDesc），
  `ApplySort()` 在 `DeferRefresh` 内重排；排序态数据变化经 `_filterDirty` 合批后 `Refresh()` 一次。
- `HasNoFilteredDevices` 改用 `IsEmpty`（尊重 Filter，O(1)）。
- 注意：`CustomSort` 是 `ListCollectionView` 专有属性（ICollectionView 无此成员），故属性类型用具体类。
- 测试适配：`Cast<LineDeviceItem>()` 两处（Count/Single）。

### P0-5 修复内容（故障档案冷却）

不硬改用户可配超时（默认 5000ms，范围 100-60000），改为**档案级冷却**：
- `PlcDataAcquisitionService` 新增 `UpdateProfileCooldowns()`：档案连续失败 **3 轮**进入冷却、
  **冷却 3 轮**后自动重试；成功即清零计数并解除冷却；进出冷却记 Info/Warning 日志。
- `RefreshDeviceData` 跳过冷却档案的设备（保留上一轮状态，不标记离线、不触发断线重连，
  避免诊断面板把"冷却跳过"误报成"离线"）；`noDevicesToRead` 语义兼容（全冷却时按无设备处理，不误判断线）。
- `PlcScanPipeline.PrepareDWordBatchValues(cooledDownProfileIds)` 跳过冷却档案的块读
  （不再每轮为故障 PLC 付一次完整超时 × 全部块）；可选参数，测试调用零改动。
- 组间并行（Parallel.ForEachAsync）**本次未引入**：当前环境单 PLC 零收益 + 共享批读缓存并发风险，
  留作多 PLC 场景的 P1 项。

### P0-6 修复内容（Remote 保存卡顿）

`CreateDeepSnapshot` 锁内仅浅拷贝（`Devices.ToList()` + NormalizeChildIds），JSON 序列化/反序列化移出锁，
与 `SaveAll` 的既有模式一致；消除锁内 MB 级 JSON 双次往返对 `EnsureRuntime`/`GetDevicesSnapshot`
（RemoteRuntimeSink 每 500ms 批量应用）的阻塞。

### P0-1/P0-3/P0-4 修复内容（第一轮）

1. **KPI getter 去 LINQ 化**：17 个 getter 从每次 O(N) 的 `Count/Sum(闭包)` 改为读取缓存字段；
   新增 `RecalculateKpis()` 单次 O(N) `for` 循环一次累加状态计数 + 产量 + 加权 OEE + 合格率
   （`_runningCount` 等 9 个字段缓存）。
2. **事件合批**：`OnRuntimePropertyChanged` 只置 `_kpisDirty`/`_filterDirty`/`_dirtyTransientItems` 脏标记，
   由 `DispatcherTimer(DispatcherPriority.Background, 500ms)` 合并后一次 `FlushPendingRefresh()` 重算+批量通知；
   页面不可见时停表、脏标记保留，`OnPageEnter` 时统一 flush。
3. **瞬态刷新 O(1) 反查**：`NotifyItemTransient` 的 O(N) `FirstOrDefault` 换成 `Dictionary<DeviceRuntime, LineDeviceItem>`
   `_runtimeToItem`（增删/Reset/Dispose 同步维护）。
4. 语义不变：状态计数仍为四态精确等值匹配；`DeviceCount` 保持直接读 `LineDevices.Count`；
   设备增删低频路径仍同步 `RefreshSummaryKpis()`。

### P0-3 修复内容（地址解析记忆化）

- `PlcAddressParser.Parse` 外包 `ConcurrentDictionary<string, PlcAddressParseResult>`（Ordinal 键，原始串命中），
  原逻辑移到 `ParseCore`；`GetOrAdd` 命中后零 Trim/ToUpper 分配、零正则。
- 四个独立正则实现的品牌 codec（Siemens / ModbusTcp / Omron / Keyence）同样加静态 `ParseCache`；
  Mitsubishi 委托 `PlcAddressParser` 自动受益。
- 失效入口统一：`PlcAddressParser.ClearCache()` 聚合清空全部品牌缓存；挂载点：
  `DeviceRepository.ReplaceStateLocked`（覆盖 LoadAll/ReplaceAll/ReplaceAllAndSave）、
  `SaveAll` / `SaveAllAsync`（覆盖 UI 直接改内存后保存）、`AppSettings.Save()`（覆盖连接档案/品牌变更）。
- `CountConfiguredReadOperations` 每轮的全量 Parse 由正则链降为字典命中，无需再加失效复杂的计数缓存。

### P0-4 修复内容（session 锁 + JSON）

- `PlcRuntimeSession.ConfigurationSignature` 按 `Profile.Version` 缓存（Version 仅 Refresh 递增，
  Config 是不可变快照 → 缓存安全），消除 manager 每次比较时的全对象 JSON 序列化。
- `PlcDeviceAdapter.RuntimeProfile/RuntimeDriver` 从"每次读写都 `sessionManager.Get()`（全局锁 + 配置查找）"
  改为字段级缓存：session/driver 引用 `??=` 缓存一次，profile 按 Version 失效。
  adapter 经 `DeviceAdapterResolver._profileBindings` 按档案缓存（长生命周期），缓存安全。
  热路径（每次 PLC 读写）从"2 次全局锁 + 2 次 JSON 序列化"降为"0 次"。

---

## 修复后复检（2026-09-01 16:27，第二轮检查）

### 运行时状态（PlcSimulator PID 49916 + MainAPP PID 49848，15:42 重启后）

| 观察项 | 结果 |
|---|---|
| 采集运行 | ✅ 8 台设备正常，无 IO 超时/批读回退/SQLite busy/慢日志 |
| 日志告警 | 仅 AutoMapper license 警告（无关）；0 条性能相关 WRN/ERR |
| production_logs.db | 98MB，WAL 正常 |
| **defect_history.db** | ⚠️ **650MB / 309 万行**（`DefectSnapshots`） |

### 缺陷库膨胀分析（新发现）

- **97% 的行来自 8/02-8/06 爆发期**（单日最高 126.7 万行），8/07 后回归正常（每日数百~数千行），
  当前每日约 500-3000 行 → **650MB 是历史遗留，非当前运行问题**。
- 保留期 365 天（`DefectHistoryStore.CleanupOldSnapshots(365)`，启动时由 `HistoryService` 执行）——库会持续累积。
- 风险关联：**P1-9 未修**——`DefectHistoryStore.Append`（:20-28）在采集线程同步 `new DbContext + SaveChanges`，
  与后台 flush 抢 SQLite 写锁；库越大 SaveChanges 越慢，采集线程被拖累的隐患随库膨胀放大。
- 建议（P2 级）：8/02-8/06 的 ~300 万行如无业务价值可 `DELETE` + `VACUUM` 压缩（一次性瘦身）；
  长期考虑把保留期从 365 天按业务下调。

### 修复验证复核（6 项 P0）

- 编译：Collector.Core / MainAPP / MainAPP.Tests 全部 0 错误。
- 测试：产线页 4 类 18 全绿；采集链路 5 类 156 全绿。
- 运行：新版本已重启生效（15:42），采集正常、UI 无卡顿日志。

### 剩余瓶颈（P0 清零后）

**P1（7 项）**：快照拷贝 ×11/轮 ｜ 推送先建 DTO ｜ **缺陷同步落库（与 650MB 库交互）** ｜ 计划签名重建 ｜
筛选深扫+优先级 ｜ 监控页分配风暴 ｜ HomeViewModel 定时器优先级（一行）。

**P2（10 项）**：Task.Delay 漂移 ｜ 诊断排序 ｜ 每轮 HashSet ｜ 复合键拼接 ｜ ScanPipeline ToList ｜
校验错误缓存 ｜ RelativeSource 上溯 ｜ Overview 图拆装 ｜ WrapPanel 二次布局 ｜ 启动同步加载。

### 建议下一步

1. **P1-13 定时器优先级**（一行改动，零风险）；
2. **P1-9 缺陷落库异步化**（Channel + 后台批写，与 ProductionHistoryWriter 同构）——同时缓解 650MB 大库写入拖累采集线程；
3. 缺陷库一次性 `VACUUM` 瘦身（需用户确认 8 月初数据无价值）。

---

## 建议实施（2026-09-01 16:33-16:45，第三轮）

| # | 项 | 状态 | 改动 |
|---|---|---|---|
| P1-13 | HomeViewModel 定时器优先级 | ✅ 已修 | `HomeViewModel.cs:509` 一行 |
| P1-9 | 缺陷落库异步化 | ✅ 已修 | `DefectHistoryStore.cs` 重写 + `HistoryService.cs` |
| 瘦身 | defect_history.db 650MB → 10.6MB | ✅ 完成 | DELETE 304.7 万行 + VACUUM，备份保留 |

### P1-9 修复内容（缺陷落库异步化）

`DefectHistoryStore.Append` 从"采集线程同步 new DbContext + SaveChanges"改为 **Channel + 后台批量落库**：
- `Channel.CreateBounded(8192)`，`FullMode = DropOldest`（采集线程永不阻塞；极端溢出丢最旧保最新，
  快照型数据语义合理）；后台 flush 循环 1s 批量（单批 ≤512 条）`SaveChangesAsync`。
- 失败语义与原实现一致：记日志丢弃本批（快照数据可容忍，不回放）。
- 新增 `Flush()`（同步排空，供测试断言与停机）与 `IDisposable`（停机排空 + 停止后台任务）；
  `HistoryService` 对自建实例负责释放（`_ownsDefectStore`），DI 注入实例由容器释放。
- 测试适配：`DefectHistoryStoreTests` 两处断言前加 `store.Flush()`；`PagedQueryStableOrderTests`
  直写 DbContext 不受影响。

### 缺陷库瘦身结果

- 删除 `Timestamp < 2026-08-07` 共 **3,046,725 行**（8/1-8/6 爆发期），剩余 51,681 行；
  `VACUUM` 后 **682MB → 10.6MB**。
- 备份：`%APPDATA%/Kanban/defect_history.db.bak-20260901`（682MB，可回滚）。

### 验证（第三轮）

- 编译：Collector.Core / MainAPP / MainAPP.Tests 0 错误。
- 测试：DefectHistoryStore/HomeViewModel/PlcDataAcquisition/ProductionLine/PagedQueryStableOrder
  5 类 **100 测试全绿**。
- 运行：新版已重启生效（PlcSimulator PID 41052 / MainAPP PID 11616，4999 连通）。

---

## 结论摘要

代码库的基础设施层做得**相当扎实**——批量读合并、SQLite WAL 调优、索引覆盖、Channel 异步批写、
MessagePack 差分推送、WPF 集合同步注册、OxyPlot 节流，这些常见坑基本都填了。**不要在这些地方重复劳动**。

真正的瓶颈集中在两处，且都是**随设备数 N 平方级恶化**的：

1. **产线页汇总 KPI 的 O(N²) 刷新链**（`ProductionLineViewModel`）— 50 台设备即每秒约 50 万次迭代，200 台时放大 16 倍。
2. **PLC 地址解析零缓存 + 每次读写触发全局锁/JSON 序列化**（采集热路径）— 50 台 × 5Hz 下每秒约 5 万次正则匹配。

修掉这 5 个 P0，50 台规模下的 CPU 占用预计可下降一个数量级，且**全部是局部改动，不触碰业务规则**。

---

## 已优化，不要动（核查确认）

| 项 | 位置 | 现状 |
|---|---|---|
| PLC 批量读 | `PlcScanPipeline.cs:124-232`、`PlcBatchReadPlanCache.cs:28` | D 字/M 位已合并块读 + 轮内缓存 + 计划签名缓存，同地址一轮只读一次 |
| SQLite 调优 | `DatabaseProvider.cs:427-446,518-539` | WAL + `synchronous=NORMAL` + `busy_timeout=5000` + `mmap_size=256MB` + 5min PASSIVE checkpoint |
| 索引与查询 | 8 个 DbContext | `(DeviceId,Timestamp)` 复合索引齐全，查询全 `AsNoTracking`，无 N+1 |
| 写入异步化 | `ProductionHistoryWriter.cs:125-164` | Channel + 后台批写 + 事务；基线全内存仅变更落盘 |
| SignalR 推送 | `SnapshotPublisher.cs:61`、`EventBroadcaster.cs:89` | MessagePack、差分推、有界 DropOldest 背压、Seq 环形缓冲重连补偿 |
| 实时链路 | `RemoteRuntimeSink.cs:63-91,473-521` | 500ms 批量闸 + 同设备丢旧帧，SignalR 线程只入队，UI 线程一次 FlushBatch |
| 跨线程集合 | `WpfCollectionBindingRegistrar.cs:41-44` | Devices/Runtimes/WorkOrders/Recipes 已 `EnableCollectionSynchronization` |
| OxyPlot 节流 | `OverviewView.xaml.cs:93-128`、`HomeViewModel.cs:901-910` | 脏标记 + 2Hz 节流 + IsPageActive 守卫；PollingTrendBuffer 硬上限 60 点 |
| 列表虚拟化 | 各列表页 | 显式 `VirtualizingStackPanel.IsVirtualizing` + Recycling；WrapPanel 是真 VirtualizingPanel |
| 内存泄漏 | 各 VM 的 Dispose | 事件订阅成对解绑，无静态事件持有 VM |
| 定时器优先级 | 11 个定时器 | 除 `HomeViewModel.cs:509` 外全部显式 `Background` |

---

## P0 — 随规模平方级恶化

### P0-1 产线页汇总 KPI 刷新是 O(N²)

**位置**：`MainAPP/ViewModels/ProductionLineViewModel.cs:276-301` → `:310-329`，getter 在 `:90-113`

**根因**：任一设备的 `TotalOkProduction`/`TotalNgProduction`/`Oee`/`QualityRate`/`PerformanceRate`/`AvailabilityRate`
变化即调 `RefreshSummaryKpis()`，一次抛 **17 个** `PropertyChanged`。而这 17 个属性**全是 O(N) LINQ**：

- `RunningCount/AlarmCount/PausedCount/OfflineCount:91-95` — 4 × `Count(闭包)`
- `TotalOkProduction/TotalNgProduction:96-97` — 2 × `Sum(闭包)`
- `TotalOutput:98` — 2 × Sum；`TotalOutputDetailText:100` — 再 2 × Sum
- `WeightedOee:105-113` — 2 × Sum + 2 个闭包，**且 `ProductionLineView.xaml:130-131` 绑了 2 次**（Foreground + Text）

另外 `NotifyItemTransient:306` 每次 O(N) 线性查 sender。

**放大链条**：一帧内 N 台 × 6 个属性 = 6N 次触发 × 17 次通知 × 平均 O(N) 求值 ≈ **102 N²**。
50 台 ≈ 25.5 万次迭代 / 500ms；**200 台时 16 倍 ≈ 408 万次**，UI 线程直接卡死。

**修法**：
1. `OnRuntimePropertyChanged` 里只置 `_kpisDirty = true`，用一个 `DispatcherTimer(DispatcherPriority.Background, 500ms)` 合并刷新——这是收益最大的一步，一行脏标记换掉 6N 次调用。
2. KPI getter 改成**单次 `for` 循环累加所有指标**（count/ok/ng/oee 加权一次遍历出结果），缓存到字段，去掉全部 LINQ 与闭包分配。
3. `WeightedOee` 结果缓存到字段，XAML 只绑一次（Foreground 改绑缓存属性而非再求值）。
4. `NotifyItemTransient` 用 `Dictionary<DeviceRuntime, LineDeviceItem>` 反查替掉 `FirstOrDefault`。

---

### P0-2 `FilteredLineDevices` 每次返回新 List，虚拟化完全失效

**位置**：`ProductionLineViewModel.cs:428-457`，绑定在 `ProductionLineView.xaml:259`

**根因**：getter 末尾 `return q.ToList()` 每次返回**新 List 实例**。`ItemsSource` 收到新引用 → ListBox 走
`Reset` → `VirtualizingWrapPanel.OnItemsChanged:173` 全量重建容器，**虚拟化彻底失效**。
`HasNoFilteredDevices:422` 又调 `FilteredLineDevices.Any()`，多求值一次；XAML 252/267 绑两次。

**触发条件**：`LineSortBy != Default` 或 `LineStatusFilter != All` 时，每台设备每次属性变化都触发
（`:285-286`、`:296-297`）。默认是 `Default` + `All`，所以**默认路径不痛，用户一旦点排序/筛选立刻卡死**——
而按 OEE 排序正是看板的常用操作。

**修法**：改用 `ListCollectionView`（`CollectionViewSource.GetDefaultView(LineDevices)`）+ `SortDescriptions`/`Filter`，
数据变化时 `DeferRefresh()`；排序态下用 `LiveSortingProperties`（WPF 4.5+）或定时 `Refresh()`，
避免每次属性变化重建集合引用。

---

### P0-3 PLC 地址解析零缓存，热路径每次 5~10 次正则

**位置**：`Kanban.Collector.Core/Services/PlcAddressParser.cs:91-187`

**根因**：`Parse` 无任何缓存，每次调用 `Trim().ToUpperInvariant()`（2 次串分配）+ 顺序正则匹配。
D 字地址要走 5 次才命中 `DPattern`，M 位要 6 次。更糟的是**单次 DWord 读会重复解析 3~4 次**：
`TryReadProductionCount`(`PlcDataAcquisitionService.cs:768`) → `ReadInt32Value`(`PlcScanPipeline.cs:110` Normalize)
→ `PlcDeviceAdapter.ReadInt32`(`DeviceAdapter.cs:236`) → `ToTransportAddress`(`DeviceAdapter.cs:56`)。

`CountConfiguredReadOperations`(`PlcDataAcquisitionService.cs:594-608`) 更离谱：**每轮对每台设备的每个
报警/缺陷/计数报警地址全量 Parse 一次，纯粹为了诊断显示**。

**规模**：50 台 × 23 地址 × 5Hz ≈ 5750 次解析/秒 ≈ **5 万次正则匹配 + 1.7 万次串分配/秒**，全部进 Gen0。

**修法**：在 `IPlcAddressCodec` 外包一层 `ConcurrentDictionary<string, PlcAddressParseResult>` 记忆化
（地址集有限，key 用原始串 + `StringComparer.Ordinal`）。失效点挂在 `DeviceRepository.ReplaceAll/SaveAll`
与 `AppSettings` 配置变更上整体 `Clear()`。`CountConfiguredReadOperations` 结果随配置版本号缓存。

---

### P0-4 每次 PLC 读写触发全局锁 + 整个 PlcConfig 的 JSON 序列化

**位置**：`Kanban.Collector.Core/Services/PlcRuntimeSession.cs:134-157`

**根因**：`PlcDeviceAdapter.RuntimeProfile/RuntimeDriver` 是**属性**，`PlcDeviceAdapter.cs:196-197`
每次访问都调 `sessionManager.Get()`：加全局 `_sync` 锁 → `FindConnectionProfile` →
`EnsureConnectionProfiles()`（`AppSettings.cs:402-436`，每次 new HashSet + O(P) 循环 + `FirstOrDefault` 闭包）。
非 default 档案还要 `GetConfigurationSignature()`，而它就是 `JsonSerializer.Serialize(this)`（`PLCConfig.cs:150`）。

**一次 `ReadInt32` = 2 次 Get = 2 次锁 + 2 次全对象 JSON 序列化**。单 PLC（default 档案）走早退不付 JSON 代价；
一旦配多档案（≥2 台 PLC），20 次批读/轮 × 5Hz = **200 次 JSON 序列化/秒**，且全局锁把采集与 UI 写操作串在一起。

**修法**：
1. `PlcRuntimeSession` 缓存 `_signature`，只在 `Configure/Refresh` 时重算（或改 `long` 版本号递增比对）。
2. `PlcDeviceAdapter` 把 profile/driver 缓存到字段，按版本失效。

---

### P0-5 单设备故障拖垮整轮，且阻塞 IO 全程持锁

**位置**：`PlcDataAcquisitionService.cs:690`（串行 `foreach`）+ `HslNetworkPlcDriver.cs:126,144`

**根因**：设备串行 `foreach`，且 `Execute` 把**阻塞式 socket IO 包在 `lock(_sync)` 内**。
一台设备超时（`TimeoutMs` 默认 5000ms）会独占整轮，并阻塞该 PLC 上**所有其它读写**——
包括 UI 触发的配方下发、产量清零。跨多 PLC 时每轮耗时 = ΣRTT，在 100~200ms 轮询下必然超期，
`Task.Delay` 完全失去节流意义。

**修法**：按 `ConnectionProfileId` 分组，**组内串行**（Hsl 连接非线程安全）、**组间 `Parallel.ForEachAsync`**；
单读超时降到 300~800ms；连续失败设备进冷却列表，本轮直接跳过（别为故障设备每轮付一个完整超时）。

---

### P0-6 `CreateDeepSnapshot` 在锁内做 JSON 序列化 + 反序列化

**位置**：`Kanban.Collector.Core/Data/DeviceRepository.cs:483-493`

**根因**：锁内 `Serialize` + `Deserialize` 往返做深拷贝。调用点 `SaveAllAsync:121`（Remote 保存）、
`ExportToFile:221`。50 台设备配置 JSON 达 MB 级，锁内双次 JSON ≈ 数十 ms。

**影响**：持锁期间阻塞 `EnsureRuntime:326`、`GetDevicesSnapshot:340`——而 `RemoteRuntimeSink`
每 500ms 的批量应用走的就是这条 → **点保存时 UI 卡顿 + 实时数据滞后**。

**修法**：锁内只 `Devices.ToList()` 浅拷贝（与 `SaveAll:196-207` 一致），出锁再 `Serialize`；
Remote 推送直接发浅拷贝，去掉无谓的 `Deserialize`。

---

## P1 — 可观收益

### P1-7 `GetDevicesSnapshot()` 每轮调用 11 次

**位置**：`DeviceRepository.cs:340`（`lock` + `Devices.ToList()` 新 List）
调用点：`PlcDataAcquisitionService.cs:261,522,597,690,862,932,982,1010,1030,1056,1163` + `PlcScanPipeline.cs:133,247,284,323,390`

50 台 × 11 × 5Hz = 2750 次引用拷贝/秒 + 55 次锁获取。MainAPP 下该 `_collectionLock` 即 WPF 绑定的 SyncRoot，
**与 UI 线程直接竞争**。

**修法**：每轮只在 `RefreshDeviceData` 取一次快照，作为参数传给各 `Scan*`/`Accumulate*`；
`:261` 的 `.Count` 换成仓储上的 `DevicesCount`（锁内读 `Devices.Count`，零分配）。

### P1-8 Diff 推送先造对象再比较

**位置**：`SnapshotPublisher.cs:60-64`

`ToSnapshot` 先 `ToArray()` 出 DTO 数组再 `SameSnapshot` 比较 —— 静止设备省了带宽但**没省分配**。
2Hz × 50 台 = 100 次全量 DTO 构造/秒 + 两条 LINQ 链。

**修法**：比较下沉到字段级——先比 `StatusWord/OkProduction/NgProduction/RunTime/AlarmTime` 标量，
未变直接 `continue`，变了才建 DTO。

### P1-9 缺陷快照同步落库跑在采集线程

**位置**：`DefectHistoryStore.cs:20-32`，调用点 `PlcDataAcquisitionService.cs:916`

每 ~5s 在轮询线程 new DbContext + `SaveChanges`（开连接时还要跑 4 条 PRAGMA），
与后台 flush 抢 SQLite 写锁，拿不到就整批丢弃。

**修法**：与 `ProductionHistoryWriter` 同构，改 Channel + 后台批写。

### P1-10 每轮重建批量读计划签名

**位置**：`PlcScanPipeline.cs:177-203`

每轮为每台设备做 `addresses.OrderBy(...)` + 两层 `string.Join`，只为了和缓存签名比一次字符串。
50 台 × 20 地址 = 50 次排序 + 51 次 Join/轮。

**修法**：签名的 devices 部分换成配置版本号（设备/报警/缺陷增删时 `++`），版本未变直接复用缓存计划。

### P1-11 状态筛选谓词深扫 + 推送优先级过高

**位置**：`MainAPP/ViewModels/DeviceListViewModel.cs:204-213`、`Helpers/UiDispatcher.cs:21`

每台设备 `StatusWord` 变 → `BeginInvoke(action)` **默认 Normal 优先级（9）**，高于 Render(7) 与 Input(5)。
`StatusFilter != All` 时再 `FilteredDevices.Refresh()`，谓词 `DeviceMatchesKeyword:118-156` 深扫每台设备的
Alarms/Defects/CounterAlarms/Sources/Values（约 100 子项/台）→ **50 台 = 5000 次 Contains，且抢在渲染前执行**。

**修法**：`UiDispatcher.Dispatch` 加 `DispatcherPriority.Background` 重载；状态筛选改为预建
`HashSet<string>` 设备 Id 索引，避免深扫。

### P1-12 数据源监控页 1s 定时器里的分配风暴

**位置**：`MainAPP/ViewModels/DataSourceMonitoringViewModel.cs:410-471,662-713`

`GetOrUpdateRow:476` **对每个采集值都 new 一个 row 快照**（命中缓存也 new）→ 2500 值/秒纯垃圾；
`OrderBy` 用 `StringComparer.CurrentCultureIgnoreCase` 三级排序（文化敏感，最慢）；6 次 `Count` 全扫；
收尾 `TrendChart.InvalidatePlot(true)` 主线程同步全量重绘。

**修法**：快照仅在值确实变化时创建；排序键改 `OrdinalIgnoreCase`；KPI 合并为一次遍历；
`InvalidatePlot(false)` + 趋势点数上限。

### P1-13 `HomeViewModel` 定时器漏配优先级

**位置**：`MainAPP/ViewModels/HomeViewModel.cs:509`

`new DispatcherTimer { Interval = ... }` 未指定优先级 → 默认 Normal。**全项目其余 10 个定时器
都显式 `Background`，此处是唯一漏网**。`SyncRuntime` 内含图表重算路径，抢渲染优先级。

**修法**：`new DispatcherTimer(DispatcherPriority.Background){...}`。一行改动。

---

## P2 — 锦上添花

| # | 位置 | 问题 | 修法 |
|---|---|---|---|
| 14 | `PlcDataAcquisitionService.cs:573` | `Task.Delay` 在循环尾 → 周期 = 工作耗时 + 间隔，无漂移补偿 | 改 `PeriodicTimer` 或按 Stopwatch 余量 Delay |
| 15 | `AcquisitionDiagnosticsStore.cs:185-191` | 每 500ms 对 1024 元素做两次 `OrderBy().ToArray()` | 环形数组 + 仅快照时排序一次，或维护直方图 |
| 16 | `PlcDataAcquisitionService.cs:325-334` | 每轮 3 个 HashSet 分配 + LINQ 链 | 改字段级集合 `Clear()` 复用 |
| 17 | `PlcDataAcquisitionService.cs:787,892` | `device.Id + suffix`、`$"{device.Id}|{defect.Id}"` 每设备每轮 2~3 次 | 复合键预计算缓存 |
| 18 | `PlcScanPipeline.cs:285,324,391,934` | `device.Defects.ToList()` 等 | 快照已是可安全枚举的拷贝，直接 `foreach` 原集合 |
| 19 | `DeviceManagerViewModel.cs:80-85` | `CurrentDeviceValidationErrors` 每次 get `Where().ToArray()`，`HasCurrentDeviceValidationErrors` 再算一遍 | 缓存到字段，脏时失效 |
| 20 | `DeviceManagerDeviceParamsTab.xaml:76` | `RelativeSource AncestorType` 走可视树上溯 | 改 `ElementName` 或直接继承 DataContext |
| 21 | `OverviewView.xaml.cs:130-136` | `view.Model = null; view.Model = model;` 2Hz × 4 图强制拆装渲染上下文 | 仅首次或 Model 实例真正替换时执行 |
| 22 | `Controls/VirtualizingWrapPanel.cs:136-149` | `measuredHeight` 与 `_itemHeight` 差 >0.5 就 `InvalidateMeasure()`，卡片文案换行会触发二次布局 | 加收敛计数上限 |
| 23 | `Services/ApplicationStartupCoordinator.cs:123-128` | Show 前同步 `EnsureCreatedAll()` + `WorkOrderRepository.LoadAll()` | 移入 `Task.Run`，首屏后 Join |

---

## 落地顺序建议

| 批次 | 内容 | 风险 | 预期收益 |
|---|---|---|---|
| **A**（半天） | P0-1 脏标记合并、P1-13 定时器优先级、P0-3 地址解析记忆化、P0-6 DeepSnapshot 出锁 | 低，全是局部改动 | 采集侧 Gen0 分配降 ~70%；UI 刷新量降 ~95% |
| **B**（1-2 天） | P0-2 ListCollectionView、P0-4 session 缓存、P0-5 分组并行 + 冷却列表 | 中，涉及并发结构与行为时序 | 多 PLC 场景吞吐显著提升；筛选/排序不再卡死 |
| **C**（按需） | P1-7 ~ P1-12、P2 各项 | 低 | 进一步削峰，改善长稳 |

**注意**：批次 B 的 P0-5 会改变故障设备的时序表现（从"每轮等一个超时"变成"跳过"），
需同步确认诊断面板与报警中心的预期行为，避免把"设备被冷却跳过"误报成"设备离线"。

---

## 验证方法

- **基准**：`MainAPP.Benchmarks` 已有 BenchmarkDotNet 骨架（`CalculateStateDurationsBenchmark` 等），
  建议补充 `PlcAddressParser.Parse`（加缓存前后）与 KPI 聚合（LINQ vs 单循环）两组用例，改动前后对比出数。
- **采样**：UI 卡顿用 PerfView/dotTrace 按 CPU 采样抓 `ProductionLineViewModel.RefreshSummaryKpis` 与
  `PlcAddressParser.Parse` 的占比，改动前后应接近 0。
- **规模压测**：PlcSimulator 灌 50 / 100 / 200 台设备，观察 CPU 与帧时间是否从 O(N²) 回到 O(N)。
- **回归**：`MainAPP.Tests.exe -noColor`（2443 测试）。注意 `dotnet test` 与本项目不兼容
  （xUnit v3 in-process runner 报 0 匹配）。
