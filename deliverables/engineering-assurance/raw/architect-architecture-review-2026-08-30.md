# Archi 架构影响评估报告 - Kanban 全解决方案

**日期**：2026-08-30
**评估人**：阿奇（Archi）· 系统架构师
**范围**：全解决方案（MainAPP 194 / Kanban.Collector.Core 145 / Kanban.Collector 16 / Kanban.Web 31 / Kanban.Contracts 28 / LicenseManager.App 18 / Kanban.Client 3 / Kanban.Analysis 2 / LicenseIssuer.Wpf 4 / PlcSimulator 5 / ci 25，共 471 个源文件）
**排除**：`oxyplot/`（第三方）、MainAPP.Tests / MainAPP.E2E / MainAPP.Benchmarks / MainAPP.UIAutomation / PlcSimulator.Tests、`bin/`、`obj/`
**方法**：静态阅读 + 跨项目调用链追踪 + 与上两轮产出（`review-full-2026-08-17.md`、`architect-architecture-review-2026-08-16.md`）逐项比对

**证据标注约定**：
- **【已验证】**= 本轮实际打开文件读到对应行、或经跨文件调用链追踪确认
- **【推断】**= 基于设计结构推断，未找到直接代码证据

---

## 一、上轮架构风险整改进展复查

### 1.1 Archi 2026-08-16 轮的架构债与 ADR 提案

| 上轮编号 | 风险 / 提案 | 当前状态 | 证据 |
|---|---|---|---|
| 债 #12 | 管理域与监控域同 Hub 同连接（中优先级） | ✅ **已解决** | 已拆出独立 `KanbanAdminHub`：`Kanban.Collector/Hubs/KanbanAdminHub.cs:15` `public sealed class KanbanAdminHub : Hub<IKanbanHubClient>, IKanbanAdminServer`，独立路径注册于 `Kanban.Collector/Program.cs:210` `MapHub<KanbanAdminHub>(KanbanHubPaths.AdminHubPath)`。类注释明确"与只读 KanbanHub 使用不同路径，监控连接不会暴露这些方法" |
| 债 #10 | HslCommunication 裸 DLL（lib\，3 项目引用） | 🟡 **部分改善** | 3 个项目仍裸引用（`MainAPP/MainAPP.csproj:83`、`Kanban.Collector/Kanban.Collector.csproj:33`、`Kanban.Collector.Core/Kanban.Collector.Core.csproj:35`、`PlcSimulator/PlcSimulator.csproj:18`）；但最新提交 `119eb69 Track licensed HslCommunication.dll for out-of-box builds.` 说明已开始版本跟踪。**【推断】**：仍无版本号锁定文件 |
| 债 #9 | MainAPP 引用 Collector.Core 支撑 Local 模式 | ❌ **未动** | `MainAPP/MainAPP.csproj:107` `ProjectReference ..\Kanban.Collector.Core\...` 仍在；`IsRemote` 分支数从 67 处（18 文件）**增长到 70 处**（15 个生产文件 + IRuntimeMode 定义）【已验证，grep 计数】 |
| 债 #11 | oxyplot 本地源码 4 项目入 slnx | ⬜ **未复查**（本轮范围排除 oxyplot） | — |
| ADR-8 提案 | Local/Remote 双模式退役条件与冻结 | ❌ **未采纳** | `docs/架构决策记录.md` 中 ADR-8 编号已被"数据采集容量基线"占用（文档第 61-93 行）；冻结未生效——`IsRemote` 计数不降反升（`RemoteHistoryQueryService.cs` 独占 33 处，上轮 29 处） |
| ADR-9 提案 | SignalR 重连所有权分层 | ❌ **未成文，但代码已落实** | ADR-9 编号被"数据源协议 reader"占用（文档第 97-111 行）。代码层约定已固化：三处单实例守卫 `RemoteRuntimeSink.cs:141-142 _eventsRetryLoopStarted` / `:245-246 _subscribeRetryLoopStarted`（并在 `:294` finally 复位）、`StartEventsRetryLoop():149-179` 5s 循环、"重连所有权归调用方"注释体系完整 |
| ADR-10 提案 | Hub 信任边界（LAN 无认证是有意负债） | ❌ **未采纳** | `Program.cs:209-210` MapHub 无 `RequireAuthorization`；`:157-158` CORS `SetIsOriginAllowed(_ => true)`；`KanbanAdminHub.cs:180-194` `ResolveOperator()` 仍从 `?operator=` 查询串取值且无校验 |
| ADR-11 提案 | 6 库分立 + SQLite 适用边界 | 🟡 **部分被 ADR-8 吸收** | ADR-8（容量基线）已把"队列积压/恢复文件/WAL/单库 2GB"写成运行判据（文档第 86-91 行），实质承担了 ADR-11 的度量职能；但"7 库分立是否合并"的边界声明仍缺失。库数已从 6 → 7（`DatabaseProvider.cs:30-39`，新增 `datasource_snapshots.db`） |
| 风险 1 附带建议 | 数据目录（%APPDATA%/Kanban）无备份，建议每日 robocopy/online backup | ❌ **未落实** | `ci/` 下 13 个 ps1 脚本中未发现任何备份相关脚本【已验证：`grep -rln "backup\|robocopy" ci/*.ps1` 无输出】 |
| 决策复审 #5 | 6 库分立的保留策略 | ✅ **维持且增强** | 新增 `ADR-8` 的运行判据表 + `HistoryRetentionService`（`Program.cs:98`）+ `DatabaseProvider.CheckpointAll():445-458` |

### 1.2 2026-08-17 全量审查中架构相关缺陷的修复状态

| 上轮编号 | 缺陷 | 当前状态 | 证据 |
|---|---|---|---|
| **P0-1** | 跨页导航事件从未订阅（双 VM 创建路径） | ✅ **已修复**，但残留架构隐患（见 R2） | 新增 `MainWindowViewModel.AttachPageViewModel(object)`（`:489-509`），唯一调用点 `MainWindow.xaml.cs:99`，启动期由 `MainWindow.xaml.cs:47 ActivatePage(viewModel.SelectedIndex)` 保证首屏挂载；`_attachedPageViewModels` HashSet（`:482`）做幂等守卫，`Dispose():556-589` 改为遍历该集合解绑（不再是恒 false 的 `IsValueCreated` 死代码） |
| **P1-1** | `DataSourceAlarmTracker._states` 无锁跨线程读写 | ❌ **未修复**（本轮重新确认并补全调用链，见 R1） | `DataSourceAlarmTracker.cs` 全文件无 `lock` 关键字（`_states` 定义 `:33`、`_pendingReconnect` `:36`，写入 `:97`，清除 `:274/:292/:300/:308`）；同类 `AlarmStateTracker.cs:37` 与 `DeviceStatusTracker.cs:24` 均有 `_lock` |
| **P1-2** | PRAGMA 拦截器 `Task.Run` 跨线程执行 | ✅ **已修复** | `DatabaseProvider.cs:523-528`：`ConnectionOpenedAsync` 改为 `cancellationToken.ThrowIfCancellationRequested(); ConnectionOpened(connection, eventData); return Task.CompletedTask;`，`Task.Run` 已去除 |
| **P2-2** | 配方校验用通用 `PlcAddressParser` 而非品牌 codec | ❌ **未修复** | `RecipeValidator.cs:62` 仍为 `var parse = PlcAddressParser.Parse(item.PlcAddress);`；同文件 `:66` 用 `parse.Type != expectedType` 判定。Omron/Keyence 原生地址误判路径未变 |
| **P2-4** | `?operator=` 自报无校验 | ❌ **未修复** | `KanbanAdminHub.cs:186-188`：`var fromQuery = http?.Request.Query["operator"].ToString();` 无长度/字符/白名单校验（见 O1） |
| **P2-5** | 撤销只在签发机本地标记，客户端不校验吊销列表 | ❌ **未修复** | `LicenseGate.EvaluateStoredLicense():100-115` 只做签名重验 + 过期 + 机器码绑定三件事，无任何吊销查询；`LicenseGate` 全类无网络/吊销表访问（见 R9） |
| **P2-7** | 报警中心定时器切页后永不停止 | ❌ **未修复**（本轮定位到架构层根因，见 R3） | `AlarmCenterView.xaml.cs:19-28` 仍靠 `Loaded`/`Unloaded` 驱动 `vm.Start()/Stop()`；而 `NavigationPageHost.EnsureViewLoaded():70-71` 一旦 `Content is not null` 即 return、且从不置回 null → `Unloaded` 永不触发；`AlarmCenterViewModel` 未实现 `INavigationPageLifecycle`（仅 `HomeViewModel.cs:35`、`RuntimeMonitoringViewModel.cs:49`、`DataSourceMonitoringViewModel.cs:142` 三个实现，共 14 个页面 VM） |
| **P2-8** | 关窗时无条件 `vm.DeviceManagerViewModel.TryCloseWithDirtyCheck()` 强制懒创建 | ❌ **未修复** | `MainWindow.xaml.cs:246` 仍为无条件属性取值（懒加载绕过点）；与 `Navigate()` 中 `:331-333` 的短路写法（先判 `SelectedIndex == DeviceManager.Index` 再触达 VM，注释"审查修复 2026-08-30"）形成鲜明对比——后者是对的，前者漏改 |
| **P2-11** | `AlarmId` 查询忽略 from/to 时间窗 | 🟡 **已承认并文档化**（行为未变） | `HistoryQueryHandler.cs:205-217`：`GetLatestAlarmEventStrict(request.AlarmId)` 不传时间参数，注释明确"与 Local 模式 GetLatestAlarmEvent（全局最新）语义对齐"。**语义刻意，但接口契约易误导** |
| **P3-6** | `ReplaceAll→SaveAll` 两步非原子 | 🟡 **部分修复** | 设备配置路径已收敛为 `ReplaceAllAndSave` 并在锁内（`ConfigSyncHandler.cs:97`、`:133`，受 `_deviceConfigLock` 保护 `:90/:121/:156`）；**配方路径仍为两步**：`ConfigSyncHandler.cs:465-466` `_recipeStore.ReplaceAll(entities); _recipeStore.SaveAll();`（见 R7） |
| **P3-13** | `ReplayRecoveryAsync` 仅在收到新数据时调用 | 🟡 **部分修复** | 新增停机排空：`ProductionHistoryWriter.cs:114-122`（注释"停机排空…此前仅在循环内回放"）；但空闲期路径仍未覆盖——`:102-105` 的 `catch (TimeoutException)` 分支只调 `FlushPendingAsync`，**不调 `ReplayRecoveryAsync`**（见 R6） |
| 设计层 #1 | 离线激活对称 HMAC 密钥必然下发客户端，可被 dump 自签 | ✅ **已根治（本轮最重要的架构改进）** | 迁移到 **ECDSA P-256 非对称**：`LicenseSigningKey.cs:16-17` 客户端只内嵌公钥 `PublicKeyBase64`（注释"公钥可以公开，私钥绝不写入客户端"）；私钥仅存签发机 AppData（`:42-53`），且 `LoadSigner()` 用 `CryptographicOperations.FixedTimeEquals`（`:62`）校验私钥与内置公钥匹配。旧 HMAC 降级为兼容路径（`EmbeddedKey.cs:14` 注释"新版 ECDSA 正式激活不依赖此密钥"） |
| 设计层 #2 | Hub 无认证 + operator 免密 | ⬜ 用户已确认有意接受，本轮仅在 O1/R9 记录连带影响 | — |

**小结**：上轮 4 项架构建议中，**债 #12（管理域拆分）已落地、设计层 #1（非对称授权）已根治、P0-1/P1-2/P3-13/P3-6 部分修复**；**ADR-8/9/10/11 四条成文建议全部未进入 `docs/架构决策记录.md`**（其中 ADR-8/9 编号被其他主题占用），仍停留在代码注释层。P1-1（并发）、P2-7（生命周期）、P2-8（懒加载破坏）三条上轮已定位的缺陷**零进展**。

---

## 二、架构风险清单

### R = 可靠性 / O = 可观测·可核验性 / C = 配置 / E = 扩展性

| # | 类别 | 位置 | 风险描述 | 影响 | 建议（含 ADR 建议） |
|---|---|---|---|---|---|
| **R1** | R | `Kanban.Collector.Core/Services/DataSourceAlarmTracker.cs:33,36,94-97,266-308`；写入方 `PlcScanPipeline.cs:519/540/547` 与 `PlcDataAcquisitionService.cs:1151/1242/1271`；UI 入口 `MainAPP/ViewModels/DeviceManagerViewModel.cs:391` | **采集状态容器线程安全约定在同一族内不一致**。`_states` 与 `_pendingReconnect` 无任何锁保护。已追踪到确定并发链：**UI 线程** `DeviceManagerViewModel.cs:391 RemoveDeviceState` → `PlcDataAcquisitionService.cs:1263` → `_scanPipeline.RemoveDeviceAlarms` → `PlcScanPipeline.cs:547` → `DataSourceAlarmTracker.RemoveDevice:292/300`（`_states.Clear()` / `_states.Remove(key)`）；**轮询线程** `PlcScanPipeline.ScanSources` → `DataSourceAlarmTracker.Observe:97`（`_states[key] = state`）。同类 `AlarmStateTracker.cs:37`、`DeviceStatusTracker.cs:24` 均有 `_lock`，唯独此类漏加【已验证】 | `Dictionary` 并发读写可抛 `InvalidOperationException`、损坏内部结构甚至死循环；异常在 `TryScan`（`PlcDataAcquisitionService.cs:636-655`）被吞为业务异常 → **采集静默降级、数据源告警永久失准**，且无告警 | 仿照 `AlarmStateTracker` 补 `private readonly object _lock`，覆盖 `Observe/ResetAll/RemoveDevice/RemoveSource/RecoverAllOnDisconnect/GetConfirmedStates`。**ADR-11**：立"采集管线状态容器线程所有权与锁约定"，并把"同族 tracker 必须加锁"写进 `ci/architecture-gates.ps1` |
| **R2** | R | `MainAPP/Controls/NavigationPageHost.cs:68-77` vs `MainAPP/MainWindow.xaml.cs:47,95-106`；`MainAPP/ViewModels/MainWindowViewModel.cs:489-509` | **页面 VM 仍有两条创建路径，attach 挂在其中之一上**。`NavigationPageHost.EnsureViewLoaded():73-75` 经 `Page.EnsureView()` + `Page.ViewModel` 创建 VM 并赋 `DataContext`，**但从不调用 `AttachPageViewModel`**；attach 的唯一调用点是 `MainWindow.ActivatePage()`（`:99`）。当前正确性依赖"SelectedIndex 变更 → ActivatePage"必然先于"host 可见 → EnsureViewLoaded"的 WPF 时序，而该时序**没有任何断言或测试锁定**。另：`AttachPageViewModel` 是 `switch` 类型匹配，新增带跨页事件的页面 VM 若忘加 case，**编译通过、运行静默失效**【已验证：全仓 grep `AttachPageViewModel` 仅 1 个调用点】 | 与 P0-1 同源的**静默失效**风险；一次导航重构即可让"查看详情/工单管理/设备卡跳转/报警历史"全部再次失效，且无测试能拦住 | 把 attach 收敛进唯一创建点：让 `RegisterPage`（`MainAppPresentationServiceCollectionExtensions.cs:141-146`）的 `viewModelFactory` 变成 `() => { var vm = sp.GetRequiredService<TViewModel>(); onCreated(vm); return vm; }`，`NavigationPageHost` 与 `ActivatePage` 无论谁先触发都必然 attach。**ADR-12**：页面 VM 单一创建路径与生命周期契约 |
| **R3** | R | `MainAPP/Views/AlarmCenterView.xaml.cs:19-28`；`MainAPP/Controls/NavigationPageHost.cs:70-76`；`INavigationPageLifecycle` 实现仅 3/14（`HomeViewModel.cs:35`、`RuntimeMonitoringViewModel.cs:49`、`DataSourceMonitoringViewModel.cs:142`） | **两套页面生命周期机制并存，且其中一套与架构不兼容**。框架提供 `INavigationPageLifecycle`（`NavigationPageModule.cs:8`，由 `MainWindow.xaml.cs:101-105` 在导航时驱动 `OnPageEnter/OnPageExit`），但同时存在 WPF 原生的 `Loaded`/`Unloaded` 用法。由于 **View 是 Singleton（`MainAppPresentationServiceCollectionExtensions.cs:140 AddSingleton<TView>()`）+ host 一旦加载 Content 永不还原**，页面的 `Unloaded` 永不触发 → 任何用 `Unloaded` 做停止钩子的页面资源泄漏。`AlarmCenterViewModel` 未实现 `INavigationPageLifecycle`，其 3s/60s 轮询定时器自首次进入后常驻到进程结束【已验证】 | 后台轮询与 DB 查询常驻，CPU/IO 持续消耗；多页叠加后放大。且这是**系统性陷阱**——下一个用 `Unloaded` 写停止逻辑的人会再次踩坑 | ① `AlarmCenterViewModel` 改实现 `INavigationPageLifecycle`，`Start/Stop` 迁到 `OnPageEnter/OnPageExit`；② 在 `ci/architecture-gates.ps1` 增加规则：MainAPP Views 下禁止出现 `Unloaded +=` 驱动业务停止（当前该脚本仅检查 2 条：`architecture-gates.ps1:11-23`）。**ADR-12** 一并成文 |
| **R4** | R/O | `Kanban.Collector.Core/Services/PlcDataAcquisitionService.cs:359-583`（主循环），`:262-274`（仅 StopAsync 有 15s 超时） | **采集主循环无单轮看门狗**。循环体全是同步 PLC IO；若 `RefreshDeviceData` 卡在 `ReadInt32`（HSL 同步调用），`CancellationToken` 无法中断（代码自承认：`PlcDataAcquisitionService.cs:260-261` 注释"若采集线程卡在同步 PLC IO，CancellationToken 无法中断同步调用"）。当前只有**停机时**的 15s 超时保护，**运行期没有任何"本轮耗时超阈值"的检测与告警** | 采集静默停摆，只能靠 `/health/ready` 的诊断新鲜度**间接**发现（`Program.cs:176-185` + `CollectorWorker.cs:59-69`）；定位成本高（只知"不新鲜"，不知"卡在哪个扫描阶段"） | 在循环内加单轮计时：进入记录 `Stopwatch`，`CompleteDiagnosticsCycle():585-588` 时若超阈值（如 5×PollingIntervalMs）记 `LogError` 并 `RecordFailure`；复用已有的分段计时（`dwordRead/alarmRead/defectRead/counterAlarmRead/historyWrite`，见 `:499-504`）输出卡点阶段。**ADR-13** 补一条"采集循环可观测性基线" |
| **R5** | R | `Kanban.Collector/Services/CollectorWorker.cs:108-116`（含回归自述注释）、`:158-165`；`AuditLog` 静态初始化 `:140` | **两阶段启动初始化（配置 Load 必须先于依赖构造）靠人工顺序 + 注释维系**。`CollectorWorker.cs:108-111` 的注释直接记录了一次真实回归：单例构造期读到未 Load 的默认 `PlcConfig`（127.0.0.1）→ "单例档案被永久污染，采集永远连不上真实 PLC（回归自 04e8442）"。补丁是 Load 之后手工 `RefreshFromSettings()`。同类顺序依赖还有：事件接线必须在 Load 后（`:158-161`）、`AuditLog.Initialize` 必须在任何可审计操作前（`App.xaml.cs:206-210`） | MS.DI 没有"阶段"概念，**任何新加的、在构造函数里读 AppSettings 的单例都会被默认值静默污染**，无编译错误、无测试失败，只在现场表现为"连不上 PLC" | ① **ADR-13**：立"两阶段初始化约定"——构造函数禁止读取 AppSettings 业务值，一律改为惰性读取或 `IHostedService.StartAsync` 阶段注入；② 在 `CollectorWorker.InitializeAsync` 末尾加一次自检（比对 `PlcRuntimeProfileProvider.Current.Config.IpAddress` 与 `settings.PlcConfig.IpAddress`，不一致即 `LogCritical`） |
| **R6** | R | `Kanban.Collector.Core/Services/ProductionHistoryWriter.cs:96-105`（空闲路径）、`:114-122`（已修的停机排空） | **恢复文件在设备静止期不回放**。`FlushLoopAsync` 的 `catch (TimeoutException)` 分支（`:102-105`）只调 `FlushPendingAsync`，`ReplayRecoveryAsync` 只在"成功等到新数据"（`:100`）和"停机排空"（`:122`）两处调用。若上一轮批写失败转存了恢复文件、随后设备停产（无新 `LogProduction`），恢复数据会一直滞留 | 断线/故障期间的生产快照延迟到下次有数据或进程重启才补回；与 R4 叠加时（循环卡死）恢复完全停滞 | 把 `ReplayRecoveryAsync(ct)` 移入 `TimeoutException` 分支（与 `:104` 的 `FlushPendingAsync` 并列）。改后建议补一条测试：转存恢复文件 → 停止生产 → 等待 2 个 Flush 周期 → 断言恢复文件已清零 |
| **R7** | R | `Kanban.Collector/Services/ConfigSyncHandler.cs:465-466`（配方）；对照已修的设备配置 `:97/:133` | **配方全量替换仍为 `ReplaceAll` + `SaveAll` 两步非原子**，未走设备配置已收敛的 `ReplaceAllAndSave` 单步路径。两步之间若并发写入（Hub 多线程；`MaximumParallelInvocationsPerClient=16`，`Program.cs:109`）或进程崩溃，内存与磁盘会分裂 | 内存态与 recipes.json 不一致；恢复依赖重启重载，现场表现为"配方无故回滚/丢失" | 对齐设备配置做法：在 `IRecipeStore` 上加 `ReplaceAllAndSave`，`ConfigSyncHandler:465-466` 改为单步调用并纳入 `_recipeLock`（若不存在则新增），与 `_deviceConfigLock`（`:90`）保持同一套纪律 |
| **R8** | R | 数据目录 `%APPDATA%/Kanban`（`Kanban.Collector/Program.cs:262-271` + `AppSettings` 同布局）；`ci/` 全部脚本 | **7 个 SQLite 库 + devices.json + settings.json + baselines.json 无任何备份机制**（上轮已提，本轮复查仍未落实）。已确认 `ci/` 下无备份脚本，仓库内也无 SQLite online backup 调用 | 单磁盘故障 = 全部历史与配置丢失；这是当前**单点风险中最大的一项**，且消除成本远低于"多 Collector HA" | 加 `ci/backup-data.ps1`（`sqlite3 .backup` 或 `VACUUM INTO` + 配置目录 robocopy），由计划任务/服务恢复策略调用；写入部署文档。**ADR-15** 把"备份是部署契约的一部分"成文 |
| **R9** | O | `LicenseManager.App/Services/LicenseGate.cs:100-115`（`EvaluateStoredLicense`）；`LicenseIssuer.Wpf/IssuerStore.cs`、`IssuedRecord.cs` | **授权吊销在客户端无任何校验点**（P2-5 未修）。`LicenseGate` 的判定面只有：签名重验（`ProductKeyCodec.TryDecode`）、过期、机器码绑定；`LicenseIssuer.Wpf` 的签发记录只落在签发机本地。离线激活模型下这**在架构上是固有缺口**——客户端无法访问吊销列表 | 已签发后被撤销的激活码可继续使用到过期；商业风险而非安全风险。同时 `MainAPP/App.xaml.cs:150-158` 的门禁**只在启动期强制**，运行期只有 `SettingsViewModel` 的 60s 只读刷新（`:489`、`:612-614`），无"到期即降级/阻断"的运行时强制点【已验证：`grep LicenseGate MainAPP` 无运行期拦截】 | ① 产品决策：若接受"离线激活不防吊销"，写进 `docs/授权激活码设计方案.md` 明确边界（与"仅防误改、不防破解"同类声明）；② 若需要吊销：随激活码下发一份**服务端签名的短有效期凭证**（如 30 天），到期强制联网续期；③ 至少补一个运行期到期降级钩子。**ADR-15** 记录取舍结论 |
| **O1** | O | `Kanban.Collector/Hubs/KanbanAdminHub.cs:180-194` | **操作人身份自报且零校验**（P2-4 未修）。`ResolveOperator()` 依次取 `Context.User?.Identity?.Name`（无认证中间件时恒为 null）→ `?operator=` 查询串，无长度/字符/存在性校验，直接写入审计记录 | 审计数据**不可用于追责**，只能当操作留痕；可冒名 `?operator=admin`。与上轮 ADR-10 提案（Hub 信任边界）为同一根因 | 短期：加长度上限（如 64）+ 拒绝控制字符/换行（防日志注入）；中期随 Hub 认证（ADR-15 / 原 ADR-10 提案）改为服务端身份解析 |
| **O2** | O | `docs/架构决策记录.md:11`（ADR-1） vs `MainAPP/Services/RemoteRuntimeSink.cs:268-275` | **ADR-1 与代码现实漂移**。ADR-1 规定"SignalR 每个 Hub 连接最多持有一个长驻订阅流"；而 `RemoteRuntimeSink` 在**同一条 `_eventsClient` 连接上并行启动三个长驻订阅**（`alarmTask`/`statusTask`/`metaTask`，`:270-275`，注释"三个长驻订阅必须**并行**启动"）。其能工作依赖服务端 `MaximumParallelInvocationsPerClient = 16`（`Program.cs:109`），而**该前置条件未写进 ADR-1** | ADR 失准 → 后来者按 ADR-1 拆连接（成本与复杂度上升）或按代码加订阅（在 ADR 不知情的前提下触碰服务端并发上限）。文档与代码互相矛盾是架构债的典型形态 | **ADR-10（修订 ADR-1）**：保留"按优先级隔离"原则，但明确"单连接多长驻订阅的成立条件是服务端 `MaximumParallelInvocationsPerClient > 订阅数`，且必须在 Hub 注册处显式配置"；给出连接预算表（WPF：`_client` 快照 + `_eventsClient` 三流 + `RemoteHistoryQueryService` 独立 `_invokeClient`；WASM：`_client` + `_metaClient` + `_invokeClient`） |
| **C1** | C | `MainAPP/Services/MainAppServiceCollectionExtensions.cs:96-107` | **6 个历史接口"无条件"重定向到 `RemoteHistoryQueryService`，依赖 MS DI"后注册胜出"语义**。注释自认"刻意行为…依赖 MS DI 后注册胜出语义"，并要求"新增历史接口时须在此同步重定向"——这是**纯人工约定、无测试锁定、无编译保护** | 漏加一行 → Local 模式静默走远程查询（在 Local 场景下直接连不上或查错库），故障表现与配置问题难以区分；且一旦有人调整注册顺序（`AddKanbanDataServices` 与 `AddMainAppCoreServices` 的调用次序）行为整体翻转 | ① **ADR-14**：把"重定向清单"作为显式契约，要求每个被重定向接口在 `RemoteHistoryQueryService` 上必须有对应成员；② 加一条架构测试：反射扫描 `Kanban.Collector.Core` 中所有 `I*History*/I*Reader*` 接口，断言 `MainAppServiceCollectionExtensions` 中存在对应重定向注册；③ 或改用 `TryAddEnumerable`/显式代理包装，摆脱顺序依赖 |
| **C2** | C | `Kanban.Collector.Core/Data/DatabaseProvider.cs:19-27` | **硬编码 EF 产品版本与迁移 ID 常量**：`EfProductVersion = "10.0.10"` 及 7 个 `InitialMigration` 字符串（如 `ProductionLogInitialMigration = "20260731070824_InitialSchema"`） | EF Core 包升级后 `EfProductVersion` 与实际版本失配；迁移被重命名/合并后基线插入失败且错误信息指向不明。属"能跑但脆" | 从 `Microsoft.EntityFrameworkCore` 程序集版本反射读取 `EfProductVersion`；迁移 ID 集中到一处并在注释声明"重命名迁移必须同步更新此处"。优先级低 |
| **C3** | C | `MainAPP/MainWindow.xaml.cs:246`（关窗强制懒创建）；`:260-261`（脏检查硬编码 `oldItem.Index != 3` / `newItem.Index == 3`） | **懒加载破坏 + 魔法数字**并存（P2-8 未修）。关窗路径无条件取 `vm.DeviceManagerViewModel` 强制实例化整个设备管理 VM 及其服务链；`OnNavigationSelectionChanged` 用字面量 `3` 代表设备管理页，而正确的引用方式（同文件其他位置）是 `NavigationPageCatalog.DeviceManager.Index` | 关窗慢（构造重型服务链）；`NavigationPageCatalog` 顺序调整后脏检查静默失效，未保存配置丢失 | `:246` 改为 `if (vm.SelectedIndex == NavigationPageCatalog.DeviceManager.Index && !vm.DeviceManagerViewModel.TryCloseWithDirtyCheck())`；`:260-261` 的 `3` 替换为 `NavigationPageCatalog.DeviceManager.Index` |
| **C4** | C | `PlcSimulator/DeviceConfig.cs:8-45`；`PlcSimulator/Program.cs:162-193`、`:959-1000`；对照 `Kanban.Collector.Core/Models/Device.cs` | **设备配置双真相源 + 仿真器越权写 Collector 配置文件**。`DeviceConfig.cs:6-8` 自建"与 MainAPP Device 模型对齐的精简版"模型（`AlarmConfig`/`DefectConfig`/`CounterAlarmConfig` 仍为本地镜像，仅 `Sources` 已改用 Contracts 的 `DataSourceConfigDto`）；`Program.cs:186-193` 在 `devices.json` 缺失时**写入自建默认配置**——而该文件按 ADR-2 是 Collector 独占写者；`CreateDefaultDevices():959` 是第二套硬编码默认设备（"注塑机1"/D100/D102…） | ① 直接违反 ADR-2 单写者（唯一写者=Collector）；② `Device` 模型新增/改名字段时，仿真器静默失配（不报错，只是不生效），仿真与真机行为分叉——这正是仿真器最容易骗人的失败模式；③ 默认设备模板有两处维护点 | ① 让 `PlcSimulator` 复用 `Kanban.Contracts` 的 `DeviceConfigDto`（已完成 Sources 部分，推广到 Alarms/Defects/CounterAlarms）；② 缺失 `devices.json` 时**只读内置默认并提示**，不落盘；若确有"初始化种子"需求，改由 Collector 或 `ci/` 脚本统一提供种子文件。**ADR-16**：仿真器与产品共享设备配置契约 |
| **C5** | E | `Kanban.Web/Kanban.Web.csproj:31-36`（只引用 Kanban.Client / Kanban.Analysis） vs `Kanban.Web/Program.cs:18`（使用 `Kanban.Contracts.KanbanHubPaths`） | **WASM 端经由传递依赖使用 Contracts**，未在 csproj 中显式声明 | 一旦 `Kanban.Client` 调整引用关系，Web 端编译在不知情处断裂；ADR-6 要求"新跨进程契约放 Contracts"，隐含各消费端应显式引用 | 在 `Kanban.Web.csproj` 增加 `<ProjectReference Include="..\Kanban.Contracts\Kanban.Contracts.csproj" />`。改动 1 行，收益是契约依赖显式化 |
| **C6** | E | `MainAPP/ViewModels/MainWindowViewModel.cs:410,465-466`（注入 `IServiceProvider` 并 `GetRequiredService<T>()`）；`Kanban.Collector/Services/CollectorWorker.cs:112-155`；`Kanban.Collector.Core/Services/AuditLog.cs:12,32` + 初始化点 `App.xaml.cs:208-210`、`CollectorWorker.cs:140`；`MainAPP` 生产代码 78 处 `GetRequiredService/GetService` | **服务定位器 + 静态环境上下文削弱了架构边界的可验证性**。Shell VM 直接持有容器（`_serviceProvider`）而非显式依赖；`AuditLog` 是静态可变单例，两处进程各自 `Initialize`，未初始化时静默丢弃审计（`CollectorWorker.cs:138-140` 注释"未初始化时静默忽略"） | 依赖关系不可静态分析（无法用 DI 图校验），`ci/architecture-gates.ps1` 这类门禁无处着力；`AuditLog` 的静默丢弃意味着**审计可能缺失而无人知晓**——对有审计需求的系统是隐蔽风险 | ① `MainWindowViewModel` 的 10 个 Lazy 改为接受显式依赖（至少把 `GetService<T>()` 收敛到一个 `PageViewModelFactory`）；② `AuditLog` 未初始化时改为记一次 `LogWarning`（限频），而非完全静默。**ADR-14** 一并约定"DI 边界：ViewModel 不注入容器" |
| **C7** | E | `MainAPP/Services/RemoteRuntimeSink.cs:82-83`；`Kanban.Web/DashboardState.cs:62,368-369` | **`KanbanDataClient` 在容器外被手工 `new`**（WPF 事件连接、WASM 的 `_metaClient`/`_invokeClient`）。容器只托管了"主连接"那一个实例 | 这些连接的生命周期、日志工厂、配置来源都不走 DI，无法统一治理（如统一加认证 header、统一超时策略）；与 ADR-6"共享 DI 入口只有 AddKanbanDataServices"的精神相悖 | 引入 `IKanbanDataClientFactory`（DI 注册），按用途命名（`AddNamed`/keyed 或 `Snapshot`/`Events`/`Invoke` 三个委托），把 URL/协议/超时集中到一处。**ADR-10** 连接预算表一并固化 |

---

## 三、架构决策记录（ADR）建议

> 现有 ADR 编号已用到 ADR-9（`docs/架构决策记录.md` 变更记录，最后更新 2026-08-20）。以下从 **ADR-10** 起顺延。注意：我上轮（2026-08-16）建议的 ADR-8/9/10/11 编号已被其他主题占用，本轮重新编号并合并。

### ADR-10：SignalR 订阅拓扑与连接预算（修订 ADR-1）

**背景**
ADR-1 规定"每连接最多一个长驻订阅流"，理由是同连接多 `await foreach` 长驻读取会互相延迟。但 `RemoteRuntimeSink.cs:268-275` 实际在单条 `_eventsClient` 上并行启动三个长驻订阅（报警/状态/Meta），注释说明"必须并行启动"——否则串行 `await` 会让后两个订阅永远不可达。其成立依赖服务端 `MaximumParallelInvocationsPerClient = 16`（`Program.cs:109`，注释记录了默认 1 会导致长驻订阅永久占槽）。文档与代码已相互矛盾。

**决策**
1. 保留"按优先级隔离"的原则，**但承认单连接多长驻订阅为合法模式**，并写明成立条件：服务端必须显式设置 `MaximumParallelInvocationsPerClient > 该连接上的长驻订阅数`；该设置与订阅数必须在一起维护（改任一方都要回看另一方）。
2. 固化连接预算表：

| 端 | 连接 | 承载 | 备注 |
|---|---|---|---|
| WPF | `_client`（DI 单例） | 快照流（唯一长驻）+ 首次全量 Invoke | `MainAppServiceCollectionExtensions.cs:79` |
| WPF | `_eventsClient`（手工 new） | 报警 + 状态 + Meta 三流 | `RemoteRuntimeSink.cs:82`；ADR-7 要求 Invoke 不得在此连接 |
| WPF | `_invokeClient` | 历史查询/管理写 Invoke | `RemoteHistoryQueryService`（ADR-7） |
| WASM | `_client` | 快照流 | `DashboardState.cs:11` 注释 |
| WASM | `_metaClient` | Meta 流 | `DashboardState.cs:62` |
| WASM | `_invokeClient` | 查询 Invoke | `DashboardState.cs:368` |

3. 三流的**启动必须并行**（`Task.WhenAll` 而非串行 `await`），此约束从代码注释升格为 ADR。

**后果**
- 变容易：后来者知道可以在一条连接上放多流，不必盲目拆连接；知道拆/合的判据在服务端并发设置。
- 变困难：新增长驻流时必须同步检查 `MaximumParallelInvocationsPerClient`，并更新预算表。
- 需重新审视：若将来引入 Hub 认证或连接数上限，预算表要重算。

**备选方案**
(a) 严格执行 ADR-1，把报警/状态/Meta 拆成三条连接——被否决：连接数翻倍，且低频 Meta 独占一条连接是浪费；当前服务端并发设置已让单连接多流稳定工作。
(b) 保持 ADR-1 不动、只在代码加注释——被否决：这正是当前状态，文档与代码矛盾已造成理解成本。

---

### ADR-11：采集管线状态容器的线程所有权与锁约定

**背景**
`Kanban.Collector.Core` 中同类状态容器锁纪律不一致：`AlarmStateTracker`（`:37`）、`DeviceStatusTracker`（`:24`）、`PlcConnectionManager`（`:62-63`，且已细分为状态锁 + 驱动操作锁）都有锁；`DataSourceAlarmTracker` 没有（R1）。而它的访问确实跨线程：轮询线程写（`PlcScanPipeline.ScanSources` → `Observe`），UI 线程删（`DeviceManagerViewModel.cs:391` → `PlcDataAcquisitionService.cs:1263` → `RemoveDevice`）。`PlcConnectionManager` 的双锁设计（`:20-21` 注释）已是团队内部的正确范式，但未成文。

**决策**
1. 采集管线的所有进程内状态容器（各类 Tracker、Coordinator、字典/集合字段）**默认加锁**，且必须声明其线程所有权（谁写、谁读、来自哪个线程）。
2. 采用 `PlcConnectionManager` 的双锁范式：**状态锁**（短临界区，保护内存状态）+ **IO 锁**（串行化阻塞式 PLC/DB 操作），避免网络超时阻塞状态查询。
3. 事件回调一律**在锁外触发**（`PlcConnectionManager.cs:233/258/269-271` 已是范例），防止订阅者回调重入死锁。
4. 异常分类纪律：致命异常（`OutOfMemoryException`/`AppDomainUnloadedException`/`ThreadAbortException`）**必须重抛**，不得吞掉——此纪律已在 `PlcConnectionManager.cs:183-184/345-346`、`PlcDataAcquisitionService.cs:544-545/620-621/644-645` 一致执行，升格为 ADR。
5. 在 `ci/architecture-gates.ps1` 增加检查：`Kanban.Collector.Core/Services` 下的 `*Tracker.cs` 若含 `Dictionary<`/`List<` 字段则必须含 `lock`（误报可接受，作为人工复核提示）。

**后果**
- 变容易：新增状态容器有明确模板；并发缺陷从"靠 review 发现"变成"有门禁提示"。
- 变困难：每处加锁需要想清楚锁粒度，短期增加代码量。

**备选方案**
(a) 改用 `ConcurrentDictionary`——被否决：复合操作（检查 + 插入 + 状态转移）仍需原子性，`ConcurrentDictionary` 只解决单操作线程安全，掩盖而非解决问题；且 `PlcConnectionManager` 的双锁划分无法用它表达。
(b) 全部收敛为"轮询线程独占 + 队列投递"——被否决：改造面过大，且 UI 删除设备的即时性要求不支持异步投递。

---

### ADR-12：页面 VM 单一创建路径与生命周期契约

**背景**
2026-08-11 的懒加载重构引入双 VM 创建路径，P0-1（跨页导航事件全部失效）即由此产生。本轮复查确认 P0-1 已通过 `AttachPageViewModel`（`MainWindowViewModel.cs:489-509`）+ `_attachedPageViewModels` 幂等守卫修复，但**根因未消除**：`NavigationPageHost.EnsureViewLoaded()`（`NavigationPageHost.cs:73-75`）创建 VM 时并不 attach，正确性依赖"ActivatePage 必然先于 host 可见"这一**未被断言的 WPF 时序**（R2）。同时存在两套生命周期机制（`INavigationPageLifecycle` vs `Loaded`/`Unloaded`），后者与"View 单例 + host 常驻"的架构不兼容（R3）。

**决策**
1. **单一创建路径**：`RegisterPage` 的 `viewModelFactory`（`MainAppPresentationServiceCollectionExtensions.cs:141-146`）负责"解析 + 挂接"，即 `() => { var vm = sp.GetRequiredService<TViewModel>(); Attach(vm); return vm; }`。`NavigationPageHost.EnsureViewLoaded()` 与 `MainWindow.ActivatePage()` 都只经由 `NavigationPage.ViewModel`（`NavigationPageDefinition.cs:34` 的 `Lazy`）取值，谁先触发都必然 attach。
2. **单一生命周期机制**：`INavigationPageLifecycle`（`OnPageEnter`/`OnPageExit`）是**唯一**的页面启停契约；禁止用 `Loaded`/`Unloaded` 驱动业务启停（View 是单例且 host 常驻，`Unloaded` 不触发）。
3. 页面 VM 跨页事件若需挂接，在实现 `INavigationPageLifecycle` 的同时，必须在 `AttachPageViewModel` 的 switch 中登记；建议把 switch 换成"页面声明自己需要的挂接"（如 `INavigationPageLifecycle` 增加 `void OnCreated(INavigationHost host)`），消除 switch 的静默失败面。
4. `ci/architecture-gates.ps1` 增加两条：① `MainAPP/Views/**` 禁止 `Unloaded +=` 用于业务停止；② `MainAPP/ViewModels/**` 中带 `Requested` 后缀事件的新增必须有对应挂接点（人工复核辅助）。

**后果**
- 变容易：新增页面不需要知道"要不要在第二个地方挂事件"；生命周期行为可预测。
- 变困难：需要改造 `RegisterPage` 与 `NavigationPageHost`，是一次小重构（约 30 行）。
- 需重新审视：若未来页面需要"离开即销毁"（非单例），本 ADR 的"单例 + 常驻"前提失效，需重议。

**备选方案**
(a) 维持现状 + 加强注释——被否决：P0-1 已经证明注释挡不住这类失效。
(b) 取消懒加载、构造期全量创建——被否决：首屏与启动耗时是已验证的实际收益（`MainWindowViewModel.cs:419-423` 注释记录了消除重型服务链 JIT 成本）。

---

### ADR-13：两阶段启动初始化约定

**背景**
MS.DI 无"阶段"概念，但本系统存在硬性的两阶段依赖：**配置必须先 Load，之后构造/初始化的组件才能读到正确值**。`CollectorWorker.cs:108-111` 的注释记录了一次真实回归——单例在 `settings.Load()` 之前被构造，读到默认 `PlcConfig`（127.0.0.1），"单例档案被永久污染，采集永远连不上真实 PLC（回归自 04e8442）"。当前补丁是 Load 后手工调 `RefreshFromSettings()`。同类顺序依赖还存在于事件接线（`:158-161`）与 `AuditLog.Initialize`（`App.xaml.cs:206-210`）。这类缺陷**无编译错误、无测试失败，只在现场暴露**。

**决策**
1. **构造函数不得读取 `AppSettings` 的业务值**（PlcConfig/Shifts/ConnectionProfiles）。允许的例外：路径类（`GetFilePath`）、不影响连接语义的 UI 偏好。
2. 需要在配置就绪后才能确定的状态，一律走以下三种方式之一：① 惰性读取（每次使用时从 `AppSettings` 现取，如 `PlcConnectionManager.EnsureConnected():168` 的 `_profileProvider?.Current.Config`）；② 提供显式 `RefreshFromSettings()` 并在 `CollectorWorker.InitializeAsync` 的**固定位置**调用；③ 由 `IHostedService.StartAsync` 阶段注入。
3. `CollectorWorker.InitializeAsync` 是**唯一的启动编排点**，其步骤顺序（配置 → 档案刷新 → 本地化 → 审计 → 设备 → schema → 工单 → 配方 → 采集启动 → 事件接线）不得在别处复制；新增初始化步骤必须加在此处并在注释说明其前置。
4. 初始化末尾加自检：比对 profile 实际 IP 与 `settings.PlcConfig.IpAddress`，不一致即 `LogCritical`（把回归变成启动期告警）。

**后果**
- 变容易：新加服务时不再需要猜"什么时候构造才安全"；回归从现场暴露提前到启动期告警。
- 变困难：部分组件要改成惰性读取，需要逐个审视。

**备选方案**
(a) 引入自定义 DI 生命周期（如 `IConfigurationReady` 标记接口 + 启动扫描自动 Refresh）——被否决：增加框架复杂度，当前 7 个库的规模下收益不抵成本。
(b) 把 `settings.Load()` 提前到 `Program.cs` 的 `builder.Build()` 之前——部分可行且值得做（可消除"构造期污染"窗口），但无法覆盖"配置运行中热更新"场景，故仍需要本 ADR 的惰性读取纪律。**建议两者都做**。

---

### ADR-14：Remote/Local 接口重定向的注册纪律（替代 2026-08-16 的"双模式冻结"提案）

**背景**
上轮我建议立"ADR-8 Local/Remote 退役条件与冻结策略"。本轮复查发现该提案未落地，`IsRemote` 分支从 67 处**增长到 70 处**——说明"冻结新增"这类**靠人自律的约定在本项目不奏效**（Remote 化是持续演进方向，不可能真的冻结）。与其冻结，不如把风险**变成可自动验证的**。

**决策**
1. 承认 Local/Remote 双模式将长期共存，不设退役时间表；但把维护成本控制在一个可验证的边界内：
   - **历史/查询类**分支一律收敛在 `RemoteHistoryQueryService`（当前 33 处，是健康的 facade 形态——门面内分支，不是散落分支）；
   - **新增** `IsRemote` 分支默认应落在 facade，若必须落在 ViewModel，需在 PR 说明中给出理由。
2. **`MainAppServiceCollectionExtensions.cs:96-107` 的"后注册胜出"重定向不得依赖人工记忆**：
   - 每个被重定向的接口必须在 `RemoteHistoryQueryService` 上有对应成员（否则编译期就该失败——建议让 `RemoteHistoryQueryService` 显式实现全部 6 个接口，而不是靠 `AddSingleton<I>(sp => sp.GetRequiredService<RemoteHistoryQueryService>())` 的鸭子类型转发）；
   - 增加一条反射级架构测试：扫描 Core 中 `I*HistoryService`/`I*Reader`/`I*Query` 接口，断言重定向清单完整。
3. 在 `docs/架构决策记录.md` 中维护"Local/Remote 分支分布表"（每半年更新一次即可），让散落分支的数量**可见**——可见性本身即是约束。

**后果**
- 变容易：Local/Remote 的心智负担从"记住两处"降为"看一张表 + 有测试兜底"。
- 变困难：需要写一条反射测试（约 30 行）。

**备选方案**
(a) 维持上轮的"冻结新增"——被否决：已被 13 天内的实际演进（67→70）证伪。
(b) 立即移除 Local 模式——被否决：用户已拍板不急着动，且 Local 是现场兜底路径。

---

### ADR-15：备份是部署契约的一部分

**背景**
`%APPDATA%/Kanban` 下有 7 个 SQLite 库（`DatabaseProvider.cs:30-39`）、`devices.json`/`settings.json`/`recipes.json`/`baselines.json`。上轮已指出该目录是"真正的单点残留"，建议加每日备份；本轮复查确认 `ci/` 下无任何备份脚本。ADR-3 已明确不为 <100 台规模做多 Collector HA，兜底是 `sc failure` 重启 + 重连自愈 + 恢复文件——**但这些机制全部无法对抗"数据目录损坏/丢失"**。

**决策**
1. 备份属于部署契约，不是可选项：`ci/` 提供 `backup-data.ps1`（SQLite `VACUUM INTO` 或 `.backup` + 配置目录 robocopy），由安装脚本（`ci/install-collector-service.ps1`）注册每日计划任务。
2. 备份目标必须是**不同物理盘**或网络位置；同盘副本不视为备份。
3. 在 `/health/ready`（`Program.cs:176-185`）之外，暴露"最近备份时间"到 `/metrics`（`Program.cs:202-207`），使备份缺失可被监控发现。
4. 记录适用边界：本 ADR 覆盖"数据丢失"，不覆盖"高可用"（仍按 ADR-3 不提前做 HA）。

**后果**
- 变容易：单磁盘故障从"全量历史丢失"降级为"丢失一天数据"。
- 变困难：部署多一个步骤、多一个运维依赖（备份目标可达性）。

**备选方案**
(a) 依赖客户自身的 IT 备份策略——被否决：车间现场的 IT 保障水平不可假设，且本系统已是数据唯一生产者。
(b) 换用客户端-服务器数据库（如 PostgreSQL）从根本上解决——被否决：违反 ADR-2/ADR-3，且运维成本远超车间场景承受范围。

---

### ADR-16：仿真器与产品共享设备配置契约

**背景**
`PlcSimulator` 用自建的 `DeviceConfig`（`DeviceConfig.cs:6-8`，自述"与 MainAPP Device 模型对齐的精简版"）反序列化 `devices.json`——而该文件按 ADR-2 是 Collector 独占写者。`Program.cs:186-193` 在文件缺失时还会**写入**自建默认配置（ADR-2 违反），`CreateDefaultDevices():959` 是第二套硬编码默认设备。已有改进迹象：`Sources` 已改用 `Kanban.Contracts` 的 `DataSourceConfigDto`（`DeviceConfig.cs:23`），但 `AlarmConfig`/`DefectConfig`/`CounterAlarmConfig` 仍是本地镜像。

**决策**
1. `PlcSimulator` 一律使用 `Kanban.Contracts` 的设备配置 DTO（已完成 `Sources`，推广到 Alarms/Defects/CounterAlarms）；`PlcSimulator.csproj:24` 已引用 Contracts，不存在障碍。
2. **仿真器不得写 `devices.json`**：文件缺失时在内存中用内置默认运行并明确提示，不落盘。
3. 默认设备模板**只有一份**：若产品侧有默认设备种子，仿真器引用之；否则由 `ci/` 提供统一的种子文件，仿真器与 Collector 都从该文件读取。
4. 加一条契约测试：`DeviceConfigDto` 的每个字段在仿真器中都有消费点（或显式声明"仿真不模拟此字段"），使模型演进时的失配可见。

**后果**
- 变容易：设备模型演进时仿真器同步演进，仿真结果可信度提升——这对"用仿真器做验收"的场景是关键。
- 变困难：仿真器要适配 DTO 的字段语义（如新增的 `ConnectionProfileId`）。

**备选方案**
(a) 仿真器直接从 `devices.json` 读原始 JSON（`JsonNode`）而不做模型绑定——被否决：失去类型安全与字段拼写检查，失配更隐蔽。
(b) 让 Collector 暴露一个"设备配置" Hub 接口供仿真器拉取——可行但过重；列为未来选项（若仿真器需要支持多配置文件时再议）。

---

## 四、架构优点（保留并发扬）

本轮评估发现多处**高于同类车间软件的工程水准**，明确建议保留并作为范式推广：

1. **采集主循环的故障隔离设计（优秀）** — `PlcDataAcquisitionService.cs:359-583`：四类扫描（报警/缺陷/计数报警/数据源）各自 `TryScan` 独立 try-catch（`:612-656`），异常按"通信类/业务类"分类处理——业务异常只记日志不影响连接状态，通信异常才走 `MarkDisconnected`（`:622-626`）；顶层 catch 同理分类（`:554-561`）；致命异常一律重抛（`:544-545`）；异常后重置 Stopwatch 防止 OEE 时间暴涨（`:564`）。**这套"异常分类 → 差异化降级"的纪律应升格为 ADR-11 的一部分。**

2. **`PlcConnectionManager` 的双锁设计（范式级）** — `:62-63` 状态锁与 IO 锁分离，`:20-21` 注释说明理由（避免 PLC 网络超时阻塞状态查询）；事件在锁外触发（`:233/258/269-271`）；`MarkDisconnected` 主动推进冷却期以抑制"半连接"重连风暴（`:246-252`，含测试名引用）；`Disconnect` 刻意不重置冷却期以抗抖动（`:321-322`）。**这是本项目并发处理的标杆实现。**

3. **`EventBroadcaster` 的扇出与背压（优秀）** — `EventBroadcaster.cs:20-24,45-61,89-124`：`Seq` 分配 + 环形缓冲写入 + 扇出在**单临界区**原子完成，杜绝"补发与实时各投递一次"；订阅时"补发 + 注册"同锁原子（`:102-110`），消除丢事件窗口；每订阅者独立有界 channel（`BoundedChannelFullMode.DropOldest`，`SingleReader/SingleWriter`），慢客户端不影响他人也不 OOM；报警/状态流独立 Seq + `ServerEpoch` 支持 Collector 重启后的游标归零（`:34`、`:181-191`）。**建议作为"推送扇出"的标准模板。**

4. **写入侧背压 + 崩溃恢复（优秀）** — `ProductionHistoryWriter.cs:27-28,75-87`：`Channel.CreateBounded` + `TryWrite` 非阻塞入队（不把背压传导到 PLC 轮询线程），溢出转存 JSONL 恢复文件（200MB 上限，`:17`）；`DataSourceSnapshotStore.cs:27-30` 同构；诊断面完整（p95/p99、Pending、Overflow、恢复文件字节数，`:51-72`）。**ADR-8 的容量基线正是建立在这套可观测数据上——数据支撑决策，是这个团队做得最好的地方。**

5. **数据库迁移策略已收敛（显著改进）** — 相比上轮的"6 套独立 Migrations + EnsureCreated 双轨制"，现已统一为单一 `Migrations` 目录 + `Database.Migrate()`（`DatabaseProvider.cs:56-95`），并为旧库提供"基线补建 + 迁移前自动备份 + 迁移后关键表校验"的完整路径（`:97-147`），迁移后还幂等补列以应对"事后修改迁移"的历史包袱（`:149-161`，注释引用修复 #13）。**这是上轮债务中治理得最干净的一项。**

6. **授权体系升级为非对称（关键改进）** — `LicenseSigningKey.cs:16-17,42-75`：客户端仅内嵌 P-256 公钥，私钥存签发机 AppData，且加载时用 `FixedTimeEquals` 校验公私钥匹配；旧 HMAC 明确降级为兼容路径并保留环境变量注入的防反编译约束（`EmbeddedKey.cs:9`）。**这从根本上消除了上轮标记的"离线激活可被 dump 自签"架构缺陷——是本项目最重要的安全架构进步。**

7. **管理域与监控域 Hub 拆分（上轮建议已落地）** — `KanbanAdminHub.cs:15` + `Program.cs:210`，配置/工单/配方/设置写入不再暴露给只读监控连接。

8. **配置写入的原子性与锁纪律（良好）** — `AppSettings.WriteFileAtomically` 是统一入口（`AppSettings.cs:509/513`），`DeviceRepository` 全篇 `_collectionLock` 一致保护（`:139/153/196/249/263/282/292/302/328/342/359/485`），损坏文件自动隔离备份（`:503`）；`settings.json` 有完整的版本化迁移链（schema 0→8，`SettingsMigrationRunner.cs:15-27`）。

9. **架构门禁已具雏形** — `ci/architecture-gates.ps1` 用脚本而非口头约定约束架构（当前 2 条：禁用废弃 Remote Hook、禁用 `private async void`）。**这是把 ADR 变成可执行约束的正确载体**，本报告多个建议都指向扩展它。

10. **ADR 文化与度量驱动** — `docs/架构决策记录.md` 已有 ADR-1~9 并附变更记录；ADR-8 甚至包含实测压测数据表（20/50/100/200 台的 Append/Flush p95 基线）与运行判据表。**"先度量再决策"的习惯是这套架构最值钱的资产。**

---

## 五、评估覆盖说明与局限

### 5.1 本轮实际覆盖

**逐文件精读（部分或全部）**：
- DI 与组合根：`MainAppServiceCollectionExtensions.cs`、`MainAppPresentationServiceCollectionExtensions.cs`、`KanbanDataServiceCollectionExtensions.cs`、`Kanban.Collector/Program.cs`、`Kanban.Web/Program.cs`
- 导航与生命周期：`MainWindowViewModel.cs`（全文 771 行）、`MainWindow.xaml.cs`（1-130、230-305）、`NavigationPageHost.cs`（全文）、`NavigationPageDefinition.cs`（全文）、`NavigationPageModule.cs`（部分）
- 采集主链路：`PlcDataAcquisitionService.cs`（210-660 行区间，含 Start/StopAsync/PollingLoopAsync/TryScan）、`PlcConnectionManager.cs`（全文）、`CollectorWorker.cs`（全文）、`EventBroadcaster.cs`（全文）、`ProductionHistoryWriter.cs`（1-122）、`DataSourceSnapshotStore.cs`（grep 级）
- 数据访问：`DatabaseProvider.cs`（1-160、400-530）、`DeviceRepository.cs`（grep 级）、`AppSettings.cs`（455-545）、`SettingsMigrationRunner.cs`（1-60）
- 契约与仿真：`Kanban.Contracts.csproj`、`Kanban.Client.csproj`、`Kanban.Web.csproj`、`PlcSimulator/DeviceConfig.cs`、`PlcSimulator/Program.cs`（150-200、950-1000）、全部 6 个 csproj 的引用清单、`Kanban.slnx`
- 授权：`LicenseGate.cs`（全文）、`LicenseSigningKey.cs`（全文）、`EmbeddedKey.cs`（全文）、`App.xaml.cs`（130-220）
- Hub：`KanbanAdminHub.cs`（1-50、150-195）、`KanbanHub.cs`（grep 级）
- CI：`ci/architecture-gates.ps1`（全文）、`ci/` 文件清单
- 文档：`docs/架构决策记录.md`（全文 163 行）
- 客户端链路：`RemoteRuntimeSink.cs`（60-300）、`DashboardState.cs`（grep 级）、`KanbanDataClient.cs`（grep 级）

**跨项目调用链追踪（已完成）**：UI 线程删除设备 → 采集状态容器的并发写路径（R1）；页面 VM 创建的全部路径与 attach 调用点（R2）；页面生命周期机制的两条实现路径（R3）；PlcSimulator 与 Collector 对 `devices.json` 的读写权（C4）。

### 5.2 局限与未覆盖项

1. **未逐条复查 2026-08-17 报告中的全部 P2/P3 项**。已明确复查并给出结论的：P0-1、P1-1、P1-2、P2-2、P2-4、P2-5、P2-7、P2-8、P2-11、P3-6、P3-13、设计层 #1/#2。**未复查**：P2-1（`DeviceRuntime` 会话字段并发读改写）、P2-3（`EnsureConnected`/`Disconnect` 竞态——本轮已看到双锁改造，判断为**大幅缓解但存在残留**：`Disconnect()` 先置 `IsConnected=false`（`:330-334`）再于另一把锁内断开驱动（`:336-349`），与 `EnsureConnected` 交错时可能出现"socket 实际已连但状态为断、需等冷却期"的窗口，影响为延迟重连而非状态错乱，**严重程度已从上轮的 P2 降为 P3**）、P2-6、P2-9、P2-10、P2-12、P2-13、P2-14、P3-1~P3-5、P3-7~P3-12。这些多属代码级缺陷，建议由 Cody（code-reviewer）在本次全面审查中覆盖。

2. **未做运行时验证**（未编译、未跑测试、未启动进程）。所有结论基于静态阅读；R4（无单轮看门狗）、R6（空闲期不回放）的**触发条件**为静态推断，但其**代码路径**已验证。

3. **`oxyplot/` 按要求排除**，故债 #11（本地分叉 diff）本轮无结论。

4. **测试项目排除**：本报告未利用测试代码反向推断架构约束（如 `MainAPP.Tests/Integration` 中的 `ConcurrentInvoke_WhileLongRunningSubscribePending_IsQueued` 仅经 ADR 文档间接得知）。若需评估"架构约束是否有测试锁定"，需单开一轮。

5. **`ci/` 脚本只精读了 `architecture-gates.ps1`**；`deploy-svc.ps1`/`install-collector-service.ps1`/`publish-all.ps1` 等仅看文件名与 grep，R8（无备份）的结论基于"ci 下无备份脚本 + 仓库内无 SQLite backup 调用"双重 grep，置信度高但不能完全排除备份存在于仓库外的运维流程中。

6. **配置类风险的严重度依赖部署形态**。O1（operator 自报）、Hub 无认证等，在"物理隔离的车间局域网"下可接受；一旦跨网段/上云即为高危。本报告按**当前 LAN 部署**评估，并已在 ADR 建议中标注演进红线。

7. **DI 生命周期专项结论**（回应任务书第 1 条）：**未发现 Singleton 捕获 Scoped/短命对象的隐患**——全仓唯一的 `AddScoped` 是 WASM 的 `HttpClient` 工厂（`Kanban.Web/Program.cs:29`），无 captive dependency；`LoginViewModel`/`LoginWindow` 是全仓仅有的两个 Transient，且有明确注释说明原因（`MainAppPresentationServiceCollectionExtensions.cs:36-39`）。**真正的生命周期风险不在 DI 作用域，而在"14 个页面 View/ViewModel 全为 Singleton + host 常驻不卸载"导致的"永不销毁"**（R3），以及"页面 VM 创建路径不唯一"（R2）。这一结论与上轮评估不同，建议团队据此调整关注点。

---

**总体结论**：相比 2026-08-16，这套架构在**数据层治理（迁移收敛）、授权安全（非对称化）、推送域拆分（Admin Hub）**三个方向有实质进步，工程水准仍在提升。核心问题集中在两类：
- **并发与生命周期的"最后一公里"**：上轮已精确定位的 P1-1（Tracker 无锁）、P2-7（定时器不释放）、P2-8（懒加载破坏）三条**零进展**，且都不是难点问题。
- **架构约束停留在注释层**：ADR-8/9/10/11 四条建议一条未成文，导致 ADR-1 与代码漂移（O2）、重定向依赖注册顺序（C1）、启动顺序靠人工维护（R5）等"能跑但脆"的隐患持续累积。

**最高性价比的三件事**：① 补 `DataSourceAlarmTracker` 的锁（R1，10 行）；② 扩展 `ci/architecture-gates.ps1` 承载 ADR-11/12 的检查（把口头约定变成门禁）；③ 加数据目录备份脚本（R8，成本最低的单点风险消除）。不建议现在做任何结构性重构。
