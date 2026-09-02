# Kanban 性能评审 · 第二轮（当前工作树状态）

日期：2026-09-01 ｜ 只读调研，**未改任何代码**
范围：Kanban.Collector.Core / Kanban.Collector / MainAPP（WPF）/ Kanban.Web（Blazor WASM）/ 数据层

## 摘要

第一轮（同日早些时候）已覆盖采集热路径与产线页，其中 3 个 P0 已修复。本轮做两件事：**复核已修项**、**补齐第一轮完全没扫过的区域**（工单页 / 报警中心 / 设备详情 / Blazor Web / 数据访问层）。

**最重要的发现不是性能项，而是一个新引入的功能性回归**：

> `DeviceAdapterResolver._profileBindings` 是永久缓存且全仓无任何失效路径，而新加的 `_cachedSession ??=` 让 adapter 永久持有 session。档案被删除后以相同 Id 重建时，`RefreshFromSettings` 会 `Dispose()` 旧 session（`PlcRuntimeSession.cs:340`），但缓存里的 adapter 仍指向已释放的 session —— **该档案全部 PLC 读写永久抛 `ObjectDisposedException`，直到进程重启**。触发入口是 `SettingsViewModel.cs:826`（设置页保存），属于用户可稳定复现的路径。

其余新瓶颈集中在**三处 O(N²)**：工单集合逐条变更、报警集合同步、Web 历史深分页。

---

## 一、已修项复核（第一轮 P0-1 / P0-3 / P0-4）

### ✅ 通过：地址解析记忆化

`PlcAddressParser.cs:79-107`、`PlcAddressCodecs.cs:78-90/212-230`、Omron / Keyence 同构。

- **缓存"失效不完整"不是问题**：所有 `ParseCore` 都是输入字符串的纯函数，结果与配置无关。所以 `ClearCache()` 漏掉 Alarm / Recipe / WorkOrder 写入口不会产生陈旧数据 —— 现有失效点已足够，不必补齐。
- **无界增长：无风险**。key 来自配置项地址与批量计划派生的稳定地址集，有限。唯一瑕疵：key 用**原始串**，`"D100" / "d100" / " D100 "` 各占一项（受配置规模约束，可接受；想省内存可先 Trim 再查）。
- **线程安全**：`GetOrAdd` 无锁双检时可能并发多跑一次正则，无副作用。

### ✅ 通过（小瑕疵）：ConfigurationSignature 按版本缓存

`PlcRuntimeSession.cs:32-47`。前提成立（`Profile.Version` 仅在 `PlcRuntimeProfileProvider.Refresh` 递增）。

**瑕疵**：`_signature` / `_signatureVersion` 非 volatile 且两字段非原子写，并发下可读到 `(新 version, 旧 signature)`，导致 `Get()` 漏掉一次必需的 `Configure`。概率低但真实。修法：存成一个不可变元组 / record，一次引用赋值。

### ⚠️ 回归风险：`_profileBindings` 永久缓存 + session 字段缓存

| 文件:行 | 事实 |
|---|---|
| `DeviceAdapter.cs:61` | `ConcurrentDictionary<string, IDeviceAdapter> _profileBindings` |
| `DeviceAdapter.cs:111` | **全仓唯一引用** —— `_profileBindings.GetOrAdd(profileId, ...)`，无任何 `Clear` / 失效路径 |
| `DeviceAdapter.cs:204/222` | 新增 `_cachedSession ??=` / `_cachedDriver ??=`，adapter 永久持有 session |
| `PlcRuntimeSession.cs:329-340` | `RefreshFromSettings` 对移除的档案 `session.Dispose()`；`:320` 对新增档案 `CreateSession` |
| `SettingsViewModel.cs:826` | 设置页保存即触发 `RefreshFromSettings()` |

**失效场景**：档案删除后以相同 Id 重建（或品牌 / IP 变更后 Dispose 重建）→ 旧 session 被释放、新 session 建好，但缓存里的 adapter 的 `_cachedSession` 仍指向旧的 → 每次读写 `ObjectDisposedException`。

**修法**（二选一）：
1. `_profileBindings` 的 value 改为 `(signature, adapter)` 元组，`RefreshFromSettings` 时整体失效重建；
2. adapter 每次使用时校验 `_cachedSession` 仍存在于 manager 的 `_sessions` 中，否则重取。

### ✅ 已验证"不是问题"（避免重复劳动）

`_cycleInt32Values` / `_cycleBatchAddresses`（每轮 Clear）、`_initializedCounterAlarmIds`（`RemoveWhere` 回收）、`DataSourceAlarmTracker._states`（RemoveDevice 同步清理）、`_durations` 队列（上限 1024）—— 均有界。
`HslNetworkPlcDriver.Execute` 的 `lock(_sync)` 是单物理连接必需，不算问题。轮询为「工作 + Delay」串行，无重入，周期漂移已有 Stopwatch 补偿。

---

## 二、采集核心（Kanban.Collector.Core）

### P0 — 随规模平方级恶化

| # | 位置 | 根因 | 规模 | 修法 |
|---|---|---|---|---|
| **C-1** | `PlcScanPipeline.cs:289 / 336 / 394` | `_adapterResolver.Resolve(device)` 写在了**子项内层循环**里（`:285 foreach defect`、`:324 foreach ca`、`:391 foreach source` 之内）。每次 Resolve = `_adapters.Where(...).ToList()`（`DeviceAdapter.cs:101-105`）+ `FindConnectionProfile` 线性扫（`AppSettings.cs:385-391`） | 20 设备 × (10 缺陷 + 5 计数报警 + 5 源) @5Hz ≈ **2000 次 Resolve/s**，每次 2 次 LINQ 物化 | 提到设备层循环外，每设备解析一次 |
| **C-2** | `PlcScanPipeline.cs:190-203`、`AlarmStateTracker.cs:273-278` | 每轮为「判定计划是否变化」重算签名：`addresses.OrderBy(...)` + 两层 `string.Join` 全部地址。**计划缓存的收益被签名成本吃光** | 500 地址时每轮 500·log500 次比较 + 拼接 ~10KB 串；5Hz → 2500 次排序比较/s + 50KB/s 垃圾；两处合计翻倍 | 改配置版本号驱动（配置变更时 `Invalidate()`），或缓存 `HashSet` 引用 + 计数做廉价脏检测 |
| **C-3** | `PlcRuntimeSession.cs:307,322-323` ← 每轮由 `PlcDataAcquisitionService.cs:384 EnsureConnectedAll()` 触发 | `GetConfigurationSignature()` = `JsonSerializer.Serialize(this)`（`PLCConfig.cs:150`）全对象图反射序列化，**每轮 × 档案数** | 8 档案 @5Hz = **45 次全量 JSON 序列化/s**，随档案数线性放大 | 给 `ConnectionProfile` 加变更版本号，签名按版本缓存（同 P0-4 模式） |

已复核：`Resolve()` 的 5 个调用点中 `:109`、`:188` 在设备层（正确），`:250` 的 `GroupBy(_adapterResolver.Resolve)` 也只按设备走一次（正确），**仅 289/336/394 三处在内层**。

### P1

- **`PlcDataAcquisitionService.cs:594-608`** `CountConfiguredReadOperations()`：每轮全量遍历所有设备的报警/缺陷/计数报警并对每个子项 `codec.Parse`（闭包 + `Count()` 迭代器）。500 子项 @5Hz = 2500 次/秒。改为配置变更时算一次并缓存。
- **`PlcDataAcquisitionService.cs:745-749`** `GetConfiguredReadProfileIds()`：每轮 `HasPotentialReadAddress`（6 个嵌套 LINQ `Any`）全量扫 + `ToHashSet`。同上，按配置版本缓存。
- **`DeviceRepository.cs:345-349`** `GetDevicesSnapshot()`：**每轮被调用 10 次**（AcqService 490/597/690/982/1010 + Pipeline 133/247/284/323/390），每次 `lock` + `ToList()`。该锁经 `SyncRoot` 注册给 WPF 的 `EnableCollectionSynchronization` → 采集线程 50 次/s 抢 UI 共享锁。改为每轮取一份快照向下传递。
- **`PlcScanPipeline.cs:234-235` / `AlarmStateTracker.cs:324-325`** `GetBatchCacheKey`：每次读值做字符串插值 + `CanonicalKey`（→ `ToTransportAddress` → `Parse`），每次分配 2~3 个串；且 `adapter.Brand/ProtocolKey/AddressCodec` 每次都走 `PlcRuntimeProfileProvider.Current` 的 `lock`（`PlcRuntimeProfile.cs:43-46`）。每个地址每轮 3~4 次锁 + 3 次分配。
- **`WorkOrderRepository.cs:344-350`** `GetRunningByDevice`：锁内 `FirstOrDefault` 线性扫全表，锁与 WPF 绑定共享。每个历史写周期 × 每设备一次（`AcqService.cs:872`）。1000 工单 × 20 设备 = 2 万次比较/写周期。改 `deviceId → running` 字典索引，按 `ChangeVersion` 失效。
- **`ProductionBaselineStore.cs:140-152 + 245-259`**：任一 key 变化即全字典拷贝 + 全量 JSON + 原子写盘（含 .bak）。换班瞬间 N 个设备 × 2 key 依次触发 → **O(N²) 拷贝 + N 次全文件重写**。改为 1s 合并延迟写。
- **`DataSourceSnapshotStore.cs:430-441`** `Query → FlushBeforeQuery` → `FlushAsync().GetAwaiter().GetResult()` **同步阻塞**，每次 UI 查询强制排空队列 + 回放恢复文件。改查询只读已提交数据，或提供 async 重载。
- **`DataSourceSnapshotStore.cs:212-296`** 恢复文件回放：每批两次 `File.ReadAllLines`（`:227`、`:267`）+ `SequenceEqual`。200MB 上限、200 行/批 → 每轮两次全文件读，O(n²)。改流式游标 + 行偏移。

### P2

- `AcquisitionDiagnosticsStore.cs:53-54,185-191`：`Percentile` 每次快照对 1024 元素 `OrderBy().ToArray()`（3 处同类）。改环形数组 + 按需采样排序。
- `DatabaseProvider.cs:522-531`：`SqlitePragmaInterceptor` 每次开连接执行 4 条 PRAGMA（含 `mmap_size=256MB`）。
- `PlcDataAcquisitionService.cs:1302-1304`：`DetectShiftChange` 每轮 `Shifts.ToList()`。
- 删设备未清理：`_lastDefectSnapshot`（`AcqService.cs:898`）、`_lastRecordedSnBySource`（`PlcScanPipeline.cs:474`）。
- `AuditService.cs:114-128` `QueryAll`：`Take` 上限 10 万行 + `Count()` 两次往返。
- `DataSourceReader.cs:242-266` `Resolve` 每源每轮两次 LINQ + `ToArray()`；reader 集合构造后不变，按 protocolKey 建字典即可。

---

## 三、WPF 客户端（MainAPP）

### P0 — 卡 UI / 随规模平方级恶化

#### W-1 工单集合逐条变更 → 每次触发全量重算，导入 / 启动为 O(W²·K)

**位置**：`WorkOrderManagerViewModel.cs:437-438`（订阅点 `:368`）

```csharp
RecalcDerivedCounts();   // 每次 Add/Remove/Replace 都跑
RefreshFilteredView();
```

`RecalcDerivedCounts()`（`:456-477`）每次执行：
- `:458-460` 三次 `WorkOrders.Count(...)` 全表遍历
- `:461` `CountScheduleConflicts(...)` → `:690-699` 双重循环
- `:464-465` `Where().Select().ToHashSet()` + `ComputeConflictOrderIds()` → `:712-722` **再来一遍近乎相同的双重循环**
- `:468-473` `foreach (var w in WorkOrders)` 全表写 `IsOverdue / OverdueHintText / IsScheduleConflict`

冲突检测是 `for i / for j>i` 配对 + `if (ordered[j].PlannedStart >= ordered[i].PlannedEnd) break;` 剪枝 → **O(W × 并发重叠窗口 K)，最坏 O(W²)**（同设备大量重叠 Pending 工单时退化）。

**放大链条（已复核）**：`WorkOrderRepository.LoadAll()`（`:99-104`）是 `WorkOrders.Clear()` 后 **`foreach ... Add(w)` 逐条添加**：

```csharp
WorkOrders.Clear();
foreach (var w in snapshot)
{
    WorkOrders.Add(w);      // 每条都各触发一次上面那一整套
}
```

`ReflectInto`（`:222-239`）与 `CleanupOldWorkOrders`（`:298-330`）同样逐条。而 CSV 导入是**按行** `UpsertAsync`（`Services/WorkOrderService.cs:613`）。

**代价**：W 条工单 × 每次 O(W×K)。W=300 且重叠窗口大时，一次导入卡死 UI 数十秒；同时排队 W 次 `FilteredView.Refresh()` + W 个 OxyPlot 甘特图 `Task.Run`（`:668`）。

**修法**：
1. 仓储侧加批量入口（`LoadAll` 用一次性 Reset 通知，或 `AddRange` 抑制逐条事件后发一次 Reset）；
2. VM 侧对 `CollectionChanged` 做**合并** —— `_changedSinceLastCoalesce` 标记 + `DispatcherTimer(Background, 100~200ms)`，把 N 次事件塌缩成 1 次 `RecalcDerivedCounts`；
3. `CountScheduleConflicts` 与 `ComputeConflictOrderIds` 合并为**一次**扫描同时产出计数与 Id 集合。

#### W-2 `ObservableCollectionSyncHelper.Sync` 实为 O(N²)，且引用比较使其退化为全量重建

**位置**：`Helpers/ObservableCollectionSyncHelper.cs:19-24`

```csharp
for (int i = target.Count - 1; i >= 0; i--)
    if (!source.Contains(target[i], ReferenceEqualityComparer.Instance))   // ← O(N) 线性扫
        target.RemoveAt(i);
```

`source` 是 `IList<T>`，无带 comparer 的 `Contains` 实例方法 → 绑定到 `Enumerable.Contains` → **每个 target 项一次 O(N) 扫描**，整体 O(N²)。

**更关键的是引用比较让差分完全失效**（已复核）：调用方传入的 source 都是 `new` 出来的对象 —— `AlarmCenterViewModel.cs:598-602 / 594-596` 每次 `new ActiveAlarmInfo(...)`，`HomeAlarmCollector.cs` 同理 —— 与 target 中的旧对象**引用必然不等** → 删除循环移除全部，再 Add 全部 → 等价 `Clear() + AddRange`，即 **2N 次 CollectionChanged**，ItemsControl 走 Reset、虚拟化容器全部重建、滚动位置丢失。

**主要调用点与规模**：
- `AlarmCenterViewModel.cs:646` `Sync(ActiveAlarms, visible)`，`MaxActiveAlarms = 200`（`:140`），`_activeTimer` 3s（`:372`）+ 搜索防抖触发 → **每 3 秒 400 次集合通知 + 200 个 DataTemplate 重建**
- `HomeAlarmCollector.cs:50-67`：`:52 FirstOrDefault(a => a.Equals(...))` 与 `:59 Any(a => a.Equals(item))` 是**另两个 O(N²)** 循环，然后 `:67` 再走一次 Sync
- `OverviewViewModel.cs:1010-1017`（7 个集合，60s 一次，N 小）

**修法**：
1. Sync 内先 `var set = new HashSet<T>(source, ReferenceEqualityComparer.Instance)` 再删 → O(N)；
2. 更根本：调用方先按**值**做一次 `existing` 查找并把 `desired[i] = existing`（`DeviceDetailViewModel.cs:916-925` 就是正确写法，照搬即可），让引用得以保留，差分才真正生效。

### P1

| # | 位置 | 根因 | 修法 |
|---|---|---|---|
| **W-3** | `Views/WorkOrderManagerView.xaml:78`（无 `Delay`）+ `WorkOrderManagerViewModel.cs:578 → 615-634 → 639` | 搜索无防抖，每次按键在 **UI 线程**跑 O(D×W) 扫描：`:645-647` `deviceSnapshot.Where(d => filtered.Any(w => w.DeviceId == d.Id))`。50 设备 × 2000 工单 = 10 万次比较/按键，且每次按键提交一次完整 OxyPlot 甘特图构建（N 个 `RectangleBarItem` + 每个 ≥60min 工单一个 `TextAnnotation`，`ChartService.cs:567-581`）。对照 `AlarmCenterViewModel.cs:385` 有 350ms 防抖，工单页缺失 | XAML 加 `Delay=300`；`devices` 过滤改 `filtered.Select(w => w.DeviceId).ToHashSet()` 再 `Where(d => ids.Contains(d.Id))` → O(D+W) |
| **W-4** | `WorkOrderManagerViewModel.cs:497` | `_derivedCountsTimer` **未指定 `DispatcherPriority.Background`** → 默认 Normal(9) > Render(7)。Tick（`:371-375`）同步跑 `RecalcDerivedCounts()`（O(W×K)）+ `RefreshGanttForCurrentFilter()`。全项目其它定时器（`RemoteRuntimeSink.cs:86`、`RuntimeMonitoringViewModel.cs:243`、`MainWindowViewModel.cs:438`、`ProductionLineViewModel.cs:68` 等）都显式写了 Background，此处是漏网 | 构造函数改 `new DispatcherTimer(DispatcherPriority.Background)`，并把 Tick 体重算下放 `Task.Run` |
| **W-5** | `DeviceDetailViewModel.cs:430-438` + `:588-593` | 每个数据源值变化都 `DispatchOnUi(() => { })` —— **空 Action**，且 `BeginInvoke` 无优先级 → Normal。数据源值由 `DataSourceValue.cs:275-278` 每扫描周期逐值写入，订阅点 `:408` 对设备全部启用源逐值挂接 → 每设备数十~上百源 × 200ms 周期 = **数百次 Normal 优先级 DispatcherOperation/秒**，纯分配 + 队列竞争，抢在 Render 之前 | `DispatchOnUi` 统一 `BeginInvoke(action, DispatcherPriority.Background)`；空委托钩子改为合并后的脏标记定时器（照搬 `:514 OnRuntimePropertyChanged` 的 `_kpiRefreshScheduled` 做法），不要逐值派发 |
| **W-6** | `AlarmCenterViewModel.cs:483-496` | `BeginInvoke` 无优先级；内部 `DeviceFilterItems.Clear()` + 全量 `OrderBy(d => d.Name)` 重建 + `Any(d => d.Id == ...)` O(N)；结尾 `RefreshActiveAlarms(blockUntilApplied: true)` **同步全设备 + 历史库扫描**。Remote 模式下设备列表批量刷新会连续触发多次 CollectionChanged（`:371` 订阅），每次一次全量同步扫描 | `BeginInvoke(..., Background)`；改 `blockUntilApplied: false`（异步路径 `:522-533` 已存在且带 `requestVersion` 守卫）；`DeviceFilterItems` 用差分同步而非 Clear+Add |
| **W-7** | `AlarmCenterViewModel.cs:844`、`:866` | `_uiDispatcher.Invoke(ApplyStats)` **同步阻塞工作线程**。`ApplyStats` 内含两次 `Sync`（O(N²)，见 W-2）+ 8 个属性赋值 + `:841 RefreshActiveAlarms(blockUntilApplied: true)`。UI 若正忙（OxyPlot 重建）则两头互等 | 改 `BeginInvoke(..., Background)` + 版本守卫（`:827/850` 已有 `requestVersion` 判断逻辑，沿用） |

### P2

| 位置 | 问题 | 修法 |
|---|---|---|
| `SettingsViewModel.cs:497` | `new DispatcherTimer { Interval = 60s }` **无优先级**（Normal）；Tick → `:621-633 RefreshLicenseStatus` 连发 9 个 `OnPropertyChanged` | 补 `Background`；9 个通知按 XAML 实际绑定裁剪 |
| `DeviceDetailViewModel.cs:916-925` | `ActiveAlarms.FirstOrDefault(a => a.Equals(desired[i]))` 在循环内 → O(N²) | 先建 `Dictionary<(deviceId,alarmName,level,kind), Row>` 再查 |
| `DeviceDetailViewModel.RefreshWorkOrder`（`RefreshKpis` 内） | `GetRunningByDevice` O(W) + `GetLatestPendingByDevice` **O(W log W) 且持 `_collectionLock`**（`WorkOrderRepository.cs:344/356`），每个采集周期在 UI 线程跑一次 | 加 `deviceId` 字典索引；UI 线程避免在锁内排序 |
| `AlarmCenterViewModel.cs:626-631` | `filtered` 被独立遍历 3 次：`.Select().Distinct().Count()`、`.OrderBy().First()`、`.Take()` | `MinBy(EventTime)` 或一次 `foreach` 聚合 |
| `Views/RecipeManagerView.xaml:424`、`Views/SettingsView.xaml:942` | `ScrollViewer` 包裹 `ItemsControl`（默认 StackPanel，无虚拟化） | 参数项多时改 `ListBox`/`DataGrid` + `IsVirtualizing` |
| `UserManagerViewModel.cs:336/382/420/448/459` | `_userStore.Add/Update/Remove/ResetPassword/Unlock` **同步文件 IO**（`UserStore.cs:67 ReadAllText`、`:97 Serialize` + 写盘）直接在 UI 线程 RelayCommand 执行 | 包 `await Task.Run(...)` 或给 UserStore 加 Async 变体（用户量小，故列 P2） |

### 已排查并排除（避免重复劳动）

- **`OverviewViewModel.cs`（70KB，全项目最大）写得最好，不用动**：全部计算属性 O(1)（`:99/139/142/143/154/162-173`），唯一 O(N) 是 `:117 SelectedDeviceName`（N=设备数，可忽略）；已有 `_refreshVersion` 守卫（`:622`）、`ComputeChartSignature` 图表签名缓存（`:1026-1047`）、`ApplicationIdle` 让位（`:635`）、Stopwatch 埋点。
- **`Views/OverviewView.xaml.cs`**：2Hz 节流 + `Background` 优先级（`:31`、`:55`），`ForceChartsRefresh` 仅进入时触发一次。
- **`Services/ChartService.cs`**：`MaxProductionLabels = 200`（`:125`）已对标注抽样封顶；`BuildWorkOrderGanttChart` O(N log N) —— 问题在于被无防抖调用（属 W-3），不是图表本身。
- **`RemoteRuntimeSink.cs`**：500ms 批量闸 + `Background` 优先级（`:86`），注释明确说明防队列积压。设计正确。
- **Dispatcher 优先级**：`MainWindowViewModel.cs:438/725/734`、`ProductionLineViewModel.cs:68/99`、`RuntimeMonitoringViewModel.cs:243/259`、`DataSourceMonitoringViewModel.cs:303/321`、`RecipeManagerViewModel.cs:84`、`UserManagerViewModel.cs:137`、`DeviceManagerViewModel.cs:266/1113` 均已显式 `Background`。
- **同步阻塞**：`RemoteHistoryQueryService.cs:300/656/731/871` 与 `WorkOrderService.cs:276-471` 的 `GetAwaiter().GetResult()` 都包在 `Task.Run` 内（防 UI 死锁，注释已说明），且 `WorkOrderManagerViewModel` 只调 `Async` 变体（`:936-1126`）。非 UI 阻塞问题。
- **虚拟化**：`WorkOrderManagerView.xaml:154-157`、`RecipeManagerView.xaml:228-230`、`AlarmCenterView.xaml:92-94`、`HistoryQueryView.xaml` 4 处、`OverviewView.xaml` 2 处、`DeviceManagerView.xaml:158`、`HomeRealtimeAlarmsCard.xaml:168-177` 均已正确设置 `IsVirtualizing + Recycling + CanContentScroll` 并显式指定 `VirtualizingStackPanel` 为 ItemsPanel。
- **事件泄漏**：全局无 `static event`；`DeviceDetailViewModel.cs:74-127/604-625`、`DeviceManagerViewModel.cs:220/253/1177/1180`、`DirtyTracker.cs` 全部成对 `+= / -=`，Dispose 路径完整。

---

## 四、Blazor Web + 数据访问层（第一轮完全未覆盖）

**宿主模型**：`Kanban.Web.csproj` = `Microsoft.NET.Sdk.BlazorWebAssembly`，`net10.0-browser` —— **WASM，单线程**。JSON 反序列化与 GC 会直接冻结 UI。

### P0

| # | 位置 | 根因 | 规模 | 修法 |
|---|---|---|---|---|
| **B-1** | `Services/HistoryFetch.cs:63-66` + `AlarmHistoryStore.cs:71,74-81`、`StatusTransitionHistoryStore.cs:71,74-82` | 循环 `page=2..200` 每页一次查询，**每页都跑一次全时间窗 `COUNT(*)`**，再 `OrderByDescending().ThenByDescending().Skip(offset).Take(size)`。而 `HistoryFetch.cs:57` **只用了首页的 `first.Total`** —— 后 199 页的 COUNT 全是纯浪费 | 500 条/页 × 200 页 = 10 万条（`HistoryQueryLimits.cs:11,28`）；Σoffset ≈ 500×(1+…+199) ≈ **995 万次索引步进** + 200 次全窗 COUNT。31 天窗口分钟级阻塞（代码注释 `HistoryFetch.cs:29` 已自证「7 天窗口 9 分钟」） | ① Total 仅首页计算（分页 API 加 `withTotal` 开关）；② 改 **keyset 游标分页** `WHERE (EventTime,Id) < (@lastTime,@lastId)`，彻底消除 `Skip` 的 O(offset) |
| **B-2** | `Services/AlarmAnalysis.cs:107-120` | 连锁检测外层遍历全部 trigger，内层从 `i+1` 前扫至 5 分钟窗口外（`:114 break` 剪枝）；**`:110` 每个 i 都 `new HashSet<string>()`**；`:123-124` 再对每个 chain 做 `string.Join` 分组 | 报警风暴（数千条 trigger 挤在 5 分钟内）退化为 O(n·k)：5000 条 → 千万级迭代 + 5000 次 HashSet 分配；**WASM 上直接卡死** | 双指针滑窗单次扫描（窗口单调，非嵌套），复用一个 `HashSet.Clear()`；或先对 trigger 数封顶再分析 |
| **B-3** | `Services/OeeAnalysis.cs:56-62` | 每个班次实例都执行 `sortedTrans.Where(...).ToList()` **和** `sortedTrans.LastOrDefault(t => t.EventTime < shiftFrom)` —— 两次全量线性扫 | 31 天 ≈ 60 个班次实例 × 10 万条转换 = **1200 万次比较** | `sortedTrans` 已在 `:36` 排序且 shiftFrom 单调递增 → 二分定位 + 单指针顺序推进，整体 O(n log n) |

### P1

- **`ReviewAnalysis.cs:367`**：班次汇总内嵌报警全表扫描 `alarms.Count(a => a.ShiftName == shiftName && ...)`，写在 `foreach (var group in ...)` 内 → O(班次实例 × 报警数)。改先 `GroupBy(ShiftName)` 建字典。
- **`AlarmAnalysis.cs:143-149`**：`recovers.Any(...)` 写在 `foreach` 里 → 最坏 O(n²)。改预建 `(DeviceId,AlarmId) → 最大 Recovered 时间` 字典。
- **`Components/EChart.razor:23-33`（已复核）**：为做变更检测，把**整个 Option 树**序列化成字符串（`:23`）；命中变更后 `:31/:33` 又把 `Option` **对象**传给 JS interop，由 interop **再序列化一次** → 双次全量序列化。Home / Monitoring 有 2~3s 常驻 Timer（`Home.razor:345`、`Monitoring.razor:161`），每次渲染即使数据未变也要付一次全量序列化；状态甘特图（`HistoryQuery.Status.cs:184-194`）长窗口下 Option 达数万节点。改成数据源版本号 / 结构哈希比对替代 JSON diff，或把已序列化的字符串交给 JS 侧 `JSON.parse`（只序列化一次）。
- **`Kanban.Web/Program.cs:24`**：`useMessagePack: false` —— 500ms 一帧的快照流全走 JSON（Collector 端已双协议并存）。且 `Kanban.Web.csproj` 与 `Directory.Build.props` 均无 `PublishTrimmed` / `RunAOTCompilation`。**这两项对 WASM 冷启动与反序列化开销是数量级收益**。
- **`ReviewAnalysis.cs:217-227`**：短于 6 秒的段会 `CreateSegment(prev.Start, end)` **重建前一段**并重算整段产量差分 → 连续 k 次抖动 O(k²)。改为先累积原始段，最后统一合并扫描一次。

### P2

- `AlarmAnalysis.cs:63-65` `CountPending` 用 `OrderByDescending().First()` 取最大值 —— 每分组一次完整排序，改 `MaxBy`。
- `DashboardState.cs:262-266` `GetSpeedHistory` 每次 `q.ToList()` 拷贝 120 点；`Home.razor:464-479` 每 2s 渲染调用且跑 3 遍 LINQ（`Max`/`Min`/`Select`）。改返回只读视图 + 单次遍历。
- ⚠️ **`ProductionAnalysis.cs:95-103` `FindBaselineBeforeWindow`**：循环体首轮必然 `return`，实际只检查了第 0 个元素 —— 这是**正确性缺陷**（基线查找形同失效），非性能项，顺带提示。

### 已确认没问题的区域

- **历史查询已分页**：所有 `*Paged` store 方法都在 SQL 层 `Count + OrderBy + Skip/Take + AsNoTracking`（`AlarmHistoryStore.cs:64-83`、`StatusTransitionHistoryStore.cs:64-83`、`HistoryService.cs:138-189`），客户端有 10 万条硬上限截断。
- **表格已客户端分页**：`HistoryQuery.Filters.cs:180-187` 每页 50 行，大列表不会整表渲染，无需 `Virtualize`。
- **只读查询全部带 `AsNoTracking`**：ProductionHistory / AlarmHistory / StatusTransition / DefectHistory / SnEvent 五个 store 无一遗漏；未发现 `Include` 滥用或客户端求值。
- **索引覆盖充分**：8 个 DbContext 的 `HasIndex` 与实际 Where/OrderBy 完全对齐 —— `production_logs`/`status_transitions`/`defect_history`/`datasource_snapshots`/`sn_events` 均有 `(DeviceId, Timestamp)` 复合索引，`alarm_events` 有 `(DeviceId,AlarmId)` + `EventTime`，`work_orders` 有 `(DeviceId,Status)`。**未发现缺索引的查询条件**。
- **SQLite 配置到位**：WAL + `synchronous=NORMAL` + `busy_timeout=5000` + `temp_store=MEMORY` + `mmap_size=256MB`（`DatabaseProvider.cs:438-443` 持久化项、`:526-529` 连接级拦截器），PASSIVE 周期 checkpoint（`:465`）。事务边界短，无长事务持锁。
- **`DashboardState` 渲染节流做得好**：排序快照缓存 + 状态汇总脏标记（`DashboardState.cs:210-252`），500ms 推送回调不触发 UI，页面统一 2~3s Timer 重渲染；无循环内 `StateHasChanged`。
- **`Kanban.Analysis/OeeCalculator.cs`**（45 行）为纯标量公式，无复杂度问题。

---

## 五、落地顺序建议

| 批次 | 内容 | 风险 | 预期收益 |
|---|---|---|---|
| **0（立即，P0 中的 P0）** | 修 `_profileBindings` 无失效导致的 `ObjectDisposedException` 回归 | 低 | 这不是优化，是**修 bug**。设置页保存后 PLC 读写可能整体失效，必须优先 |
| **A（半天）** | W-1 工单事件合并 + W-2 Sync 改 HashSet、调用方复用引用 + W-4 定时器优先级 + C-1 Resolve 提到循环外 | 低，全是局部改动 | 工单导入/启动从分钟级回到秒级；报警中心每 3s 的 400 次通知降到个位数；采集侧 Resolve 降 3 个数量级 |
| **B（1-2 天）** | C-2/C-3 版本号驱动失效、B-1 keyset 分页 + 首页才 Count、W-3 搜索防抖 | 中，涉及缓存失效契约与分页 API 语义 | 历史查询从分钟级降到秒级；搜索不再卡键 |
| **C（按需）** | B-2/B-3 Web 算法、C 区 P1、W 区 P1、各项 P2 | 低 | 进一步削峰，改善长稳 |

**需要提前确认的语义风险**：
- **W-1** 合并 `CollectionChanged` 后，「导入中」的实时计数会从「逐条跳动」变成「批量跳一次」—— 需确认进度展示是否可接受。
- **W-2** 让调用方复用实例引用后，`ActiveAlarmInfo` 从「每 3s 全新对象」变成「原地更新」，依赖「对象引用变化」来判断刷新的代码（若有）会失效 —— 需确认 `AlarmCenterView` 的绑定与样式触发器不依赖引用相等。
- **B-1** 改 keyset 分页后，翻页期间若有新数据写入，分页行为会从「Skip 偏移漂移」变成「游标稳定」—— 行为其实是变好，但需同步确认 UI 上的「总条数」显示是否仍准确。

---

## 六、验证方法

- **基准**：`MainAPP.Benchmarks` 已有 BenchmarkDotNet 骨架。建议补三组：`ObservableCollectionSyncHelper.Sync`（HashSet 版 vs 现状）、`RecalcDerivedCounts`（W=300 导入场景）、`PlcAddressParser.Parse`（缓存前后对照，用于确认 P0-3 收益）。
- **采样**：PerfView / dotTrace 按 CPU 采样抓 `WorkOrderManagerViewModel.RecalcDerivedCounts`、`AlarmCenterViewModel.ApplyActiveAlarms`、`PlcScanPipeline`（Resolve + 签名计算）。
- **规模压测**：PlcSimulator 灌 50 / 100 / 200 台设备；工单侧造 300 / 1000 / 3000 条含重叠窗口的工单做导入。
- **Web 侧**：HistoryQuery 选 31 天窗口，记录首屏耗时与 GC 暂停（WASM 用浏览器 Performance 面板）。
- **回归**：`MainAPP.Tests.exe -noColor`（2443 测试）。注意 `dotnet test` 与本项目不兼容（xUnit v3 in-process runner 报 0 匹配）。
- **专项验证（批次 0）**：设置页新建档案 → 保存 → 删除 → 用**相同 Id** 重建 → 保存 → 观察该档案设备是否仍在上报数据。当前预期是**永久失败**。
