# 综合代码审查报告 — Kanban 工作区未提交改动

**日期**：2026-08-30
**工作流**：工作流 1 · 全面代码审查
**参与成员**：科迪（code-reviewer）· 阿奇（architect）· 泰莎（testing-expert）
**审查范围**：`main` 分支当前未提交工作树改动（65 文件 / ~5196 行新增 / 258 行删除；29 个生产 `.cs` + 14 个测试 `.cs` + csproj/resx/xaml/csv 资源）

---

## 📌 TL;DR（执行摘要）

- **整体结论**：改动质量较高、分层边界健康（ViewModel 零直接 DB 访问）、并发设计成熟；**建议合入，但需先处理 3 项阻塞项**。
- **严重度分布**：🔴严重 3 项（均为测试盲区）/ 🟠高 6 项 / 🟡中 15 项 / 🟢低 10 项
- **阻塞项**：① 报警持续时长列不渲染（功能性缺陷）；② SN 事件背压/溢出静默丢数据（质量追溯合规风险）；③ `RemoveDataSourceState` 死代码 + 未来并发隐患
- **最突出主题**：新增「SN 序列号逐件追溯」功能是本次最大增量，但**采集端、视图模型入口、远端链路三段均无单测**，是核心测试盲区（🔴）。
- **安全面**：无 SQL 注入（全走 EF 参数化）、无硬编码密钥；仅 SN Hub 鉴权与 CSV 注入为低优待确认项。

---

## 🎯 核心结论卡片

| 项目 | 内容 |
|------|------|
| 整体评级 | 🟡 有条件通过（3 项阻塞需先修） |
| 阻塞项数量 | 3 |
| 关键行动项 | 9 条（见行动清单，P0×3 / P1×4 / P2×2） |
| 建议下一步 | 先修 3 阻塞项 → 收敛 `ISnEventStore` 隐式双注册 → 补齐 SN 追溯测试基线 |

---

## 🔍 审查发现（按严重度排序）

### 🔴 严重（测试覆盖缺口，3 项）

| # | 严重度 | 类别 | 文件:行 | 问题描述 | 建议修复 | 来源 |
|---|--------|------|---------|----------|----------|------|
| T1 | 🔴严重 | 测试覆盖 | PlcScanPipeline.cs / SnEventStore.cs | SN 事件**采集端**（`Append`、同源去重键、结果值项识别、Running 工单关联）**零单测**，追溯"写"半边完全未验证 | 新增 `PlcScanPipelineTests`：Mock `ISnEventStore`+provider，验证触发/去重/结果位/store=null 跳过 | Tessa #1 |
| T2 | 🔴严重 | 测试覆盖 | SnQueryViewModel.cs（全新） | SN 追溯 Tab 主入口 `SearchAsync`/`Reset`/`K921~K923`/`IsEmptyResult` **无任何测试文件** | 新增 `SnQueryViewModelTests`：空 SN/命中/未命中/异常/store=null 回退 | Tessa #2 |
| T3 | 🔴严重 | 测试覆盖 | RemoteHistoryQueryService.cs（+93） | 远端 `ISnEventStore` 分支（`QueryRemoteSn` 10s 超时文案、`ToEntity` 映射、Remote `Append` 忽略）**仅注入了构造器，零行为测试** | Mock `KanbanDataClient` 测远程查询路径 + 超时文案 + `Append` 忽略 | Tessa #3 |

### 🟠 高（代码 / 架构缺陷，6 项）

| # | 严重度 | 类别 | 文件:行 | 问题描述 | 建议修复 | 来源 |
|---|--------|------|---------|----------|----------|------|
| F1 | 🟠高 | 正确性 | AlarmQueryViewModel.cs:82 vs 143-150 | `Query()` 先在 82 行 `Page()` 装填 `AlarmEvents`，后于 143 行回填 `DurationText`；`AlarmEventRecord` 非 INPC，DataGrid 已渲染 → **持续时长/MTTR 列恒空** | 把 `DurationText` 回填前移至 `Page()` 之前（同对象引用，装载即带值） | Cody #2 / Archi #8 |
| F2 | 🟠高 | 正确性/数据 | SnEventStore.cs:66-72,92-99；PlcScanPipeline.cs | SN 事件满队列**静默丢弃**仅 `LogWarning`。⚠️ 两成员对 `BoundedChannel FullMode=Wait` 行为判断**存在分歧**：code-reviewer 认为满时 `TryWrite` 返回 false 并丢失；testing-expert 认为 Wait 会阻塞、`_overflowCount` 与"丢弃"日志为死代码。需**先验证实际语义** | 明确背压语义（统一 `DropOldest`/`Wait`），溢出计数/告警对外可观测；补背压单测；评估 `Append` 是否应在采集线程同步阻塞 | Cody #4 / Tessa #1 |
| F3 | 🟠高 | 架构/可维护性 | MainAppServiceCollectionExtensions.cs:110 + KanbanDataServiceCollectionExtensions.cs:43-44 | `ISnEventStore` 同接口两次注册依赖 MS DI「后注册胜出」隐式契约做 Local/Remote 分派；重排/拆分将**静默翻转** Local/Remote 行为，无编译期/启动期校验 | 改用按 `IRuntimeMode` 的工厂或命名键接口（`ILocalSnEventStore`/`IRemoteSnEventStore`），组合根一次性决定 | Archi #1 / Cody #5 |
| F4 | 🟠高 | 性能/正确性 | KanbanHub.cs:118-191；SnEventStore.cs:178-186 | `QuerySnEventsAsync` 是 SignalR Unary 方法，体内同步调用 store 查询，而 `FlushBeforeQuery()` 用 `.GetAwaiter().GetResult()` 阻塞；SN 查询在 **hub 调度线程**做 SQLite 读写并与后台落库串行，并发查询互相阻塞、拖慢采集通道 | 最小：hub 方法体 `Task.Run(...)` offload；彻底：`ISnEventStore` 查询改 `Task` 返回、hub 内 `await` | Cody #1 / Archi #6 / Tessa #9 |
| F5 | 🟠高 | 架构/分层 | WorkOrder.cs + WorkOrderManagerViewModel.cs:341-343 | `WorkOrder` 实体承载 `[NotMapped]` 的 `IsOverdue`/`OverdueHintText`/`IsScheduleConflict` 等 UI 运行时态；逾期/冲突/达标率/CSV 解析等大量领域逻辑落在 VM | 展示态移入 `WorkOrderRowViewModel` 投影；领域计算下沉 `WorkOrderService` | Archi #2 |
| F6 | 🟠高 | 架构/可维护性 | RemoteHistoryQueryService.cs:24-31,45-59 | 单类实现 7 接口（含新增 `ISnEventStore`），近 900 行，远程链路单点（god-service 趋势） | 拆出 `RemoteSnEventStore` 代理（同历史代理结构），翻页/批量/超时抽共享 helper | Archi #3 |

### 🟡 中（15 项）

| # | 严重度 | 类别 | 文件:行 | 问题描述 | 建议修复 | 来源 |
|---|--------|------|---------|----------|----------|------|
| F7 | 🟡中 | 正确性/并发 | PlcScanPipeline.cs:41-44,474-492 | `RemoveDataSourceState`（清理 SN 去重字典）**全工作树无调用方**——死代码 + 潜在内存增长；注释暗示未来跨线程调用，而字典非线程安全 | 接线到数据源删除路径（与扫描同线程）或加 `lock`；暂不用则移除并标注 | Cody #3 |
| F8 | 🟡中 | 正确性 | PlcScanPipeline.cs:474-492 | SN 去重键 `device:source` 仅保留"上一条 SN"，连续相同 SN 抑制；返工同 SN 合法复现会被误抑制；键 `OrdinalIgnoreCase` 但值比对 `Ordinal`（大小写敏感） | 文档化语义；防真实重复应基于去重窗口/TTL 而非"仅上一条" | Cody #6 |
| F9 | 🟡中 | 正确性 | WorkOrder.cs:127-129 | `Abort()` 对 Pending→Abort（从未开始）仍写 `CompletedAt ??= DateTime.Now`，但 `StartedAt` 为 null；"未开始却已结束"语义不一致 | 未开始则不写 `CompletedAt`（保持 null），或注释口径 | Cody #7 |
| F10 | 🟡中 | 可维护性 | HistoryQueryViewModel.cs:665-690 | `ScheduleAutoQuery` 每次 `new CancellationTokenSource()`，`Cancel()` 后未 `Dispose()`；长会话高频筛选产生大量未释放 CTS | `Cancel()` 后 `Dispose()` 旧 CTS，或复用单一 CTS | Cody #8 |
| F11 | 🟡中 | 性能 | AlarmCenterViewModel.cs（RefreshStatsCore） | 60s 定时器在 UI 线程同步执行历史库查询，本次新增"昨日同期"二次查询，分组/聚合仍在 UI 线程，大报警量有卡顿风险 | 将查询+聚合 offload 后台线程（已有 `_statsRefreshVersion` 过期丢弃机制可复用） | Cody #9 |
| F12 | 🟡中 | 架构/迁移 | DatabaseProvider.cs:228-231 + 新迁移 AddStatusTimestamps | WorkOrder 同时存在 EF 迁移与 `ApplyWorkOrderLegacyPatch` 的 `EnsureColumn`（同列重复加列），对 `EnsureCreated` 老库（无迁移历史）可能触发"重复列"错误 | 在 `DatabaseMigrationCompatibilityTests` 用真实 EnsureCreated 老库跑升级验证；统一单一补列机制 | Archi #4 |
| F13 | 🟡中 | 架构/并发 | SnEventStore 构造 + AddKanbanDataServices | Remote 模式 MainAPP 仍构造本地 `SnEventStore` 并 `EnsureCreated` 打开 `sn_events.db`，与 Collector 同机部署时 SQLite 文件锁竞争（Remote 下 Append 被忽略但连接仍开） | Remote 模式用 `NullSnEventStore` 占位，不构造/不注入本地实例 | Archi #5 |
| F14 | 🟡中 | 并发 | PlcScanPipeline.cs:43-47 | `_lastRecordedSnBySource` 为普通 `Dictionary`，假定"每实例单扫描线程"；若引入并行扫描将产生数据竞争 | 注明单线程约束，或改用 `ConcurrentDictionary` | Archi #7 |
| F15 | 🟡中 | 架构/可维护性 | SnQueryViewModel.cs + HistoryQueryViewModel.cs:369 | `SnQueryViewModel` 未注册 DI，由父 VM 直接 `new`（依赖 `ISnEventStore?`） | 也注册为单例由父 VM 解析，统一生命周期与可测试性 | Archi #10 |
| F16 | 🟡中 | 测试覆盖 | AlarmCenterViewModel.cs（+330 KPI） | 本班次窗口 `CurrentShift`、较昨日同期 `FormatCompareText`、按持续时长 `ByTotalDuration`/`ComputeAlarmTotalDuration`、**无单测**，错误口径静默误导排障 | 单测：CurrentShift 跨午夜回退、昨日对比 Δ%、按时长排序 | Tessa #4 |
| F17 | 🟡中 | 测试覆盖 | DeviceManagerViewModel.cs（+179 命令） | `ValidateAllDevices`/`ExportDevice`/`ImportDevice`（JSON 解析失败/`Id` 空/已有 Id 冲突/`CloneDevice`）**无单测**，导入分支未验证 | 单测 `ImportDevice`：文件损坏、Id 空、Id 冲突、保留源 Id | Tessa #5 |
| F18 | 🟡中 | 测试覆盖 | WorkOrderManagerViewModel.cs（+389 SN 明细） | `LoadSnItems` 分页/选中切换自动加载/过期结果丢弃/`IsSnSupported=false` 回退**未覆盖**（仅甘特/逾期/冲突被测） | 单测 `LoadSnItems`：分页计数、切换工单丢弃旧结果、不支持时清空 | Tessa #6 |
| F19 | 🟡中 | 测试覆盖 | WorkOrderService.cs（ValidateImportCandidate） | 工单导入三条 happy path 有测，但空 `OrderNo/ProductCode/ProductName`、`TargetQuantity<=0`、`PlannedEnd<=PlannedStart` 等非法分支**未测**，可能静默接受非法导入 | 补 `ValidateImportCandidate` 各失败分支用例（断言 `Errors` 含 K599/K600/K601/K603/K604） | Tessa #8 |
| F20 | 🟡中 | 测试覆盖/安全 | KanbanHub.cs:118 | `QuerySnEventsAsync` 分支遗漏：时间范围分支、分页归一化（`page<=0→1`、`pageSize` clamp `[1,200]`）、异常 catch 返回空**无单测**，服务端安全边界未验证 | Hub 单测：`page=-1`、`pageSize=99999`、`DeviceId+时间范围`、store 抛异常→返回空 `Total=0` | Tessa #9 / Cody #14 |
| F21 | 🟡中 | 测试覆盖 | CollectionContainsConverter.cs / WorkOrderEndLabelConverter.cs（全新） | 两个新 Converter（逾期/冲突行高亮、结束标签）**无测试**，错误会静默导致行高亮/标签错误 | 加入 `AdditionalConverterCoverageTests`：集合包含/多值、Aborted→K776/其他→K775 | Tessa #11 |

### 🟢 低（10 项）

| # | 严重度 | 类别 | 文件:行 | 问题描述 | 建议修复 | 来源 |
|---|--------|------|---------|----------|----------|------|
| F22 | 🟢低 | 可维护性/时区 | SnEventRecord.cs:42；SnEventStore.cs:168,221 | 全链路用 `DateTime.Now`（本地时间）；若传入 `DateTime.UtcNow` 会使 SQLite TEXT 比较错位 | 统一 `DateTime.UtcNow` 并标注 Kind，或文档约定只接受本地时间 | Cody #10 |
| F23 | 🟢低 | 性能 | WorkOrderManagerViewModel.cs:280-290,550-571 | 每分钟定时器重建甘特 PlotModel + `ComputeConflictOrderIds` O(n²) 冲突扫描（即便面板折叠），UI 线程 | 折叠时跳过重建；冲突计算缓存/增量 | Cody #11 |
| F24 | 🟢低 | 性能 | *BuildCsvAll（Production/Alarm/Status QueryViewModel） | 全量导出在内存拼接整段 CSV 字符串，10万+ 时内存与单次写入压力大 | 超大场景改用流式写出 | Cody #12 |
| F25 | 🟢低 | 可维护性 | LocalizationCatalog.cs（+386，多为 CRLF→LF） | 大量行变更实为行尾规范化噪音，审查易掩盖真实改动 | 团队统一行尾（`.gitattributes text=auto`） | Cody #13 |
| F26 | 🟢低 | 安全 | KanbanHub.cs:118 | SN 逐件 genealogy 属敏感数据，未显式要求鉴权/授权 | 确认 Hub 仅暴露于可信内网，否则加 `[Authorize]`/角色校验 | Cody #14 |
| F27 | 🟢低 | 安全 | 各 `BuildCsv*` 导出 | 导出字段可能含 `= + - @` 开头，Excel 中可被解释为公式（CSV 注入） | 危险前缀加 `'` 或统一引号转义 | Cody #15 |
| F28 | 🟢低 | 测试质量 | HistoryQueryViewModelTests / WorkOrderManagerViewModelTests / WpfStaFixture | ① `SelectedOverdueHintText_..._TimerPath` 名不副实（未触发真实 Tick）；② `FilterChange_AfterDebounce` 用 `Thread.Sleep` 轮询（flaky）；③ `WpfStaFixture` 内联复制 `AppIcon` 几何（易漂移） | 用真实计时器/可测时钟；注入时钟或手动触发；抽取共享资源字典 | Tessa #2,#3,#4 |
| F29 | 🟢低 | 测试覆盖 | HistoryQueryViewModel.cs（+235 SN Tab） | `case 4` + `CreateSnResult` SN Tab 路由 + 导出 SN 分支无端到端测试 | 配合 T2 覆盖 `case 4` 路由、`PrepareAlarmHistory` 跳转 | Tessa #7 |
| F30 | 🟢低 | 测试覆盖 | SnEventStore.cs（QueryByTimeRange/GetDiagnosticsSnapshot） | 时间范围+设备过滤分支、诊断健康度计数仅经 Hub 间接覆盖 | 直接单测 + 诊断快照 `PendingCount/OverflowCount` | Tessa #10 |
| F31 | 🟢低 | 测试覆盖 | WorkOrderManagerView.xaml.cs（+39 双击编辑） | 纯 UI 事件，低风险，无测试 | 可选：UI 自动化或 `MainWindowE2ETests` 扩展双击 | Tessa #12 |

---

## 🏗️ 架构影响评估（阿奇）

- **整体健康度**：🟡 需关注。结构清晰、组合根分层明确（Core / MainAPP 远程链路 / Presentation 三层），ViewModel 全量依赖服务抽象（零 `DbContext`/`DatabaseProvider` 直访），并发重构（`SharedPlcDriverRouter` 引用计数、`SnEventStore` 有界通道+批量落库）质量高。
- **核心风险**：① `ISnEventStore` 隐式"后注册胜出"双注册（F3）；② `WorkOrder` 实体混入 UI 态 + 领域逻辑落 VM（F5）；③ `RemoteHistoryQueryService` god-service（F6）。均非阻断，但属"能跑、但脆弱/难维护"。
- **迁移一致性**：SN 库走 `EnsureCreated` 不参与迁移（无漂移，良）；WorkOrder 迁移与快照一致；但 EF 迁移与 legacy patch **同列双写**对老库是隐患（F12）。
- **👍 正面实践（建议保留/推广）**：`SharedPlcDriverRouter` 锁外 IO + 引用计数退役；`SnEventStore` 通道+批量+`_flushGate` 模式；ViewModel 分层边界；SignalR Hub 分页归一化与异常隔离。

---

## 🧪 测试覆盖评估（泰莎）

- **整体结论**：🟡 部分覆盖。已改测试**质量高**（全部含行为断言、非"仅编译通过"），但**与生产改动同步性存在盲区**。
- **同步到位的区域（肯定）**：工单甘特/冲突/逾期、未保存离开保护、报警风暴合并、PLC 并发（`SharedPlcDriverRouterTests`）、数据库迁移兼容性——均有充分断言。
- **核心盲区**：SN 追溯"采集端 + 视图模型入口 + 远端链路 + 转换器"四段全无测试（T1/T2/T3 + F21）；报警中心新 KPI、设备管理新命令、工单导入校验分支、Hub SN 安全边界亦缺（F16-F20）。
- **测试设计问题**：`SnEventStore` 背压语义自相矛盾（见 F2，已转交代码审查）；名不副实测试；flaky `Thread.Sleep` 防抖测试；`WpfStaFixture` 资源重复。
- **优先补测**：`SnQueryViewModelTests` + `PlcScanPipelineTests`、`RemoteHistoryQueryService` 远端 SN 单测、`AlarmCenterViewModel` KPI 单测、`KanbanHub.QuerySnEventsAsync` 边界单测、工单导入/设备导入校验单测。

---

## ✅ 行动清单（按优先级排序）

| # | 行动 | 负责角色 | 紧急度 | 预期完成 |
|---|------|---------|--------|---------|
| 1 | 修复报警持续时长列不渲染：将 `DurationText` 回填前移至 `Page()` 之前（F1） | 科迪 / 开发 | P0 | 合入前 |
| 2 | 厘清 `SnEventStore` 背压语义（先验证 `BoundedChannel.FullMode=Wait` 实际行为），使溢出可观测并补背压单测（F2） | 科迪 + 泰莎 | P0 | 合入前 |
| 3 | 处理 `RemoveDataSourceState` 死代码：接线到数据源删除路径（同线程/加锁）或显式移除标注（F7） | 科迪 / 开发 | P0 | 合入前 |
| 4 | 收敛 `ISnEventStore` 双注册为显式 `IRuntimeMode` 工厂分发，消除"后注册胜出"隐式契约（F3/F13） | 阿奇 / 开发 | P1 | 下个迭代 |
| 5 | 将 SignalR `QuerySnEventsAsync` 改为真正异步（`ISnEventStore` 查询改 `Task` 返回 + hub `await`），消除调度线程阻塞（F4） | 科迪 / 开发 | P1 | 合入前（高优） |
| 6 | 补齐 SN 追溯测试基线：`SnQueryViewModelTests` + `PlcScanPipelineTests` + `RemoteHistoryQueryService` 远端分支（T1/T2/T3） | 泰莎 / 开发 | P1 | 合入前 |
| 7 | 拆分 `RemoteHistoryQueryService` god-service，抽出 `RemoteSnEventStore` 代理（F6） | 阿奇 / 开发 | P1 | 后续迭代 |
| 8 | 修复正确性/可维护性中项：SN 去重语义（F8）、`Abort` 语义（F9）、CTS 释放（F10）、报警统计 offload（F11）、迁移双写验证（F12） | 科迪 / 开发 | P2 | 后续迭代 |
| 9 | 低优收尾：时区统一（F22）、甘特节流（F23）、CSV 内存/注入（F24/F27）、`.gitattributes` 行尾（F25）、Hub 鉴权确认（F26） | 全员 | P2 | 后续迭代 |

---

## ⚠️ 待完善 / 已知局限

- 本审查基于**未提交工作树**（`git diff`），未含已 `git stash` 或分支外内容；若后续还有改动需重新审查。
- F2 中 `BoundedChannel` 背压行为两成员判断**分歧**，结论以实测为准——建议先写最小验证再定方案。
- 测试覆盖评估为**静态对照缺口**，未实际运行测试套件统计行覆盖率（仓库已具备 E2E/集成/单元三层，质量基础好）。
- 架构评估未做运行时 profiling，性能类结论（F4/F11/F23）基于代码路径与既有模式推断。

---

## 📚 数据来源 & 成员产出索引

- **科迪（code-reviewer）原始产出**：29 个生产 `.cs` 文件安全/性能/正确性/可维护性审查，15 项发现（#1-#15），含 2 个修复示例（hub offload、DurationText 前移）。
- **阿奇（architect）原始产出**：DI/服务层/Hub/数据层/客户端/VM 架构影响评估，10 项发现（#1-#10）+ 5 条 ADR（SN 链路、双注册、并发重构、远程门面、连接时序）+ 3 条演进建议。
- **泰莎（testing-expert）原始产出**：14 个改动测试 + 2 个新增测试逐文件评审，12 项覆盖缺口（#1-#12）+ 4 项测试设计问题，对照 29 个生产文件定位盲区。

---

> 本报告由工程保障团队 AI 协作生成，关键决策请由人类工程负责人复核。
