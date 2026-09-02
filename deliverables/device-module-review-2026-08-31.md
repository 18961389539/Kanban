# 设备管理模块审查报告

- 日期：2026-08-31
- 范围：设备配置管理（设备管理器 6 个 Tab、设备详情页、导入导出/回滚、校验与审计、持久化仓储）
- 方法：静态代码走查，覆盖 33 个文件约 7700 行核心代码，交叉验证事件订阅链、并发路径（Remote 快照灌入 / PLC 采集线程）与既有单测覆盖

## 1. 模块地图

| 层 | 文件 | 行数 | 职责 |
|---|---|---|---|
| View | `MainAPP/Views/DeviceManagerView.xaml.cs` | 227 | 快捷键、拖拽排序、Tab 聚焦 |
| View | `MainAPP/Views/DeviceManager*Tab.xaml.cs`（6 个） | 218 | 各 Tab 的地址聚焦锚点 |
| View | `MainAPP/Views/DeviceDetailView.xaml.cs` | 17 | 详情页 |
| ViewModel | `MainAPP/ViewModels/DeviceManagerViewModel.cs` | 1181 | **协调者**：CRUD、保存、导入导出、回滚、审计、冲突定位、脏标记、权限、未保存保护 |
| ViewModel | `MainAPP/ViewModels/DeviceDetailViewModel.cs` | 1162 | 单设备 KPI、图表、工单、数据源卡 |
| ViewModel | `DeviceChildManagerViewModel.cs` + 6 个子 VM | 1592 | 报警/缺陷/计数/数据源/工单/PLC 命令 |
| ViewModel | `MainAPP/ViewModels/DeviceListViewModel.cs` | 222 | 搜索、状态筛选、摘要 |
| Service | `MainAPP/Services/DeviceConfigValidator.cs` | 481 | 保存前全量校验（地址/唯一性/阈值/跨设备冲突） |
| Service | `DeviceConfigIOService` / `DeviceAuditService` / `DevicePlcCommandHandler` / `AddressConflictService` | 621 | 导入导出回滚、审计快照、PLC 写、冲突汇总 |
| Repository | `Kanban.Collector.Core/Data/DeviceRepository.cs` | 515 | Devices/Runtimes/DeviceMap/RuntimeMap + JSON 持久化 |

**整体评价**：模块的可测试性设计（纯静态校验器、IDialogService 抽象、DirtyTracker 抽取、审计 before/after 快照、Dispatcher 封送的显式处理）明显高于项目平均水平，995 行的 `DeviceManagerViewModelTests` 覆盖了脏标记、保存竞态、未保存保护、冲突定位等关键路径。

遗留风险集中在两处：**ViewModel 绕过仓储直接改集合导致的索引失同步**，以及**深度克隆/审计快照采用字段白名单，新增字段极易漏拷**。

## 2. 问题清单

### P0 — 正确性缺陷，会导致静默数据丢失

#### P0-1 `DeviceMap` 与 `Devices` 失同步：Remote 模式下新建设备永远收不到实时数据

- 现象：
  - 新增/复制/单台导入的设备，在 Remote 模式下产量、状态字、报警、数据源实时值全部不刷新，重启应用后恢复。
  - 删除的设备仍残留在索引中，被继续回写，并在报警中心显示已删除设备名。
- 证据：
  - `DeviceManagerViewModel.cs:281`（`Devices.Add(newDevice)`）、`:489`（复制）、`:871`（单台导入）、`:398`（`Devices.Remove(target)`）—— 四处直接操作仓储集合。
  - `DeviceRepository.cs:461-473` `ReplaceStateLocked` 是唯一重建 `DeviceMap` 的地方（仅 LoadAll/ReplaceAll/导入/回滚/样本数据触发）。
  - `RemoteRuntimeSink.cs:362` `if (device is null) return;`、`:525` 同样静默丢弃 —— 每 500ms × 每设备一次，无任何日志。
- 影响：Remote 是生产部署形态，新设备上线后"看起来在跑但永远没数据"，且无任何错误提示，排查成本极高。
- 建议：
  1. 仓储新增 `AddDevice(Device)` / `RemoveDeviceById(string)` 单一入口，内部同时维护 `Devices/DeviceMap/Runtimes/RuntimeMap`；把 `RemoveRuntime` 降级为私有。
  2. 更彻底的做法：仓储在构造函数中订阅自己的 `Devices.CollectionChanged`，让 `DeviceMap` 成为集合的强制投影，从根上消灭绕过路径。
  3. 静默丢弃改为 `Log.Debug` + 诊断计数器，并在采集诊断面板可见。
  4. 补单测：`AddDevice 后 GetDeviceById 非空`、`RemoveDevice 后 GetDeviceById 为 null`。

#### P0-2 `CloneDevice` 丢失 `ConnectionProfileId` 与 `MachineType`

- 现象：复制设备（或单台 JSON 导入）时，副本静默回落到默认连接档案与通用机型。
- 证据：`DeviceManagerViewModel.cs:500-595` 的 `CloneDevice` 逐个复制了 5 个 PLC 地址、配方三元组、TargetCycle 与 4 个子集合，唯独漏掉 `Device.cs:20 MachineType` 与 `Device.cs:25 ConnectionProfileId`。
- 影响：若源设备走的是非默认连接档案（另一台 PLC、不同 IP/端口），复制出的设备会连错 PLC 而不报错；机型丢失还会影响配方按机型归属的关联。
- 建议：
  1. 立即补两个字段；
  2. 长期改为"JSON 深拷贝 → 改 Id/Name → 重建子项 Id"的黑名单式克隆，新增字段自动继承；
  3. `CopyDevice_ClonesConfig_NewId_UniqueName_MarksDirty` 用例补充对这两个字段的断言（当前用例只断言了名称与 Id，所以漏拷没被发现）。

### P1 — 功能缺陷与误导性交互

| 编号 | 问题 | 证据 | 建议 |
|---|---|---|---|
| P1-1 | 新建/复制/单台导入设备时误弹"放弃未保存更改"确认框；点"否"后新设备已入列但不被选中 | `DeviceManagerViewModel.cs:284 / :491 / :873` 设置 `SelectedDevice` 时未置 `_suppressSelectionGuard`，而 `ImportConfig`/`Rollback`/`SelectAddressConflict` 都置了（`:760`、`:908`、`:415`） | 三处补 suppress；或把保护改为仅响应"用户从列表主动点选"这一来源 |
| P1-2 | 删除设备未检查关联工单，工单 Tab 因 `FilterWorkOrderByDevice` 直接静默消失 | `DeviceManagerViewModel.cs:375-406` vs `DeviceWorkOrderViewModel.cs:192` | 删除前统计关联工单数量并提示；已有工单时要求二次确认或禁止删除 |
| P1-3 | 详情页活跃报警行的级别/描述修改后不刷新 | `DeviceDetailViewModel.cs:1155-1161` `Equals` 只比较 `Name/PlcAddress/IsCounterAlarm`，而 `Level`/`Description` 是 `init`（`:1118`）；`:916-925` 的差分只更新 4 个可变字段 | 差分命中时同步更新全部字段（改为可 set），或把这两个字段纳入身份键 |
| P1-4 | Remote（采集在 Collector 侧）下详情页数据源卡与趋势图恒为空，UI 无任何解释 | `DeviceDetailViewModel.cs:306-331` 注释已承认 "本进程库为空则趋势图为空" | Remote 模式下显示"实时数据由 Collector 提供"占位，而非空白 |
| P1-5 | 审计快照漏字段：无 `ConnectionProfileId`、无 `CounterAlarm.Id` | `DeviceAuditService.cs:14-25` 与 `:106-107` | 补齐；并把"审计快照字段完整性"做成契约测试（反射比对 Device 图与快照 record） |
| P1-6 | `DeviceConfigIOService.RollbackToBackup()` 同步版在 Remote 模式下会误读 MainAPP 本地 `devices.json.bak` | `DeviceConfigIOService.cs:104-134` 无 `IsRemote` 判断，仅 Async 版有（`:139`） | 同步版在 Remote 时抛 `InvalidOperationException`，与 `DeviceRepository.SaveAll()`（`:190`）保持一致 |

### P2 — 架构、可维护性与性能

| 编号 | 问题 | 证据 | 建议 |
|---|---|---|---|
| P2-1 | 协调者过重：1181 行承担 9 类职责 | `DeviceManagerViewModel.cs` | 拆出 `DeviceConfigPersistenceCoordinator`（保存+审计+备份刷新）、`DeviceImportExportCoordinator`、`DeviceSelectionGuard` |
| P2-2 | `CurrentDeviceValidationErrors` 每次 get 都 LINQ + `ToArray()`，`HasCurrentDeviceValidationErrors` 再算一次 | `:80-85` | `ValidationErrors` 赋值时按设备分组缓存字典 |
| P2-3 | `DeviceSummaryText` 每次 get 全量遍历所有设备，且被 runtime 变更（每秒 × 每设备）触发重读 | `DeviceListViewModel.cs:43-67`、`:204-213` | 改为增量计数器，仅在 StatusWord 跳变时增减 |
| P2-4 | `RefreshActiveAlarms` 用 `FirstOrDefault(Equals)` 做 O(n²) 差分，且每次 KPI 刷新都跑 | `DeviceDetailViewModel.cs:916-927` | 按身份键建 Dictionary 复用 |
| P2-5 | `_kpiRefreshScheduled` 非 volatile；`DispatchOnUi` 在 Dispatcher 关闭时提前 return，标志永久为 true → KPI 停止刷新 | `DeviceDetailViewModel.cs:504-519`、`:588-594` | 标志复位移入 finally，或用 `Interlocked` |
| P2-6 | `CreateDeepSnapshot` 在 `_collectionLock` 内做 JSON 序列化，会阻塞后台采集线程的 `GetDevicesSnapshot` | `DeviceRepository.cs:483-493` | 锁内只做 `ToList()`，序列化移出锁 |
| P2-7 | 依赖具体类而非接口，且 `DeviceMap` 不在 `IDeviceRepository` 中 | `DeviceManagerViewModel.cs:196`、`DeviceListViewModel.cs:69`、`DeviceWorkOrderViewModel.cs:81`、`DeviceRepository.cs:15-37` | 把 `DeviceMap`/`AddDevice`/`RemoveDevice` 纳入接口并全面面向接口编程 |
| P2-8 | `RemoveRuntime` 与 `RemoveDevice`（tombstone）双入口语义重叠，前者被 UI 删除路径误用（见 P0-1） | `DeviceRepository.cs:290-311` | 合并为单一 `RemoveDevice(deviceId)` |
| P2-9 | `ValidateChildIds<T>` 用泛型 + `switch` 判定具体类型，新增子类型需改 switch | `DeviceRepository.cs:438-459` | 让子项实现 `IIdentifiedEntity`（`Id`/`DeviceId`），统一处理 |
| P2-10 | 拖拽排序判定逻辑写在 code-behind，不可单测 | `DeviceManagerView.xaml.cs:154-226` | 抽 `DeviceListDragDropBehavior` 或可测的服务类 |
| P2-11 | `Devices => _deviceRepository.Devices` 直接暴露可变集合 | `DeviceManagerViewModel.cs:60` | 暴露 `ReadOnlyObservableCollection`，变更一律走仓储方法 |
| P2-12 | runtime 事件订阅链在 3 个 VM 中重复实现，新增 VM 易漏 Dispose | `DeviceManagerViewModel.cs:248`、`DeviceListViewModel.cs:75`、`DeviceDetailViewModel.cs:127` | 抽 `DeviceRuntimeMonitor` 统一订阅与推送 |
| P2-13 | 保存完成后 `OnIsDirtyChanged` 与 `Save()` 双写 Feedback，可能出现 Warning→Success 闪烁 | `DeviceManagerViewModel.cs:157-165`、`:669` | 统一由一处写入 |

## 3. 测试覆盖评估

已有覆盖（`MainAPP.Tests/Unit/DeviceManagerViewModelTests.cs`，995 行 / 40+ 用例）质量很高，覆盖了：脏标记边界、保存竞态（Remote 编辑期间保持脏）、审计 before 语义、未保存保护、撤销切换、冲突实时检测与定位、导入导出、状态筛选、搜索。

**缺口（建议优先补齐）**：

| 缺口 | 对应问题 |
|---|---|
| `CloneDevice` 字段完整性断言（尤其后加的字段） | P0-2 |
| `Add/Remove` 后 `GetDeviceById` 与 `Devices` 一致性 | P0-1 |
| `AddDevice`/`CopyDevice` 不触发未保存确认弹框 | P1-1 |
| `DeviceAuditService` 快照字段完整性（反射契约测试） | P1-5 |
| `DeviceConfigIOService.RollbackToBackup()` 在 Remote 模式应拒绝 | P1-6 |
| 报警级别修改后详情页活跃报警行同步更新 | P1-3 |

## 4. 落地建议（分批）

- **第 1 批（P0，建议在下次发版前完成）**：P0-1 仓储单一入口 + 集合投影维护 DeviceMap；P0-2 补齐克隆字段并改深拷贝方案。两项均需配套单测。
- **第 2 批（P1）**：P1-1 suppress 补全、P1-3 差分区配、P1-5 审计字段、P1-6 API 陷阱收敛；P1-2/P1-4 可结合交互稿一起改。
- **第 3 批（P2，随重构节奏）**：先做 P2-1 协调者拆分（为后续减负），再做 P2-2/-3/-4/-6 性能项，最后处理 P2-7/-8/-9/-11 的接口与封装收敛。

## 5. 值得保留的做法

以下设计在本次走查中表现良好，重构时建议保留，不要被"过度拆分"冲掉：

- `DeviceConfigValidator` 全静态、无副作用，错误带 Device + Tab 索引，天然可测且支撑"点击定位"交互。
- `DirtyTracker` 显式维护运行时字段黑名单，避免采集线程写入误置脏标记。
- 审计采用 before/after 全量快照 + 保存期间 `IsSuppressed` 抑制回填噪声，并用配置修订号判断保存后是否仍脏。
- 跨线程一律走 `UiDispatcher`/`DispatchOnUi` 封送，且子 VM 通过 `IDeviceManagerHost` 反向依赖父级，避免循环引用。
- 原子写 + 损坏文件备份为 `.corrupt`，避免配置永久丢失。
