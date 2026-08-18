# Kanban 全量代码审查报告

**日期**：2026-08-17
**范围**：全解决方案核心源码约 6.1 万行（MainAPP、Kanban.Web、Kanban.Collector、Kanban.Collector.Core、Kanban.Contracts、Kanban.Client、LicenseIssuer.CLI、LicenseManager.App、PlcSimulator；不含第三方 oxyplot 与测试项目）
**方法**：按核心数据链路 / 通信与授权 / WPF 客户端 / Web 客户端与数据库 4 条线并行审查 + 主 agent 对 P0/P1 关键项逐一交叉验证。

---

## 一、结论速览

| 严重度 | 数量 | 说明 |
|--------|------|------|
| 🔴 P0（功能回归） | 1 | 跨页导航事件从未订阅，多个核心跳转失效 |
| 🟠 P1（高危） | 2 | 并发写无锁、跨线程 PRAGMA |
| 🟡 P2（中） | 14 | 跨线程读改写、配方地址误判、定时器泄漏、时间窗被忽略等 |
| 🟢 P3（低/可维护性） | 12 | 热路径分配、非原子写、防御性不足等 |

**最关键结论**：2026-08-11 的「页面懒加载」重构引入了**双 VM 创建路径**，导致 4 类跨页事件订阅全部成为死代码，主页「查看详情/工单管理」、产线/概览「点击设备卡」、报警中心「查看报警历史」等跳转**全部失效**——这是最高优先级，建议立即修复。

> 标注规则：凡「✅ 已验证」为我在本仓库源码逐一核实的条目；其余为并行审查 agent 提供的、带文件行号的证据，未逐一复核。

---

## 二、P0 缺陷

### P0-1 跨页导航事件从未订阅（懒加载重构回归）✅ 已验证

- **文件**：`MainAPP/ViewModels/MainWindowViewModel.cs:415-418, 471-496` + `MainAPP/Services/MainAppPresentationServiceCollectionExtensions.cs:130-135`
- **类别**：correctness（功能回归）

**问题**：`HomeViewModel`/`ProductionLineViewModel`/`AlarmCenterViewModel`/`OverviewViewModel` 的跨页导航事件，其订阅代码 `SubscribeHome/SubscribeProductionLine/SubscribeAlarmCenter/SubscribeOverview` 只挂在 `_homeLazy` 等 `Lazy` 字段的创建回调里；而这些 `Lazy` 字段的**唯一取值点是同名属性 getter**（`MainWindowViewModel.cs:53/55/57/59`）。全项目 grep 确认这些属性 getter **从未被任何代码访问**。

而页面的真实 VM 创建路径是 `NavigationPage.ViewModel` → `NavigationPageModule` 的 `viewModelFactory = () => sp.GetRequiredService<TViewModel>()`（`NavigationPageHost.cs:75` `element.DataContext = Page.ViewModel`），**直接从 DI 解析，完全绕过 MainWindowViewModel 的 Lazy 属性**。

**证据**（已核实）：
- `MainWindowViewModel.cs:472` `vm.FocusDeviceRequested += OnFocusDeviceRequested;` 只存在于 `SubscribeProductionLine`，而它只被 `_productionLineLazy` 工厂（`:416`）调用。
- 全项目仅 `App.xaml.cs:399` 命中 `HomeViewModel`，但那是 `GetService<ViewModels.HomeViewModel>()` 的 DI 直取，非属性 getter。
- 受影响事件及其失效功能：
  - `HomeViewModel.ViewDeviceDetailRequested`（:499）→ 主页「查看详情」失效
  - `HomeViewModel.ViewWorkOrderManagerRequested`（:531）→ 主页「工单管理」失效
  - `ProductionLineViewModel.FocusDeviceRequested`（:327）→ 产线点设备卡失效
  - `OverviewViewModel.FocusDeviceRequested`（:1445）→ 概览点设备卡失效
  - `AlarmCenterViewModel.ViewAlarmHistoryRequested`（:528）→ 报警中心「查看报警历史」失效
- 例外：`DeviceDetailViewModel.GoBackRequested/ViewAlarmHistoryRequested` 因 `OnSelectedIndexChanged:662` 访问了 `DeviceDetailViewModel` 属性而**仍可用**；但「查看详情」入口已死，设备详情页实际无法进入。

**建议**：收敛为单一创建+订阅路径。二选一：
1. 删除 `MainWindowViewModel` 内重复的页面 VM `Lazy` 字段，把事件订阅改到 `NavigationPageModule` 的 `viewModelFactory` 内（解析后统一挂接）；
2. 或让 `RegisterPage` 的 `viewModelFactory` 改调 `MainWindowViewModel` 对应属性 getter。

同时把 `Dispose:514-529` 的 `IsValueCreated` 解绑逻辑一并收敛（当前这些 `IsValueCreated` 恒为 false，是死代码）。

---

## 三、P1 缺陷

### P1-1 `DataSourceAlarmTracker._states` 无锁，跨线程并发读写 ✅ 已验证

- **文件**：`Kanban.Collector.Core/Services/DataSourceAlarmTracker.cs:33-36`
- **类别**：concurrency

**问题**：`_states` 字典与 `_pendingReconnect` 布尔无任何锁保护，而同类 `AlarmStateTracker`/`DeviceStatusTracker` 均有 `_lock`——明显是漏加。

**证据**：`Observe()`（:85-88 轮询线程 `ScanSources` 写 `_states[key]=state`）与 `RemoveDevice()`（:290）/`RemoveSource()`（:298）/`ResetAll()`（:275）/`RecoverAllOnDisconnect()`（:257,265 `_states.Clear()`）由 UI 线程（设备管理删除）与后台线程（`Task.Run` 重置产量）调用。普通 `Dictionary` 并发读写会抛 `InvalidOperationException` 或损坏内部结构。

**建议**：仿照 `AlarmStateTracker` 加 `private readonly object _lock`，在 `Observe/ResetAll/RemoveDevice/RemoveSource/RecoverAllOnDisconnect` 内锁保护 `_states` 与 `_pendingReconnect`。

### P1-2 SQLite PRAGMA 拦截器用 `Task.Run` 跨线程执行 ✅ 已验证

- **文件**：`Kanban.Collector.Core/Data/DatabaseProvider.cs:441-442`
- **类别**：concurrency

**问题**：`ConnectionOpenedAsync` 把同步 `ConnectionOpened`（`CreateCommand`+`ExecuteNonQuery` 执行 4 条 PRAGMA）丢到线程池执行，而连接在调用线程 `Open`——`SqliteConnection` 明确非线程安全。若 `cancellationToken` 已取消，`Task.Run` 返回已取消任务、**PRAGMA 不执行**，则 `busy_timeout=5000`/`synchronous=NORMAL` 等保障缺失，并发写时 `SQLITE_BUSY` 概率上升。

**建议**：直接同步调用 `ConnectionOpened(connection, eventData); return Task.CompletedTask;`，去掉 `Task.Run`。

---

## 四、P2 缺陷

| # | 位置 | 类别 | 摘要 |
|---|------|------|------|
| P2-1 | `Collector.Core/Services/DeviceRuntime.cs:100` + `PlcDataAcquisitionService.cs:847` | concurrency | 会话字段（RunTime/AlarmTime/TotalOkProduction）被轮询线程与 `ResetDeviceProduction` 后台线程并发读改写，`_resetLock` 只保护 Reset↔Reset，不保护与轮询的互斥，存在 OEE/产量丢失更新 |
| P2-2 | `Collector.Core/Services/RecipeValidator.cs:62` | correctness | 配方校验用通用 `PlcAddressParser` 而非品牌 codec，Omron（W100/D100.05）/Keyence（DM100/MR100）原生地址被误判「无法解析」，配方无法下发 |
| P2-3 | `Collector.Core/Services/PlcConnectionManager.cs:184` | correctness | `EnsureConnected` 与 `Disconnect` 竞态：断开后 `IsConnected` 可能被置回 true，与实际 socket 状态不一致 |
| P2-4 | `Collector/Hubs/KanbanHub.cs:63-75` | security | `?operator=` 客户端自报无长度/内容校验，审计归属可被任意伪造（冒名 `?operator=admin`） |
| P2-5 | `LicenseIssuer.CLI/RevokeCommand.cs:13` + `LicenseManager.App/Services/LicenseGate.cs:147` | security | 撤销只在签发机本地标记，客户端从不校验吊销列表，被撤销激活码仍可继续使用 |
| P2-6 | `Collector/Hubs/KanbanHub.cs:100-115` | correctness | 报警/状态订阅方法缺 `OperationCanceledException` 处理，客户端正常断开时异常沿 Hub 方法上抛（快照订阅已有 catch，此处不一致） |
| P2-7 | `MainAPP/Views/AlarmCenterView.xaml.cs:25-30` | perf/leak | ✅ 报警中心定时器切页后永不停止：页面 host 常驻 ItemsControl（`Content is not null` 后不再卸载），`Unloaded` 不触发，`Stop()` 从不执行，3s+60s 轮询常驻 |
| P2-8 | `MainAPP/MainWindow.xaml.cs:242` | correctness | ✅ 关窗时无条件 `vm.DeviceManagerViewModel.TryCloseWithDirtyCheck()`，强制懒创建设备管理 VM 及服务链，违背懒加载初衷 |
| P2-9 | `Kanban.Web/DashboardState.cs:450` | correctness | 元数据设备集变更只按 `Count` 判断，删除+新增各一台（总数不变）时走增量分支，被删设备工单残留不清理 |
| P2-10 | `Kanban.Web/DashboardState.cs:381` | resource-leak | 设备 tombstone 删除只清 `_snapshots`，`_speedHistoryByDevice`/`_workOrdersByDevice` 等字典条目永不清除，长运行内存增长 |
| P2-11 | `Collector/Services/HistoryQueryHandler.cs:209-216` | correctness | ✅ 报警查询带 `AlarmId` 时走 `GetLatestAlarmEventStrict(alarmId)` 只取全局最新一条，**忽略 from/to 时间窗**（查「昨天+某报警」拿到今天记录）。注释称与 Local 语义对齐，属已知但易误导 |
| P2-12 | `Collector.Core/Services/ProductionHistoryStore.cs:66` 等 | correctness | `QueryLatestProductionLog`/`GetLatestProductionBeforeStrict`/`GetLatestStatusBeforeStrict` 只 `OrderByDescending(Timestamp)` 无 `ThenByDescending(Id)`，同刻多设备「最新」不确定 |
| P2-13 | `Collector.Core/Services/AlarmHistoryStore.cs:71` 等 | perf | 分页每页重算 `Count()`（全量扫描）+ `Skip(offset)`，深页（page=200）近似全量扫描；三 Store 一致 |
| P2-14 | `Collector.Core/Data/DatabaseProvider.cs:179-195` | maintainability | legacy patch 硬编码「已知新增」列/索引子集，旧 `EnsureCreated` 库缺其它模型列时 `IsSchemaCompatible` 抛异常阻断启动且不指明缺哪列 |

---

## 五、P3 缺陷

| # | 位置 | 类别 | 摘要 |
|---|------|------|------|
| P3-1 | `DataSourceAlarmTracker.cs:36,95-101` | correctness | `_pendingReconnect` 全局单标志，重连后仅首个值项消费它，其余值项丢失重连告警窗口 |
| P3-2 | `PlcScanPipeline.cs:151-208` + `AlarmStateTracker.cs:251-269` | perf | 批读计划签名每轮（200ms）重算：全量解析+排序+`string.Join`，缓存只跳过 `Plan` 一步 |
| P3-3 | `PlcAddressParser.cs:104,136` + `PlcBatchReadPlanner.cs:40` | robustness | 地址 `int.Parse` 超范围抛 `OverflowException`；`start.Number + length*step` 无 checked 可溢出 |
| P3-4 | `PlcAddressParser.cs` vs `Siemens/Omron/KeyenceAddressCodec.cs` | maintainability | 双地址解析实现分叉，规则/匹配顺序不一致（P2-2 的根因） |
| P3-5 | `Collector/Services/ConfigSyncHandler.cs:55/109/133/233/364` | perf | 多个标 Async 的写方法实际同步 IO/DB，跑在 Hub 调度线程，与 `ApplyRecipeAsync` 的 `Task.Run` 纪律不一致 |
| P3-6 | `ConfigSyncHandler.cs:73-77,382-383` | concurrency | `ReplaceAll→SaveAll` 两步非原子，并发写可交错造成内存/磁盘 lost update |
| P3-7 | `Collector.Core/Services/PasswordHasher.cs:44/50,60-81` | security | PBKDF2 iterations 从存储哈希解析无上界（篡改为 `int.MaxValue` 可致登录 CPU 打满）；旧 SHA256 哈希验证通过后不自动 rehash 升级 |
| P3-8 | `LicenseManager.App/Services/HardwareFingerprint.cs:91-92` | security | 机器码 SHA256 截断至 5 字节（40bit），绑定粒度偏弱 |
| P3-9 | `Kanban.Client/KanbanDataClient.cs:125/134/154` | concurrency | `_consecutiveFailures` 混用 `Interlocked.Increment` 与普通赋值，轻微竞态 |
| P3-10 | `MainAPP/ViewModels/RuntimeMonitoringViewModel.cs:279` + `SettingsViewModel.cs:842` | threading | ✅ 两处 `async void`，虽有 try/catch 兜底但调用方无法感知异常 |
| P3-11 | `MainAPP/MainWindow.xaml.cs:250-274` | correctness | ✅ 设备管理脏检查仅覆盖侧边栏 `SelectionChanged`，Ctrl+1~8 快捷键/程序化 `Navigate` 可绕过未保存确认 |
| P3-12 | `Kanban.Web/Services/HistoryFetch.cs:54` | resource-leak | `new List<T>(first.Total)` 按服务端 Total 预分配，大窗口内存峰值高（实际只拉 10 万条） |
| P3-13 | `Collector.Core/Services/ProductionHistoryWriter.cs:83-87` | correctness | `ReplayRecoveryAsync` 仅在 flush 循环收到新数据时调用，设备静止时恢复文件数据被无限推迟回放 |

---

## 六、设计层提示（非代码缺陷，建议书面确认）

1. **离线激活授权模型**：客户端用同一把对称 HMAC 密钥验签激活码，密钥必然下发到客户端，理论上可被离线 dump/读环境变量后自签激活码（`LicenseManager.App/Crypto/EmbeddedKey.cs` + `HmacValidator.cs`）。这是离线激活的固有属性——若要求防破解应改为非对称（客户端只内嵌公钥），否则建议在产品文档明确「仅防误改、不防破解」。
2. **Collector Hub 无认证 + 默认口令 + operator 免密**：此前已确认是用户有意接受的设计，本报告不重复列，但 P2-4 的 operator 自报是其连带影响，值得一并权衡。

---

## 七、修复优先级建议

1. **立即**：P0-1 导航事件回归（影响核心交互，改动小、风险低）。
2. **本迭代**：P1-1（加锁）、P1-2（去 Task.Run）、P2-7/P2-8（定时器与关窗懒加载破坏）、P2-2（配方地址误判）、P2-11（AlarmId 时间窗）。
3. **下迭代**：P2 其余（并发读改写、tombstone 泄漏、稳定排序键、深分页）、P3-1/P3-6（并发一致性）。
4. **backlog**：P3 其余 + 设计层确认项。
