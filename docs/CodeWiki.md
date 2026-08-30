# Kanban 工业看板系统 — Code Wiki

> 本文件为仓库的代码维基（Code Wiki），描述项目整体架构、模块职责、关键类与函数、依赖关系及运行方式。
> 覆盖范围：`Kanban.slnx` 全部 19 个项目（含本地 vendored 的 OxyPlot 源码 4 项）。
> 配套文档：[架构决策记录](架构决策记录.md) · [MainAPP 易用性建议](MainAPP易用性建议.md) · [授权激活码设计方案](授权激活码设计方案.md)

---

## 1. 项目总览

**定位**：面向工厂车间的**工业看板（Kanban）系统**。通过 PLC（三菱/西门子/Modbus TCP/欧姆龙/基恩士）实时采集设备产量、状态、报警与缺陷数据，展示 OEE 看板、生产复盘、工单管理、报警中心与历史查询，支持中文、英文、日文和巴西葡萄牙文。

**形态**：`MainAPP`（WPF 桌面主程序，管理与本地采集）+ `Kanban.Collector`（独立采集服务进程，唯一数据写者）+ `Kanban.Web`（Blazor WASM 浏览器大屏展示）+ `PlcSimulator`（虚拟 PLC，联调/演示用）+ 授权体系（`LicenseManager.App` / `LicenseIssuer.Wpf`）。

### 1.1 技术栈

| 类别 | 选型 |
|---|---|
| 运行时 | .NET 10（`net10.0` / `net10.0-windows` / `net10.0-browser`），SDK `10.0.100`（`global.json` 固定） |
| 桌面 | WPF + CommunityToolkit.Mvvm + HandyControl 3.5.1 + Material.Icons.WPF |
| Web 屏端 | Blazor WebAssembly（`net10.0-browser`） |
| 服务端 | ASP.NET Core + SignalR（服务端与客户端，MessagePack 二进制协议） |
| 数据 | Entity Framework Core + SQLite（6 个独立 `.db` 库）、Microsoft.Data.Sqlite |
| PLC 通信 | HslCommunication（`MainAPP/DLLS/HslCommunication.dll` 本地裸引用） |
| 图表 | OxyPlot（**本地 vendored 源码**，见 `oxyplot/`） |
| 图表/PDF | OxyPlot.SkiaSharp、PdfSharpCore |
| 依赖注入/Host | Microsoft.Extensions.Hosting / DependencyInjection |
| 日志 | Serilog（文件滚动，异步 Sink） |
| 测试 | xunit.v3（Microsoft Testing Platform）、FlaUI.UIA3、Appium/WebDriver、NSubstitute、BenchmarkDotNet |
| 授权加固 | Obfuscar 混淆（仅 LicenseManager.App Release 构建） |

### 1.2 解决方案项目清单（`Kanban.slnx`）

| 项目 | 目录 | 目标框架 | 形态 | 一句话职责 |
|---|---|---|---|---|
| Kanban.Contracts | `Kanban.Contracts/` | net10.0 | 类库 | 跨进程 DTO / 枚举 / Hub 契约 / 单源计算常量，零依赖 |
| Kanban.Client | `Kanban.Client/` | net10.0 | 类库 | 共享 SignalR 客户端库（WPF/WASM 共用，不依赖 UI） |
| Kanban.Collector.Core | `Kanban.Collector.Core/` | net10.0-windows | 类库 | 采集/存储/领域模型（代码命名空间 `Kanban.Core.*`），MainAPP 与 Collector 共用 |
| Kanban.Collector | `Kanban.Collector/` | net10.0-windows | Exe 服务进程 | 采集服务：PLC 采集 + 历史落库 + SignalR 服务端 + WASM 静态托管 |
| MainAPP | `MainAPP/` | net10.0-windows | WinExe (WPF) | 桌面主程序：展示 + 管理 + 本地采集（Local 模式） |
| Kanban.Web | `Kanban.Web/` | net10.0-browser | Blazor WASM | 浏览器大屏展示端（只读，零配置） |
| LicenseManager.App | `LicenseManager.App/` | net10.0-windows | WPF 类库 | 授权核心：LicenseGate/试用/激活/加密（Release 混淆） |
| LicenseIssuer.Wpf | `LicenseIssuer.Wpf/` | net10.0-windows | WinExe | 激活码签发 GUI |
| PlcSimulator | `PlcSimulator/` | net10.0-windows | Exe | 虚拟 PLC（MelsecMcServer）场景模拟器 |
| MainAPP.Tests | `MainAPP.Tests/` | net10.0-windows | Exe 测试 | 单元 + WPF 集成测试（~130 文件） |
| MainAPP.E2E | `MainAPP.E2E/` | net10.0-windows | Exe 测试 | 进程内端到端流程测试 |
| MainAPP.UIAutomation | `MainAPP.UIAutomation/` | net10.0-windows | Exe 测试 | 真实进程 UI 自动化（FlaUI + WinAppDriver） |
| MainAPP.Benchmarks | `MainAPP.Benchmarks/` | net10.0-windows | Exe | 性能基准（BenchmarkDotNet，11 组） |
| OxyPlot (×4) | `oxyplot/Source/...` | 多 TFM | 本地源码 | vendored 的 OxyPlot 图表库（替代 NuGet） |

### 1.3 关键架构决策摘要

详见 [架构决策记录](架构决策记录.md)，核心约束：

| 编号 | 决策 | 影响 |
|---|---|---|
| ADR-1 | 每连接**单长驻订阅**，多流用多连接 | WASM 三连接、WPF Remote 双连接 |
| ADR-2 | **Collector 单写者**：一切持久化写只能由 Collector 进程执行 | MainAPP 写操作经 SignalR 转发，WASM 只读 |
| ADR-3 | 当前**单 Collector 部署边界**（不做多 Collector/HA） | `/metrics` 与积压指标为扩容依据 |
| ADR-4 | **单源计算约定**：OEE/快照换算/映射/配置各只实现一次 | `OeeCalculator`、`SnapshotMetrics`、`Mapping/` |
| ADR-5 | **屏端零配置**：WASM 一切数据从 Collector 拉取/推送 | `GetDevicesAsync` + Meta 流 + 快照流 |
| ADR-6 | **进程/项目边界**：Contracts 不得引用 Core/MainAPP | 依赖方向自上而下 |
| ADR-7 | **同连接 Invoke 必须在长驻订阅之前完成** | 订阅后不得同连接再 Invoke，另开短连接 |

---

## 2. 总体架构

### 2.1 进程拓扑与数据流

```
                        ┌─────────────────────────────────────────────────┐
                        │                Kanban.Collector（唯一写者）        │
   PLC 设备 ──HslCommunication──▶ │  采集核心 (Kanban.Collector.Core)       │
   (三菱/西门子/                      │    · PlcDataAcquisitionService 200ms轮询│
    Modbus/欧姆龙/基恩士)             │    · 历史落库：6 个 SQLite .db         │
                        │   SignalR Hub (/hubs/kanban :5129)              │
                        │    · 快照流 500ms · 报警/状态事件流              │
                        │    · Meta 流 5s（工单+班次）· 历史查询 · 配置同步   │
                        │    · WASM 静态托管 (wwwroot/)                    │
                        └──────┬───────────────┬───────────────┬──────────┘
                               │ SignalR        │ HTTP/WS        │ HTTP(S)
                               │ (MessagePack)  │ (JSON)         │
                    ┌──────────▼───────┐  ┌────▼────────┐  ┌─────▼────────┐
                    │ MainAPP (WPF)    │  │ Kanban.Web  │  │  浏览器大屏    │
                    │  · Remote 模式：  │  │ (Blazor WASM)│  │  (零配置)     │
                    │    订阅+查询代理   │  │ 三连接订阅    │  │              │
                    │  · Local 模式：   │  │ 只读展示     │  │              │
                    │    内置完整采集   │  │             │  │              │
                    └──────────────────┘  └─────────────┘  └──────────────┘
```

**两种运行模式**（由 `AppSettings.RunMode` 控制，枚举 `KanbanRunMode`）：

- **Local 模式（默认）**：MainAPP 内嵌完整采集能力（PLC 采集 + SQLite 落库），自包含运行，不依赖 Collector 进程。
- **Remote 模式**：MainAPP 只做展示与管理，连接 Kanban.Collector 进程；采集、落库、写操作全部经 SignalR 委托给 Collector（落实 ADR-2 单写者）。此模式下 MainAPP 使用 `KanbanDataClient` + `RemoteRuntimeSink`（双连接订阅）与 `RemoteHistoryQueryService`（查询代理）。

### 2.2 项目依赖方向

```
                       Kanban.Contracts   （纯契约，零依赖，被所有端引用）
                              ▲
             ┌────────────────┼────────────────┐
             │                │                │
      Kanban.Client      Kanban.Collector.Core   （引用 Contracts + HslCommunication.dll）
             │                ▲        ▲
             │                │        │
      ┌──────┴──────┐   Kanban.Collector    MainAPP
      │  Kanban.Web │   （引用 Core/Contracts）
      │  (WASM)     │
      └─────────────┘
      LicenseManager.App ──┬──► LicenseIssuer.Wpf
                           └──► MainAPP（授权门禁）
      MainAPP ◄── 项目引用：LicenseManager.App、Kanban.Client、Kanban.Collector.Core、OxyPlot(本地)
      MainAPP.Tests / E2E / UIAutomation / Benchmarks ──► MainAPP（及 Collector）
      PlcSimulator ──► HslCommunication.dll（无项目依赖）
```

### 2.3 数据存储布局

所有数据根目录：`%APPDATA%\Kanban`（环境变量 `KANBAN_DATA_DIR` 可覆盖）。

| 文件/库 | 内容 | 写者 |
|---|---|---|
| `Config/settings.json` | PLC 连接、轮询间隔、班次、界面设置、语言 | Collector（Remote）或 MainAPP（Local） |
| `Config/devices.json` | 设备、PLC 地址、报警、缺陷、计数报警配置 | 同上 |
| `Config/baselines.json` | 设备产量基线与班次基线 | 同上 |
| `Config/*.bak` / `*.corrupt` | 保存前备份 / 损坏原文件备份 | 自动 |
| `production_logs.db` | 生产快照历史（EventId 幂等去重） | Collector |
| `alarm_events.db` | 报警事件历史（触发/恢复/班次切换） | Collector |
| `status_transitions.db` | 设备状态转换记录（OEE 回溯） | Collector |
| `work_orders.db` | 工单 | Collector |
| `defect_history.db` | 缺陷计数快照 | Collector |
| `audit_logs.db` | 操作审计日志（只追加） | Collector |
| `license.dat` | 授权信息（DPAPI 加密） | MainAPP |
| `trial.dat` | 试用状态（明文 + HMAC 签名） | MainAPP |

---

## 3. 模块详解

### 3.1 Kanban.Contracts — 共享契约层（`Kanban.Contracts/`）

纯类型定义项目，零 NuGet 依赖，是全仓库的跨进程契约唯一来源。

#### 3.1.1 关键 DTO（`Dtos/`）

| DTO | 用途 | 关键字段 |
|---|---|---|
| `DeviceSnapshotDto` | 设备实时快照（500ms 级推送） | `DeviceId/Name/Status/StatusWord`、`OkProduction/NgProduction`、班次累计 `TotalOk/TotalNg/RunTime/AlarmTime/PausedTime`、OEE 四率、`ActiveAlarms`、`Seq`、`Removed`（tombstone） |
| `ActiveAlarmDto` | 快照内当前触发报警 | `AlarmId/Name/PlcAddress/Description/Level/StartTime` |
| `AlarmEventDto` | 报警边沿事件（触发/恢复/班次切换） | `Seq/ServerEpoch`（补拉游标）、`DeviceId/AlarmId/EventType/Level/EventTime/ShiftName` |
| `StatusEventDto` | 状态转换边沿事件 | `Seq/ServerEpoch`、`PreviousState/CurrentState/EventTime/ShiftName` |
| `MetaStateDto` | 低频元数据包（约 5s） | `Devices`（全部设备当前工单 `DeviceWorkOrderDto`）、`Shift`（`ShiftProgressDto`） |
| `ShiftProgressDto` | 班次进度 | `IsInShift/Name/ElapsedText/RemainingText/Ratio/Pct` |
| `HistoryQueryRequest/Response` | 历史查询契约 | `QueryType/DeviceId?/From?/To?/ShiftName?/WorkOrderId?/AlarmId?/Page/PageSize`；响应含 `Total` + 4 个强类型列表（**强类型字段**是 SignalR 序列化约束，object 装箱集合会退化为 JsonElement） |
| `BatchHistoryQueryRequest/Response` | 批量历史查询（≤32 子查询一次往返） | `Queries` / `Results` 按序一一对应 |
| `CollectorSettingsDto` | 采集设置热同步（Remote） | `PollingIntervalMs/PlcBrand/PlcIpAddress/...` + `Siemens?/ModbusTcp?/Omron?/Shifts?`（可空=部分更新） |
| `CollectorDiagnosticsDto` | Collector 诊断快照 | 采集循环/历史写入/连接状态三类计数与耗时 |
| `ConfigDtos`（DeviceConfigDto/AlarmConfigDto/DefectConfigDto/CounterAlarmConfigDto/WorkOrderDto） | 配置与工单管理域 DTO | 见名知义；`DeviceConfigDto` 不含运行时状态 |
| `HistoryErrorCode` | 历史查询错误码 | `None=0` / `QueryFailed=1`（客户端按码渲染本地化文案） |

#### 3.1.2 枚举（`Enums/`）

- `DeviceStatus`：`Unknown=0, Running=1, Alarm=2, Paused=3`（状态字等值判断，非位掩码）
- `AlarmLevel`：`Low=0, Medium=1, High=2`
- `DefectSeverity`：`Minor=0, Major=1, Critical=2`；同文件含 `DefectCategory`：`Appearance/Dimension/Function/Packaging/Other`
- `WorkOrderStatus`：`Pending=0, Running=1, Completed=2, Aborted=3`（状态机 Pending→Running→Completed/Aborted）
- `AlarmEventType`：`Triggered=1, Recovered=2, ShiftChange=3`（**数值已持久化，禁止调整**）

#### 3.1.3 Hub 契约（`Abstractions/`）

- `IKanbanHubClient`：服务端→客户端推送回调（`OnSnapshot/OnAlarmEvent/OnStatusEvent/OnMeta`）
- `IKanbanHubServer`：监控域客户端→服务端方法（`GetCurrentSnapshotsAsync/SubscribeSnapshotsAsync/SubscribeAlarmEventsAsync(afterSeq)/SubscribeStatusEventsAsync(afterSeq)/QueryHistoryAsync/QueryHistoryBatchAsync/GetDevicesAsync/GetCurrentWorkOrderAsync/GetShiftProgressAsync/SubscribeMetaAsync/GetServerVersionAsync/GetTitleAsync/GetLanguageAsync`）
- `IKanbanAdminServer`：管理域写操作（`SaveDevicesAsync/UpsertWorkOrderAsync/DeleteWorkOrderAsync/SaveCollectorSettingsAsync`）——与监控域同 Hub 实现但接口独立
- `IEventStreamService`：`WatchAlarmEvents/WatchStatusEvents`（断线重连带 LastSeq 补拉）
- `IHistoryQueryService`：`QueryAsync`
- `ISnapshotService`：`WatchSnapshots/GetCurrentSnapshotAsync`

#### 3.1.4 单源常量与计算

- `KanbanHubPaths`：`HubPath = "/hubs/kanban"`、`DefaultPort = 5129`（单端口拓扑，全仓库唯一来源）
- `SnapshotMetrics`（`Metrics/`）：快照展示换算唯一实现——`RealtimeSpeed`（件/小时，`MinRunTimeSecForSpeed=5`）、`TotalOutput`、`NgRate`、`TimeRatio`、`CycleSeconds`、`AchievementRate`
- `DurationFormatter`（`Formatting/`）：`FormatCompact`（"Xh Ym"/"Xm"）与 `FormatStandard`（"Xh Ym"/"Xm Ys"/"Xs"），三端共用

### 3.2 Kanban.Client — 共享 SignalR 客户端（`Kanban.Client/`）

- `IKanbanMonitoringClient`（`IKanbanClient.cs`）：监控域窄接口（快照/事件/查询/版本等，均带 `CancellationToken ct = default`）
- `IKanbanAdminClient`：管理域窄接口（SaveDevices/UpsertWorkOrder/DeleteWorkOrder/SaveCollectorSettings）
- **`KanbanDataClient`**（核心类，`sealed`，实现上述两接口 + `IAsyncDisposable`）：
  - 构造 `(string hubUrl, ILogger, bool useMessagePack = true)`：WPF 走 MessagePack 二进制；WASM 走 JSON（`useMessagePack: false`），Collector 双协议并存
  - `ConnectAsync`：10s 超时、`_connectGate` SemaphoreSlim 串行化、`WithAutomaticReconnect` 指数退避 1s/5s/15s/30s；首次失败不做后台重连（重连所有权归调用方）
  - 事件：`ConnectionStateChanged/Reconnecting/Reconnected`；数据新鲜度：`LastDataReceivedAt/MarkDataReceived`
  - 回调注册：`OnSnapshot/OnAlarmEvent/OnStatusEvent/OnMeta`（强类型 `_connection.On<T>`）
  - 服务端调用：快照/事件/Meta 订阅、`QueryHistoryAsync/Batch`、`GetDiagnosticsAsync`、配置与工单读写、`GetServerVersionAsync/GetTitleAsync/GetLanguageAsync`
  - **约束**：遵守 ADR-1（每连接单长驻订阅）与 ADR-7（订阅前完成 Invoke）；`DisposeAsync` 幂等

### 3.3 Kanban.Collector.Core — 采集核心类库（`Kanban.Collector.Core/`）

> 命名空间为 `Kanban.Core.*`（csproj `RootNamespace` 仅用于资源命名）。MainAPP 与 Kanban.Collector 共用此库。引用 Contracts + `HslCommunication.dll`。

#### 3.3.1 PLC 驱动体系（`Services/`）

| 类 | 职责与关键成员 |
|---|---|
| `IPlcDriver` | PLC 通信抽象：`Read/Write` 的 UInt16/Int32/Bool/Float/String 系列，均返回 `PlcOperationResult<T>`；同文件含 `PlcErrorKind` 枚举 |
| `HslNetworkPlcDriver<TClient>` | 抽象基类：线程安全（`_sync`）、配置热替换（`Configure` 按签名重建客户端）、异常分类、Dispose 守卫 |
| `HslPlcDriver` | **三菱 MC 驱动**（即 Mitsubishi 实现，`MelsecMcNet`，3E 帧，默认端口 4999） |
| `HslSiemensPlcDriver` | 西门子（`SiemensS7Net`，支持 S1200/S1500/S300/S400/S200Smart/S200，Rack/Slot/DataFormat） |
| `HslModbusTcpDriver` | Modbus TCP（`ModbusTcpNet`） |
| `HslOmronFinsDriver` | 欧姆龙（`OmronFinsNet`，ReadSplits 分批） |
| `HslKeyenceMcDriver` | 基恩士（`KeyenceMcNet`） |
| `PlcConnectionManager` | 连接状态机：`EnsureConnected`（指数退避重连 1s→30s）、`MarkDisconnected(DisconnectionReason)`、`IsConnected/ConnectionStatus/TotalDisconnectCount`；双锁设计 |
| `PlcDataAcquisitionService` | 采集总调度（facade）：`PollingLoopAsync` 轮询（约 200ms）驱动班次检测/重连/产量读取/OEE 累计/历史快照写入；`Start/StopAsync/ResetShift/GetDiagnosticsSnapshot`；内部装配 `PlcScanPipeline/DeviceStatusTracker/BaselineResetCoordinator/ShiftContext` |
| `PlcScanPipeline` | 扫描子系统：`PrepareDWordBatchValues`（批量读+轮内缓存）、`ScanAlarms/ScanDefects/ScanCounterAlarms`、`ClearAlarmsOnDisconnect` |
| `PlcBatchReadPlanner` | 静态批量读规划：离散 DWord 地址聚合成连续读块（空洞合并 `maxGapSlots`、长度上限），配套 `PlcBatchReadPlanCache` |
| `SharedPlcDriverRouter` | 共享驱动路由器：全局唯一活动 PLC 连接，品牌变更时经 `ISharedPlcDriverFactory` 替换驱动；实现 `IPlcDriver` 委托转发。**并发设计（锁外 IO + 引用安全）**：`_sync` 只保护驱动引用交换与引用计数，阻塞式 IO 在锁外执行（IO 串行化由底层驱动实例锁保证）；品牌切换时旧驱动标记退役，若仍有在途 IO 则等引用归零后延迟 Dispose，避免「在途 IO 期间释放驱动」的竞态。锁持有时间与 IO 阻塞时长解耦 |
| `DeviceAdapter` / `PlcDeviceAdapter` / `DeviceAdapterResolver` | 协议适配层：先经 Codec 解析校验地址类型/读写权限，再转传输地址调 `IPlcDriver`；`Resolve(device)` 按运行时品牌解析 |
| `PlcAddressParser` | 静态地址解析：支持 D/M（三菱）、Siemens DB 多写法、Modbus（HR/IR/C/DI）；`Parse→PlcAddressParseResult(Type/Offset/Stride/Group)` |
| `PlcAddressCodecs` | 品牌地址编解码：`MitsubishiAddressCodec/SiemensAddressCodec/ModbusTcpAddressCodec` + `PlcAddressCodecResolver`；`KeyenceAddressCodec`、`OmronAddressCodec` 独立文件 |
| `PlcBrandDescriptors` / `PlcBrandRegistry` | 品牌描述符（`PlcBrand`：Mitsubishi=1/Siemens=2/ModbusTcp=3/Omron=4/Keyence=5）与注册表 |
| `PlcErrorClassifier` / `PlcRuntimeProfile` / `PlcConnectionManager` | 错误分类、运行 Profile（含当前 AddressCodec）、连接管理 |

#### 3.3.2 历史存储体系（`Data/` + `Entities/` + `Services/`）

6 个独立 SQLite 库，全部继承基类 `KanbanDbContextBase`（`Data/DatabaseProvider.cs` 内定义，统一 `UseSqlite(Cache=Shared)` + `SqlitePragmaInterceptor`：synchronous=NORMAL、busy_timeout=5000、temp_store=MEMORY、mmap 256MB）：

| DbContext（`Data/`） | 数据库文件 | 实体（`Entities/`） |
|---|---|---|
| `ProductionLogDbContext` | production_logs.db | `ProductionLog`（班次累计 OK/NG、StatusWord、**EventId 唯一索引**做幂等回放） |
| `AlarmEventDbContext` | alarm_events.db | `AlarmEventRecord`（Triggered/Recovered/ShiftChange） |
| `StatusTransitionDbContext` | status_transitions.db | `StatusTransitionRecord`（Previous/Current/EventTime） |
| `WorkOrderDbContext` | work_orders.db | `WorkOrder`（OrderNo/ProductCode/.../Status 状态机） |
| `DefectHistoryDbContext` | defect_history.db | `DefectSnapshotRecord`（Severity/Category/Count，EnsureCreated 建表） |
| `AuditDbContext` | audit_logs.db | `AuditEntry`（只追加，EnsureCreated 建表） |

`DatabaseProvider`：`CreateXxxContext()` ×6、`EnsureCreatedAll`（前四库走 EF Migrate + 基线补丁）、`EnsureWalModeEnabled`、`CheckpointAll`、`ProbeWriteAccess`。`KanbanDbContextFactory` 为 EF CLI 设计时工厂。

**历史服务层**（`Services/`）：

| 类 | 职责 |
|---|---|
| `HistoryService` | 历史兼容门面：聚合各 Store + 写入管线，5 分钟 WAL checkpoint 定时器，`CleanupOldHistory(retentionDays)` 统一清理 |
| `AlarmHistoryStore` | 报警事件读写：`LogAlarmEvent/QueryAlarmEvents(Paged/Strict/Batch)/GetLatestAlarmEvent/CleanupOldAlarmEvents` |
| `DefectHistoryStore` | 缺陷快照读写：`Append`（批量短事务）、`QueryWindowBounds/QueryHourlyBounds`（原生 SQL 窗口函数） |
| `ProductionHistoryStore` | 生产日志查询：`QueryProductionLogs(Strict/Paged/ByWorkOrder)` 等 |
| `ProductionHistoryWriter` | 异步写入管线：有界 Channel(10000) + 5s/200 条批量落库 + `production_logs.recovery.jsonl` 兜底（EventId 幂等去重） |
| `StatusTransitionHistoryStore` | 状态转换读写：`LogStatusTransition/GetLatestStatusBefore` 等 |
| `AuditService` / `AuditLog` | 审计：有界 Channel(2048) + 批量落库（2s/200 条/重试 3 次）；`AuditLog` 为静态门面（`Initialize` + `Record`，与业务解耦） |

#### 3.3.3 设备与领域模型（`Models/`）

| 模型 | 要点 |
|---|---|
| `Device` | 设备配置（ObservableObject，持久化）：产量/状态/复位/配方地址 + `Alarms/Defects/CounterAlarms` 子集合 |
| `DeviceRuntime` | 运行时状态（纯内存）：PLC 原始值 + 班次会话累计 + OEE 计算属性（**全部委托 `OeeCalculator`**）；`ResetShift/UpdateFromCollector` |
| `DeviceCapabilities` | record：`For(device)` 从配置派生（SupportsAlarmRead/WriteCommand/...），不持久化 |
| `DeviceStatus` / `DeviceStatusWord` | `Unknown=0/Running=1/Alarm=2/Paused=3`；状态字同义枚举（Offline/Running/Alarm/Standby） |
| `ShiftConfig` | 班次配置（时分秒）：`Contains/ResolveRange`（NodaTime 跨天计算）、`GetCurrentStart` |
| `PLCConfig` | PLC 连接配置：根级 Brand/IP/Port/Timeout + 各品牌嵌套选项；`GetDefaultPort`、`GetConfigurationSignature`、兼容旧字段的 JSON Converter |
| `Alarm` | 报警项：`Id = DeviceId_PlcAddress` 确定性生成；运行时 `StartTime/Duration` |
| `Defect` | 缺陷项：`Severity/Category` + 运行时 `Count` |
| `CounterAlarm` | 计数报警：`IsTriggered => MaxValue>0 && CurrentValue>MaxValue`（数值阈值，区别于 M 位边沿） |
| `User` / `UserRole` | 用户与角色（admin/gly、engineer/gcs、operator） |
| `AppSettings` | 应用设置（JSON 原子写 + .bak/.corrupt 备份），构造默认中文语言 |

#### 3.3.4 OEE 计算（`Services/OeeCalculator.cs`）

静态计算器，全项目 OEE 唯一实现（ADR-4 单源约定）：
- `CalculateQualityRate(ok, ng)`：合格率 = OK/(OK+NG)
- `CalculatePerformanceRate(...)`：性能率 = 实际产量/(节拍×运行小时)
- `CalculateAvailabilityRate(run, alarm)`：可用率 = Run/(Run+Alarm)，**不含 PausedTime**（业务约定）
- `CalculateOee(q, p, a)`：OEE = 三率乘积（全部 Clamp [0,1]）
- `CalculateStateDurations(transitions, from, to, initialState)`：从状态转换记录累计时长

#### 3.3.5 DI 注册（`DependencyInjection/KanbanDataServiceCollectionExtensions.cs`）

`AddKanbanDataServices()` 是**全仓库唯一共享 DI 入口**（MainAPP 与 Collector 的 Program 共用）。注册：
- 基础：AutoMapper(`MappingProfile`)、`AppSettings`、`ProductionBaselineStore`、`DatabaseProvider`、`DeviceRepository`、`WorkOrderRepository`、`DefectHistoryStore`、`AuditService`、`IRuntimeMode→RuntimeMode`
- PLC：5 个 `IPlcBrandDescriptor` + `PlcBrandRegistry`、`SharedPlcDriverFactory/Router`、`IPlcAddressCodecResolver`、`PlcRuntimeProfileProvider`、**`IPlcDriver → SharedPlcDriverRouter`**、`PlcConnectionManager`、`IDeviceAdapter→PlcDeviceAdapter`、`DeviceAdapterResolver`、`PlcDataAcquisitionService`（工厂显式装配 9 个依赖）
- 历史：`ProductionHistoryWriter`、`HistoryService`、各 Store、`HistoryStorageDiagnostics`

#### 3.3.6 本地化（`Localization/` + `Resources/`）

- `ConnectionStatusMessages`：进程间共享连接状态文案（默认中文，`Override` 一次性覆盖、`ApplyLanguage` 读卫星资源）
- `ValidationMessages`：配置校验错误消息（约 20 条，`ApplyLanguage`）
- `Resources/Messages.resx`（中文）+ `.en.resx` + `.ja.resx`：Core 共享文案唯一源；Collector 启动与 MainAPP 启动时均同步语言，保证两进程文案一致

### 3.4 Kanban.Collector — 采集服务进程（`Kanban.Collector/`）

入口 `Program.cs`：单实例互斥（`Global\Kanban.Collector.SingleInstance`，`WaitOne(0)` 健壮版接管 abandoned）→ Serilog → Kestrel 固定监听 `0.0.0.0:5129`（`KanbanHubPaths.DefaultPort`）→ `UseWindowsService("KanbanCollector")` → 注册服务 → SignalR（MessagePack，`MaximumParallelInvocationsPerClient=16`，规避长驻订阅占满执行槽）→ 健康检查 + `/metrics` → 静态托管 WASM wwwroot（`.dat` MIME 显式注册）。

#### 3.4.1 核心服务（`Services/`）

| 类 | 职责 |
|---|---|
| `CollectorWorker` | `BackgroundService` 采集主循环：`InitializeAsync`（配置→语言→审计→设备→建库→工单→启动采集）→ `PeriodicTimer(500ms)` 调 `SnapshotPublisher.PublishAll()` |
| `SnapshotAggregator` | 快照聚合器：`Publish`（更新副本+扇出）、`SubscribeAsync(ct)`（有界 128/DropOldest）、`RemoveDevice`（广播 tombstone） |
| `SnapshotPublisher` | 设备快照聚合为 `DeviceSnapshotDto` 并发布；**增量发布**（业务字段等价比较，静止设备跳过） |
| `EventBroadcaster` | 报警/状态边沿广播：`PublishAlarmEvent/PublishStatusEvent`（单调递增 Seq + 4096 环形缓冲）、`WatchXxxAsync(afterSeq)` 断线补拉；`ServerEpoch` 标识服务端重启 |
| `MetaPublisher` | `IHostedService` 低频元数据发布（5s）：全设备当前工单 + 班次进度，带脏标记缓存 |
| `ShiftProgressProvider` | 班次进度计算：`GetProgress()` → `ShiftProgressDto` |
| `ConfigSyncHandler` | **配置同步（ADR-2 单写者落地）**：`SaveDevicesAsync/GetDevicesAsync/UpsertWorkOrderAsync/DeleteWorkOrderAsync/GetCurrentWorkOrderAsync/SaveCollectorSettingsAsync`（草稿校验→先落盘→热生效，PLC 签名变化则重建连接）、`GetServerVersion/GetTitle/GetLanguage` |
| `HistoryQueryHandler` | 历史查询服务端：`QueryAsync`（`Task.Run` 防阻塞）、`QueryBatchAsync`（≤32 子查询）；防御 `MaxQueryWindow=7天`；实体→DTO 映射在此 |
| `HistoryRetentionService` | `IHostedService`：启动 30s 后首次清理，之后每 24h 按 `KANBAN_HISTORY_RETENTION_DAYS`（默认 365）清理 |
| `CollectorHealthState` / `CollectorMetrics` / `CollectorReadinessCheck` | 健康状态汇总 / `/metrics` 文本渲染（采集/推送/订阅峰值/积压/错误）/ 业务就绪检查 |

#### 3.4.2 Hub（`Hubs/KanbanHub.cs`）

强类型 `Hub<IKanbanHubClient>`，实现 `IKanbanHubServer + IKanbanAdminServer`。客户端方法：快照拉取/订阅、报警/状态事件订阅、历史查询（单/批）、诊断、设备配置读写、工单 CRUD、班次进度、Meta 订阅、采集设置热同步、版本/标题/语言。服务器推送：`OnSnapshot/OnAlarmEvent/OnStatusEvent/OnMeta`。

#### 3.4.3 健康检查端点

- `/healthz`：兼容旧探针（进程存活）
- `/health/live`：纯存活（无注册检查，恒 Healthy）
- `/health/ready`：业务就绪（初始化完成 + 采集活性 + 历史库可写；采集停止返回 503）

### 3.5 MainAPP — WPF 主程序（`MainAPP/`）

详见 [MainAPP/README.md](../MainAPP/README.md)。要点：

#### 3.5.1 启动流程（`App.xaml.cs`）

```
App 构造：单实例 Mutex → Serilog → Host.CreateDefaultBuilder（AddMainAppCoreServices + AddMainAppPresentationServices）
OnStartup：
  1. AppSettings.Load → Localization.Apply（先 Load 再 Apply，防语言恒为中文）
  2. 用 WPF CSV 语言资源 Override Core 的 ConnectionStatusMessages / ValidationMessages
  3. 非首实例 → 提示退出；LicenseGate.CheckStatus() 非 Active/Trial → 弹 ActivationDialog，取消即退出
  4. UserStore.Load（自动建 admin/gly、engineer/gcs、operator）→ AuditLog.Initialize
  5. 非 Viewer 模式默认 admin 自动登录（缺失回退 operator）
  6. _host.StartAsync → ApplicationStartupCoordinator.PrepareAsync() → MainWindow.Show
  7. Dispatcher.Yield(Idle) 保证首帧渲染 → RefreshLicenseStatus / RefreshNavigationForCurrentUser
  8. StartRuntimeAsync()（后台，失败降级不崩溃）
OnExit：停止采集（写离线状态转换防 OEE 虚高）→ 停止日报 → SaveAll → AppSettings.Save → HistoryService.DisposeAsync → Host.StopAsync → 显式释放 → CloseAndFlush → Dispose
```

#### 3.5.2 启动协调（`Services/ApplicationStartupCoordinator.cs` + `ApplicationRuntime.cs`）

- `ApplicationRuntimeState` 状态机：`Starting → LoadingConfiguration → MigratingDatabase → Ready → StartingAcquisition → Running`（异常 `Degraded/Failed`）
- `PrepareAsync`：配置加载（settings/字号/基线）→ 校验警告 → 设备加载 → **仅 Local 模式**建库+工单加载（Remote 跳过，落实单写者）→ 实例化 MainWindow
- `StartRuntimeAsync`：Remote → `StartRemoteDataLinkAsync`（KanbanDataClient 连接 + RemoteRuntimeSink 双连接订阅 + 版本握手 + `RemotePersistenceHook` + `GetDevicesAsync` 屏端零配置拉取）；Local → 后台历史清理 → `PlcDataAcquisitionService.Start()` → `ProductionDailyReportService.Start()`

#### 3.5.3 MainAPP 自有核心服务（`Services/`）

| 服务 | 职责 |
|---|---|
| `RemoteHistoryQueryService` | 历史查询代理：`IsRemote` 时全部查询走 SignalR `QueryHistoryAsync`，否则委托本地 SQLite；DI 中无条件覆盖本地实现（后注册胜出），写方法 Remote 下记录警告并忽略 |
| `RemoteRuntimeSink` | Remote 运行时同步器：**双连接**（快照连接 + 事件/Meta 连接，遵循 ADR-1），500ms 批量闸 + `SnapshotBatchMerger` 合并，回调线程只入队、UI 线程批量应用；5s 后台重连自愈 |
| `WorkOrderService` | 工单业务规则：Add/Copy/Edit/Delete/Start/Complete/Abort（状态机校验、同设备 Running 检查、二次确认） |
| `UserSession` / `AuthorizationService` | 登录态与会话授权（`IsInRole/CanManageDevices/CanManageSystem`） |
| `ProductionDailyReportService` | 每日 PDF 报表（依赖 `IProductionReviewPdfService`，文件存在性防重复生成） |
| `SystemResourceMonitor` / `GpuUsageMonitor` | 系统资源采样（CPU/GPU/内存/磁盘）与 Windows "GPU Engine" 性能计数器 |
| `ProductionReview*` 系列（Data/Metrics/AlarmAnalysis/StatusTimeline/HealthScore/Analysis/Calculations/ChartService/CsvExport/Pdf） | 生产复盘分析全家桶：历史数据访问、指标转换、报警汇总 Top5 + 触发→恢复配对、状态时间线（抖动短段并入）、健康评分（结构化 `HealthIssueKind`）、产量差分（二分窗口 O(log n+m)，修复 12.8s 卡顿）、OxyPlot 图表、CSV/PDF 导出 |
| `ChartService` | 静态 OxyPlot 图表构建（统一深色主题，中文 "Microsoft YaHei" 字体） |
| `DeviceConfigValidator` | 静态校验：地址完整性/名称唯一/报警缺陷阈值/跨设备地址冲突 → `List<DeviceConfigError>` |
| `SampleDeviceBuilder` | DEBUG 构建"生成虚拟设备"：20 台样本 + 地址段分配 + `AssertNoDuplicateAddresses` |
| `DevicePlcCommandHandler` | 设备 PLC 命令：`WriteRecipeAsync/ResetOeeAsync/ReadPlcValueAsync/ResetCounterAlarmAsync`（统一返回 `PlcOpResult`） |
| `Localization` | 语言文化应用（zh-CN/en-US/ja-JP/pt-BR，`CaptureCulture` 防运行期混合） |
| `INavigationService` | 页面导航抽象（`Navigate(pageKey)`） |

#### 3.5.4 导航与 ViewModel（`ViewModels/` + `Models/`）

`NavigationPageCatalog` 定义 12 页：Home、ProductionLine、AlarmCenter、DeviceManager(Engineer)、WorkOrder、HistoryQuery、Overview、Settings(Admin)、RuntimeMonitoring(Admin)、DeviceDetail(隐藏)、UserManager(Admin)、Audit(Admin)。`NavigationPageModule<TView,TViewModel>` 懒加载（DI 解析延迟到页面进入）。

主要 ViewModel 职责：

| ViewModel | 职责 |
|---|---|
| `MainWindowViewModel` | 主窗口：`SelectedNavItem` 映射、全局连接状态横幅（`IsPlcDisconnected/IsDataStale` 10s 停滞阈值）、授权状态展示、按角色过滤导航 |
| `HomeViewModel` | 主页仪表板：2×3 六卡片（设备状态/产量/OEE/质量率/实时报警/缺陷 Pareto），数据全来自内存 Runtime，每 3s 同步 |
| `ProductionLineViewModel` | 产线俯瞰：全部设备状态卡片 |
| `AlarmCenterViewModel` | 实时报警中心（只读监控） |
| `HistoryQueryViewModel` | 历史查询页（生产/报警/状态/OEE/缺陷 5 Tab），`LastQuerySnapshot` 跨会话持久化筛选 |
| `DeviceManagerViewModel` | 设备管理宿主（`IDeviceManagerHost`），承载报警/计数报警/缺陷/工单 4 个 Tab |
| `WorkOrderManagerViewModel` | 工单列表（业务在 `IWorkOrderService`） |
| `SettingsViewModel` | 应用设置（连接/采集/班次/报表/语言/主题，Admin） |
| `UserManagerViewModel` | 用户管理（Admin） |
| `OverviewViewModel`（+`.Summaries.cs`） | 生产复盘总览：全厂产量/报警按小时聚合、Top 报警、每台设备独立 OEE |
| `DeviceDetailViewModel` | 设备详情（上下文页，跨页选中设备同步） |
| `RuntimeMonitoringViewModel` | 运行监控（Admin）：PLC 连接/采集循环/设备读取健康度/系统资源 |
| `LoginViewModel` / `AuditQueryViewModel` | 登录窗口 / 审计查询导出 |
| 查询子 VM（Alarm/Production/Status/Oee） | HistoryQueryView 各 Tab 筛选/分页/导出 |

#### 3.5.5 DI 注册（`Services/MainAppServiceCollectionExtensions.cs`）

- `AddMainAppCoreServices`：`AddKanbanDataServices`（共享入口）+ 用户/授权（UserStore/UserSession/AuthorizationService）+ License 全家（LicenseStore/TrialTracker/LicenseGate 等）+ 工单/复盘/报表 + Remote 三件套（**KanbanDataClient + RemoteRuntimeSink + RemoteHistoryQueryService 无条件重定向 6 个历史接口**）
- `AddMainAppPresentationServices`：Application/Device/History/WorkOrder/Navigation 五模块；12 页 `RegisterPage<TView,TViewModel>`；`MainWindowViewModel/MainWindow` 单例

### 3.6 Kanban.Web — Blazor WASM 大屏（`Kanban.Web/`）

- **`DashboardState`**（唯一数据源，核心类）：**三连接架构**——`_client` 快照订阅 + `_metaClient` Meta 订阅 + `_invokeClient` 查询专用懒连接（ADR-1/ADR-7 落地）。`InitializeAsync` 幂等（5s 重试自愈）；`SubscribeAndRefreshAsync` 严格"先 Invoke（快照/版本/标题/语言）→ 后长驻订阅"；重渲染用 2s Timer 节流；速度点缓存（最多 120 点 ≈ 1 分钟）；tombstone（Removed）处理设备删除
- `Pages/Home.razor`：单页看板 9 卡片（设备状态/生产状态/实时故障/OEE 四环/产量明细/当前工单/速度趋势 sparkline/班次进度/数据源），换算全委托 `SnapshotMetrics` 与 `DurationFormatter`；设备选择 localStorage 持久化
- `Components/Ring.razor`：SVG 环形进度组件；`Components/EmptyState.razor`：统一空状态
- `Localization.cs`：**自动生成**（文件头 WARNING），由 `ci/generate_localization.py --web` 从 `MainAPP/Resources/Localization.csv` 生成，key → 动态语言数组，`L.T(key, args)` 取文案；`Kanban.Web.csproj` 在资源准备前自动执行
- 地址解析：`wwwroot/appsettings.json` 的 `Kanban:CollectorHubUrl` 显式配置优先，留空自动派生 `http://{host}:5129/hubs/kanban`

### 3.7 授权体系（`LicenseManager.App/` + `LicenseIssuer.Wpf/`）

#### 3.7.1 授权核心（`LicenseManager.App/Services/`）

| 类 | 职责 |
|---|---|
| `LicenseGate` | 授权门禁：`CheckStatus()`（机器码缓存 → LoadLicense → **ProductKeyCodec 重新验签**（防篡改 ExpireDate）→ 判定 Active/Expired/MachineMismatch，无记录则委托 TrialTracker）；`TryActivate(productKey, out error)`（验签→绑定→过期→SaveLicense + 成功/失败计数） |
| `LicenseStatus` | `Unlicensed/Trial/TrialExpired/TrialManipulated/Active/Expired/MachineMismatch` |
| `TrialTracker` | 30 天试用：首启写 trial.dat + **注册表备份**（`TrialRegistryBackup`，HKLM/HKCU `SOFTWARE\Kanban`，防删文件重置）；**三重时间回拨检测**（当前时间 vs LastLaunchUtc / FirstLaunchUtc / 系统启动时间） |
| `LicenseStore` | 存储：目录优先级（构造参数 → KANBAN_DATA_DIR → %AppData%\Kanban\）；`license.dat` DPAPI(CurrentUser) 加密 + 原子写；`trial.dat` 在配置旧 HMAC 密钥时使用 HMAC，否则使用当前用户 DPAPI |
| `ActivationAttemptTracker` | 激活防暴力：连续错 5 次锁定，锁定时长指数递增 5min→…→24h |

#### 3.7.2 加密体系（`LicenseManager.App/Crypto/`）

- `ProductKeyCodec`：兼容旧版 HMAC 激活码和新版 ECDSA 激活码。旧码为 23 字节负载，原始 38 字符；新码为 7 字节机器/日期负载 + 1 字节版本 + 64 字节 P-256 IEEE P1363 签名，共 72 字节，原始 117 字符、格式化后 140 字符；两者均使用 Base32 和末尾校验字符，并校验机器绑定。
- `LicenseSigningKey`：MainAPP 只内置 SubjectPublicKeyInfo 公钥；LicenseIssuer 从签发机 `%APPDATA%\Kanban\license-signing-key.pem` 加载 P-256 私钥。私钥必须备份在受控位置，不能进入客户端发布包。
- `EmbeddedKey`：旧版 HMAC 密钥从环境变量 `KANBAN_HMAC_KEY` 注入；值必须是 32 字节密钥的 Base64 形式，不提供内嵌回退。新版 ECDSA 正式激活不需要此环境变量。
- `HmacValidator`：旧版兼容路径使用 HMAC-SHA256 标签和常量时间比较；缺少旧 HMAC 密钥时不影响新版 ECDSA 激活或新机器试用。
- `Base32`：RFC 4648 编解码
- `HardwareFingerprint`：机器码 = WMI（`Win32_Processor.ProcessorId` + `Win32_BaseBoard.SerialNumber` + `Win32_DiskDrive(0)`）→ SHA256 截 5B → Base32 8 字符；进程级缓存 + DPAPI 持久化缓存 `hwid.dat`；标注 `[Obfuscation]`

#### 3.7.3 签发工具

- **LicenseIssuer.Wpf**：GUI 签发（机器码输入 + 永久/到期 + 生成/复制），签发记录写入程序目录下的 `issued/`。

#### 3.7.4 混淆（`LicenseManager.App/obfuscar.xml`）

仅 Release 构建（csproj `ObfuscateRelease` Target，AfterTargets=Build）：`MarkedOnly`（只混淆标注 `[Obfuscation]` 的类型，即 HardwareFingerprint）+ `HideStrings`（保护混淆范围内的字符串）+ `UseUnicodeNames` + `SuppressIldasm`；HMAC 密钥不写入程序集，**明确不混淆** `LicenseManager.Crypto` 命名空间与 MainAPP 直接引用的公共 API。

### 3.8 PlcSimulator — 虚拟 PLC 模拟器（`PlcSimulator/`）

- 职责：基于 HslCommunication `MelsecMcServer`（三菱 MC 协议）托管虚拟 PLC，按 `devices.json` 地址为每台设备运行状态机，模拟真实生产（节拍产出、NG/OK、报警、缺陷、停机、断线），供联调/压力测试/演示/故障演练
- 模式：`host`（默认，内部 port+1 + `TcpRelay` 代理公开端口 4999，代理断线仿真不触发 Hsl 未处理异常崩溃）；`--client IP PORT`（连接外部虚拟 PLC）
- 命令行：`--speed N`（0.1-100）、`--scenario`（normal/stress/demo/fault/counteralarm/disconnect）、`--fresh`、`--noauto`；运行时命令 start/stop/pause/alarm/reset/read/scenario/reload 等；单实例互斥
- 关键类：`DeviceSimulator`（单设备状态机 `Tick`，节拍漂移/NG 分级/随机报警/缺料停机/操作员行为/通信抖动等）；`ScenarioConfig`（约 40 参数 + 6 预设场景）；`DeviceConfig`（PLC 地址模型 + CounterAlarmKind）；`SimLog`（控制台 + sim_log.txt）
- 辅助：`check_db.csx`（统计 4 库行数）；默认 3 台设备（注塑机1/2、组装机1），默认 10 倍速

---

## 4. 关键类速查表

### 4.1 PLC 采集链路（调用链）

```
PlcDataAcquisitionService (200ms 轮询)
  └─ PlcScanPipeline：DWord 批量读计划 (PlcBatchReadPlanner+Cache) → 三组扫描
  └─ DeviceStatusTracker / BaselineResetCoordinator / ShiftContext
  └─ SharedPlcDriverRouter → (品牌) HslPlcDriver/Siemens/ModbusTcp/Omron/Keyence
  └─ DeviceAdapter(PlcDeviceAdapter) → IPlcDriver
  └─ 事件出：AlarmEdgeDetected/StatusEdgeDetected → EventBroadcaster → 订阅客户端
  └─ 快照出：SnapshotPublisher → SnapshotAggregator → Hub → KanbanDataClient → 各端
```

### 4.2 历史写入链路（Collector）

```
采集循环 → HistoryService
  ├─ ProductionHistoryWriter（Channel 10000 + 5s/200 条 + recovery.jsonl + EventId 幂等）→ production_logs.db
  ├─ AlarmHistoryStore → alarm_events.db
  ├─ StatusTransitionHistoryStore → status_transitions.db
  └─ DefectHistoryStore → defect_history.db
查询：HistoryQueryHandler → HistoryService/各 Store → HistoryQueryResponse（强类型 DTO）
```

### 4.3 关键类索引

| 领域 | 关键类（文件路径相对根） |
|---|---|
| 采集调度 | `Kanban.Collector.Core/Services/PlcDataAcquisitionService.cs`、`PlcScanPipeline.cs`、`CollectorWorker.cs`（Collector） |
| PLC 通信 | `Kanban.Collector.Core/Services/IPlcDriver.cs`、`HslNetworkPlcDriver.cs`、`HslPlcDriver.cs`、`HslSiemensPlcDriver.cs`、`HslModbusTcpDriver.cs`、`HslOmronFinsDriver.cs`、`HslKeyenceMcDriver.cs`、`PlcConnectionManager.cs`、`SharedPlcDriverRouter.cs` |
| 地址处理 | `PlcAddressParser.cs`、`PlcAddressCodecs.cs`、`KeyenceAddressCodec.cs`、`OmronAddressCodec.cs`、`PlcBatchReadPlanner.cs`、`PlcBatchReadPlanCache.cs` |
| 设备模型 | `Kanban.Collector.Core/Models/Device.cs`、`DeviceRuntime.cs`、`DeviceCapabilities.cs`、`PLCConfig.cs`、`ShiftConfig.cs`、`Alarm.cs`、`Defect.cs`、`CounterAlarm.cs` |
| 历史存储 | `Kanban.Collector.Core/Services/HistoryService.cs`、`AlarmHistoryStore.cs`、`DefectHistoryStore.cs`、`ProductionHistoryStore.cs`、`ProductionHistoryWriter.cs`、`StatusTransitionHistoryStore.cs` |
| 数据库 | `Kanban.Collector.Core/Data/DatabaseProvider.cs`（含基类）、`*DbContext.cs` ×6、`KanbanDbContextFactory.cs`、`DeviceRepository.cs`、`WorkOrderRepository.cs` |
| 契约/单源 | `Kanban.Contracts/Dtos/*.cs`、`KanbanHubPaths.cs`、`Metrics/SnapshotMetrics.cs`、`Formatting/DurationFormatter.cs` |
| 客户端 | `Kanban.Client/KanbanDataClient.cs`、`IKanbanClient.cs` |
| Hub/服务端 | `Kanban.Collector/Hubs/KanbanHub.cs`、`Services/EventBroadcaster.cs`、`SnapshotAggregator.cs`、`SnapshotPublisher.cs`、`MetaPublisher.cs`、`ConfigSyncHandler.cs`、`HistoryQueryHandler.cs`、`ShiftProgressProvider.cs` |
| 主程序 | `MainAPP/App.xaml.cs`、`Services/ApplicationStartupCoordinator.cs`、`ApplicationRuntime.cs`、`RemoteRuntimeSink.cs`、`RemoteHistoryQueryService.cs`、`MainAppServiceCollectionExtensions.cs` |
| ViewModel | `MainAPP/ViewModels/`（MainWindow/Home/ProductionLine/AlarmCenter/HistoryQuery/DeviceManager/Overview/DeviceDetail/RuntimeMonitoring/Settings/UserManager/Login 等） |
| WASM | `Kanban.Web/DashboardState.cs`、`Pages/Home.razor`、`Components/Ring.razor`、`Localization.cs`（自动生成） |
| 授权 | `LicenseManager.App/Services/LicenseGate.cs`、`TrialTracker.cs`、`LicenseStore.cs`、`Crypto/ProductKeyCodec.cs`、`Crypto/HardwareFingerprint.cs` |
| 模拟器 | `PlcSimulator/DeviceSimulator.cs`、`ScenarioConfig.cs`、`Program.cs` |

---

## 5. 依赖关系

### 5.1 项目引用矩阵

| 项目 | 引用项目 | NuGet 关键包 |
|---|---|---|
| Kanban.Contracts | 无 | 无 |
| Kanban.Client | Contracts | SignalR.Client、SignalR.Protocols.MessagePack |
| Kanban.Collector.Core | Contracts | AutoMapper、CommunityToolkit.Mvvm、CsvHelper、EF Core.Sqlite、NodaTime、Serilog、Microsoft.Data.Sqlite；裸引用 HslCommunication.dll |
| Kanban.Collector | Contracts、Collector.Core | FrameworkReference ASP.NET Core、SignalR.Protocols.MessagePack、WindowsServices、Serilog |
| MainAPP | LicenseManager.App、Kanban.Client、Kanban.Collector.Core、OxyPlot.Wpf、OxyPlot.SkiaSharp | HandyControl、Material.Icons.WPF、PdfSharpCore、SignalR.Client、EF Core.Sqlite、Serilog 等（CPM 统一版本）；裸引用 HslCommunication.dll |
| Kanban.Web | Kanban.Client | Blazor WebAssembly ×2（编译前 python 生成 Localization.cs） |
| LicenseManager.App | 无项目引用 | CommunityToolkit.Mvvm、HandyControl（Obfuscar 仅 Release） |
| LicenseIssuer.Wpf | LicenseManager.App | HandyControl |
| PlcSimulator | 无 | 裸引用 HslCommunication.dll、System.IO.Ports |
| 测试/基准 | MainAPP（E2E/UIAutomation 另引 Collector、Microsoft.AspNetCore.App 等） | xunit.v3.mtp-v2、coverlet.MTP、FlaUI.UIA3、Appium、NSubstitute、BenchmarkDotNet |

### 5.2 集中版本管理

`Directory.Packages.props`（CPM，`ManagePackageVersionsCentrally=true`）。典型版本：AutoMapper 14.0.0、CommunityToolkit.Mvvm 8.4.0、HandyControl 3.5.1、EF Core / Sqlite / SignalR / Serilog 系列 10.0.10、NodaTime 3.3.3、PdfSharpCore 1.3.67、FlaUI.UIA3 5.0.0、Appium.WebDriver 5.0.0、NSubstitute 5.3.0、BenchmarkDotNet 0.15.8、Obfuscar 2.2.50。OxyPlot 已切换为本地源码引用（`oxyplot/Source/...`）。

### 5.3 目录结构

```
Kanban/
├── Kanban.Contracts/           共享契约（DTO/Enums/Hub 契约/单源计算）
├── Kanban.Client/              共享 SignalR 客户端
├── Kanban.Collector.Core/      采集核心（PLC/历史/模型/DI）
├── Kanban.Collector/           采集服务进程（Hub/Worker/发布器）
├── MainAPP/                    WPF 主程序（Views/ViewModels/Services/...）
├── Kanban.Web/                 Blazor WASM 大屏
├── LicenseManager.App/         授权核心
├── LicenseIssuer.Wpf/         激活码签发 GUI
├── PlcSimulator/               虚拟 PLC
├── MainAPP.Tests/              单元+集成测试（Unit/ Integration/）
├── MainAPP.E2E/                进程内 E2E
├── MainAPP.UIAutomation/       真实进程 UI 自动化
├── MainAPP.Benchmarks/         基准测试
├── oxyplot/                    本地 vendored 的 OxyPlot 源码
├── ci/                         Python/PS 脚本（发布/i18n/冒烟/UI 自动化）
├── docs/                       架构决策记录 / 设计方案 / 本 Wiki
└── .github/workflows/          tests.yml / publish.yml / ui-automation.yml
```

---

## 6. 运行方式

### 6.1 前置条件

- Windows（WPF/HslCommunication 依赖）、.NET SDK 10.0.100（`global.json` 固定，`rollForward: latestMinor`）
- 首次构建需确认 `MainAPP/DLLS/HslCommunication.dll` 存在（本地裸引用）
- WASM 构建需要 `wasm-tools` workload（CI 中显式安装：`dotnet workload install wasm-tools`）

### 6.2 构建

```powershell
dotnet restore Kanban.slnx
dotnet build Kanban.slnx -c Debug
dotnet build Kanban.slnx -c Release   # 验证 Obfuscar + Release 配置
```

### 6.3 运行主程序（Local 模式，默认）

MainAPP 使用新版 ECDSA 激活码时不需要配置 `KANBAN_HMAC_KEY`。新电脑没有激活码时会进入 30 天试用；取得激活码后，在激活对话框中直接粘贴即可。`KANBAN_HMAC_KEY` 只在验证旧版 HMAC 激活码或读取旧 HMAC 状态时需要，已有旧码必须使用签发时的原密钥，不能在每台电脑重新生成：

```powershell
# 仅兼容旧 HMAC 激活码：使用签发旧码时的原密钥
.\ci\configure-hmac-key.ps1 -HmacKey "<签发旧版激活码时使用的 32 字节 Base64 密钥>"
```

```powershell
dotnet run --project MainAPP\MainAPP.csproj
```

指定独立数据目录（开发/演示/多环境）：

```powershell
$env:KANBAN_DATA_DIR = "D:\KanbanData\dev"
dotnet run --project MainAPP\MainAPP.csproj
```

数据根目录默认 `%APPDATA%\Kanban`（`Config/` 存 settings.json / devices.json / baselines.json；6 个 SQLite 历史库由 `DatabaseProvider` 管理）。

### 6.4 运行采集服务（Remote 模式 / 独立部署）

```powershell
dotnet run --project Kanban.Collector\Kanban.Collector.csproj   # 控制台方式，监听 0.0.0.0:5129
```

- 单端口部署：Collector 同时静态托管 WASM 屏端产物（`wwwroot/`），浏览器访问 `http://host:5129/` 直接开看板（与 Hub 同源免 CORS）
- 健康检查：`/healthz`、`/health/live`、`/health/ready`；指标：`/metrics`
- 注册为 Windows 服务：

```powershell
.\ci\install-collector-service.ps1 -Action Install -DataRoot D:\KanbanData   # 先配置 KANBAN_HMAC_KEY；开机自启 + 崩溃重启
```

### 6.5 运行屏端（开发）

```powershell
dotnet run --project Kanban.Web\Kanban.Web.csproj   # http://localhost:5186，自动派生到 :5129 的 Collector
```

### 6.6 运行 PLC 模拟器（联调/演示）

```powershell
dotnet run --project PlcSimulator\PlcSimulator.csproj          # host 模式，端口 4999，默认 10 倍速
dotnet run --project PlcSimulator\PlcSimulator.csproj -- --scenario stress --speed 20
```

默认读取 `%KANBAN_DATA_DIR%\Config\devices.json`（不存在则生成内置 3 台设备：注塑机1/2、组装机1）。

### 6.7 授权签发

```powershell
# 查看机器码（主程序激活对话框展示）→ 在 WPF 签发工具中输入 8 位 Base32 机器码
dotnet run --project LicenseIssuer.Wpf\LicenseIssuer.Wpf.csproj
```

WPF 签发工具支持生成永久授权或限期授权，并自动复制激活码；签发记录写入工具程序目录下的 `issued/`。

新版正式激活不需要在生产目标机注入密钥。签发机必须保管 `%APPDATA%\Kanban\license-signing-key.pem`，并在签发工具所在机器以受控方式备份；发布 MainAPP 前确认 `LicenseSigningKey` 中的内置公钥与该私钥匹配。旧版 HMAC 激活码仍需通过 `configure-hmac-key.ps1` 注入原 32 字节 Base64 密钥；不要把任何私钥或 HMAC 密钥写入程序集或发布目录。

签发私钥的首次初始化属于发布准备工作：生成 P-256 密钥对，将 SubjectPublicKeyInfo 公钥 Base64 写入 `LicenseSigningKey`，再构建并发布 MainAPP；当前生产私钥必须作为原始 PEM 备份。恢复时只能复制备份的原始 PEM 到 `%APPDATA%\Kanban\license-signing-key.pem`，不能重新生成替代密钥。LicenseIssuer 会在签发时拒绝与客户端内置公钥不匹配的私钥；密钥轮换必须同步修改公钥并重新发布客户端，旧客户端不会接受新密钥签发的激活码。

### 6.8 一键发布（部署包）

```powershell
.\ci\publish-all.ps1 -Configuration Release -Runtime win-x64 [-SelfContained] [-SkipSim]
```

产物结构：`<Out>/Collector`（含 WASM wwwroot）、`<Out>/MainAPP`、`<Out>/PlcSimulator`、`SHA256SUMS.txt`。

新电脑首次启动 MainAPP 时无需预配置 HMAC 密钥，直接运行 `start-mainapp.cmd` 或 `MainAPP.exe` 即可进入试用；正式授权时在激活对话框中直接粘贴新版 ECDSA 激活码。若收到的是旧版 HMAC 激活码，再在发布包根目录以管理员身份运行：

```powershell
.\configure-hmac-key.ps1 -HmacKey "<签发旧版激活码时使用的 32 字节 Base64 密钥>"
```

也可以直接双击发布包根目录的 `start-mainapp.cmd`。如果环境变量尚未配置，启动器会遮罩询问密钥，并且只把密钥注入本次 MainAPP 进程；需要持久化到机器环境时，使用上面的配置脚本，或在管理员 PowerShell 中运行 `.\start-mainapp.cmd -PersistMachine`。

### 6.9 测试

```powershell
# 单元 + 集成（MTP）
dotnet test --project MainAPP.Tests\MainAPP.Tests.csproj -c Debug
# 按分类过滤
dotnet test --project MainAPP.Tests\MainAPP.Tests.csproj -c Debug --filter "Category=Simulation"
# 覆盖率（cobertura）
dotnet test --project MainAPP.Tests\MainAPP.Tests.csproj -c Debug --coverlet --coverlet-output-format cobertura
# 进程内 E2E
dotnet test --project MainAPP.E2E\MainAPP.E2E.csproj -c Debug
# UI 自动化（FlaUI；加 -WithWinAppDriver 启用 WinAppDriver/Appium）
.\ci\run-ui-automation.ps1 [-WithWinAppDriver]
```

**约束**：MainAPP.Tests / E2E / UIAutomation 共享 MainAPP 的 obj 缓存，必须**串行执行**（脚本内置并发防护 exit 2）。推荐先统一 `dotnet build` 再各 `dotnet test --no-build`。WPF 测试走专用 STA 线程（`WpfStaFixture`）。

### 6.10 基准测试

```powershell
dotnet run --project MainAPP.Benchmarks\MainAPP.Benchmarks.csproj -c Release -- --job short --filter '*' --exporters json,html --memory
```

### 6.11 CI（`.github/workflows/`）

| 工作流 | 触发 | 要点 |
|---|---|---|
| `tests.yml` | push/PR main|master + workflow_dispatch | 构建 + EF 迁移校验 + 单测（覆盖率 `<60%` 失败）+ E2E + i18n 单一源守卫 + Release 构建 + publish 制品 + 基准（continue-on-error）+ publish-bundle（publish-all + WASM smoke：`/healthz` 200 + playwright-core 16 断言） |
| `publish.yml` | push tag `v*` + workflow_dispatch | run-tests 门禁 → publish-all（win-x64/x86/arm64、可选 self-contained/simulator）→ SHA256SUMS → GitHub Release |
| `ui-automation.yml` | push/PR main|master 命中 MainAPP/ci | choco install winappdriver → run-ui-automation.ps1 -WithWinAppDriver → 上传 TestResults |

---

## 7. 开发约定（新增代码时注意）

1. **单写者**（ADR-2）：任何"持久化写"必须经 Hub 落到 Collector（`SaveDevicesAsync/UpsertWorkOrderAsync/SaveCollectorSettingsAsync`），MainAPP 侧只维护内存态。
2. **单源计算**（ADR-4）：OEE 用 `OeeCalculator`、快照换算用 `SnapshotMetrics`、时长用 `DurationFormatter`、Hub 路径用 `KanbanHubPaths`——禁止两端内联复制公式。
3. **订阅纪律**（ADR-1/ADR-7）：每个连接最多一个长驻订阅；订阅发起后不得同连接再 Invoke，需要查询另开短连接。
4. **领域逻辑放 Core**：新领域逻辑放 `Kanban.Collector.Core`（命名空间 `Kanban.Core.*`）；新跨进程契约放 `Kanban.Contracts`；UI 逻辑放 MainAPP/Web 各自侧。共享 DI 入口只有 `AddKanbanDataServices`。
5. **多语言**：UI 文案单一源 = `MainAPP/Resources/Localization.csv`（`Resource,Key,zh-CN,en-US,ja-JP,pt-BR`），构建时生成 WPF/Core RESX、`Strings.cs` 与 Web `Localization.cs`；CI 通过 `ci/generate_localization.py --check` 守卫资源漂移，并执行中文泄漏扫描。
6. **PLC 协议扩展**：实现 `IDeviceAdapter` + `IPlcBrandDescriptor` 并注册到 DI，不要在 `PlcDataAcquisitionService` 中加协议判断分支。
7. **枚举持久化**：`AlarmEventType/DeviceStatus/WorkOrderStatus` 数值已入库，禁止调整数值或插入中间值。
8. **版本信息**：Collector 服务进程 `Version 1.0.0`（`GetServerVersionAsync` / `/metrics` 返回），升级发布可追溯。

---

## 8. 参考文档

| 文档 | 位置 | 内容 |
|---|---|---|
| 架构决策记录 | `docs/架构决策记录.md` | ADR-1~7 架构约束与理由 |
| MainAPP 使用说明 | `MainAPP/README.md` | 主程序构建/启动/配置/调试 |
| 授权设计方案 | `docs/授权激活码设计方案.md` | 授权体系设计（与实现有出入，已在本 Wiki 3.7 节按实际代码说明） |
| MainAPP 易用性建议 | `docs/MainAPP易用性建议.md` | UX/易用性评审建议 |
