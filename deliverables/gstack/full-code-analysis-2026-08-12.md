# Kanban 工业看板全量代码分析报告

**日期**：2026-08-12
**场景**：全量代码分析（架构/代码质量 + 安全审计 + 测试与发布健康 + 可靠性健康检查，四视角并行）
**参与成员**：🔍 产品官（gstack-product-reviewer）+ 🛡️ 安全官（gstack-security-officer）+ ✅ 质量门神（gstack-qa-lead）+ 🔧 排障手（gstack-investigator）
**分析对象**：当前工作区全量源码（528 个 .cs / 约 33.5K 行，含约 1180 个文件未提交改动；HEAD f2152b7）
**排除范围**：oxyplot/（第三方 vendor）、bin/、obj/、TestResults/、run-demo/、.git/、.workbuddy/

---

## 📌 TL;DR（执行摘要）

- 整体结论：🟡 **有条件通过** —— 代码库工程质量显著高于平均水平（依赖无环、锁纪律严谨、数据正确性工程出色），**产品/架构与可靠性视角无 P0**；但**安全姿态不达上线标准**（Collector Hub 无认证 + 本地授权可绕过），**CI 存在 2 个必失败测试**，上线/合入前必须处理。
- 阻塞项（P0）：4 条 —— ① Collector Hub 无认证（管理写方法任意调用）② 默认口令 + Full 模式自动 admin 登录 ③ 硬编码 HMAC 密钥（激活码可伪造）④ 2 个硬编码 run-demo 路径的测试在 CI 必失败。
- 严重度分布：🔴 P0 ×4 / 🟠 P1 ×9 / 🟡 P2 ×23 / 🟢 P3 ×22。
- 下一步：先修 4 个 P0（Hub 鉴权 → CI 测试治理 → 本地认证收紧 → 密钥管理），再排 P1（背压回归测试、认证链测试、Dispatcher 合并、批量查询上限）。

---

## 🎯 核心结论卡片

| 项目 | 内容 |
|------|------|
| Go / No-Go | 🟡 条件 Go（安全 P0 修复 + CI 测试治理前不发布；本地授权体系建议限期重构） |
| 严重度分布 | 🔴 4 / 🟠 9 / 🟡 23 / 🟢 22 |
| 关键行动项 | 12 条（含 4 条 P0 阻塞项） |
| 建议负责人 | 工程负责人牵头，安全 P0 优先 |

---

## 1. 各成员核心结论

### 🔍 产品官（架构/代码质量）
- 核心判断：**未发现 P0/P1 级确定缺陷**（无必然数据损坏/崩溃路径）；代码库质量显著高于平均水平，设计决策几乎都有动机注释。
- 关键建议：Core 层未真正脱离 WPF（`UseWPF` + `BindingOperations`，锁死 net10.0-windows TFM）；7 个 900+ 行上帝类与测试/生产双构造器分叉；WorkOrderService 18 个 sync/async 三份重复 API 无调用方。

### 🛡️ 安全官（OWASP Top 10 + STRIDE）
- 核心判断：综合评级 **C**（内网信任模型成立时可用，绝不可暴露到不可信网络）；**外部网络面（Collector SignalR）无任何认证**是最大风险。
- 关键建议：Hub 令牌认证 + 管理域角色分离；强制首次改密 + 启动不再自动 admin；KANBAN_HMAC_KEY 缺失拒绝启动；签发改非对称签名；清理已入库激活码。

### ✅ 质量门神（QA 与发布）
- 核心判断：测试资产规模大且结构健康（MainAPP.Tests 1528 用例 + E2E 39 + UI 66 + wasm-smoke），采集/报警/OEE/多语言/许可证覆盖强；**2 个 P0 级 CI 必失败测试**（硬编码 run-demo 路径，run-demo 未入库）+ **认证链零覆盖** + 背压改造无回归测试。
- 关键建议：删除过期诊断测试；补背压语义与认证链测试；UIAutomation 8 个 Skip + WinAppDriver 静默 return 修复假绿。

### 🔧 排障手（健康与可靠性）
- 核心判断：链路工程质量高（重连防风暴、Channel 有界化、恢复文件幂等回放、readiness 探针），**无 P0**；主要风险：WPF Remote 快照无合并（UI 线程压力）、批量查询无上限（无鉴权 Hub 上的资源耗尽向量）、DropOldest 静默丢数据不可观测。
- 关键建议：Dispatcher 节流合并；服务端查询上限；事件丢弃计数进 /metrics + 客户端 Seq 间隙检测补拉。

---

## 2. 综合审查发现（跨成员去重合并，按严重度）

### 🔴 P0（4 条，均阻塞上线）

| # | 类别 | 位置 | 问题 | 建议 | 来源 |
|---|------|------|------|------|------|
| 1 | 安全 | KanbanHub.cs:15、Program.cs:87-95 | **Collector Hub 完全无认证**：SaveDevices/工单 CRUD/SaveCollectorSettings 管理写方法任意调用；监听 0.0.0.0:5129 | Hub 令牌认证（JWT/共享密钥）+ 管理域角色分离；至少内网 ACL 收口 | 安全官 |
| 2 | 安全 | UserStore.cs:212-244、App.xaml.cs:164-192 | **默认口令 + Full 模式启动自动 admin 登录**，本地认证形同虚设；operator 空哈希任意密码 | 强制首次改密；启动不自动登录或降为 Operator | 安全官 |
| 3 | 安全 | EmbeddedKey.cs:38、HmacValidator.cs | **硬编码 HMAC 密钥 + 签发端与客户端共享对称密钥**，持有源码=持有签发端，激活码可无限伪造 | 强制环境变量注入（缺失拒绝启动）；改非对称签名；移除内嵌密钥 | 安全官 |
| 4 | CI | LocalReviewDataDiagTests.cs、TrendPreviewRealDataTests.cs | **2 个硬编码 run-demo 路径的测试在 CI 必失败**（run-demo 未入库，tests.yml 全量跑） | 删除（诊断性质已过期）或 File.Exists 守卫跳过 | QA 门神 |

### 🟠 P1（9 条）

| # | 类别 | 位置 | 问题 | 来源 |
|---|------|------|------|------|
| 5 | 安全 | issued/20260802_124102_STE3WVRO.json | **真实激活码已提交进 git**（含机器码+激活码+到期日）；issued/ 未入 .gitignore | 安全官 |
| 6 | 安全 | TrialTracker.cs、LicenseStore.cs | **试用期与激活锁定可无限重置绕过**（trial.dat 可删/可重签，同一内嵌密钥） | 安全官 |
| 7 | 安全+可靠 | KanbanHub.cs:51-84、HistoryQueryHandler.cs:48-72 | **Hub 无连接/订阅上限 + 批量查询无界**（子查询数、时间范围、行数均不限）→ 远程 DoS 向量（与 F-06/P1-2 合并） | 安全官+排障手 |
| 8 | 安全 | Program.cs:107-108 | **CORS 全放行 + 纯明文 HTTP**：任意网页可连 Hub 读生产数据；内网可嗅探 | 安全官 |
| 9 | 可靠 | MainAPP/Services/RemoteRuntimeSink.cs | **WPF Remote 快照逐条 Dispatcher 投递无合并**：30 设备 ≈150 次/秒 UI 投递，队列无界 | 排障手 |
| 10 | 测试 | EventBroadcasterTests 等 | **背压改造（有界 Channel+DropOldest）无回归测试**：测试仍自建 Unbounded 替身，满时丢最旧/补拉语义零覆盖 | QA 门神 |
| 11 | 测试 | PasswordHasher/UserStore/LoginViewModel | **认证链零测试覆盖**（安全关键路径） | QA 门神 |
| 12 | 测试 | UIAutomation（7 Skip）+ WinAppDriverSmokeTests | **UI 假绿**：异常路径全部 Skip；WinAppDriver 未启用时静默 return 真空通过；workflow 无最低门禁 | QA 门神 |
| 13 | 测试 | LiveCollectorProbeTests | 依赖外部 Collector，CI 必然 Assert.Skip（5 用例永不执行） | QA 门神 |

### 🟡 P2（23 条，精选 14 条）

| # | 类别 | 位置 | 问题 | 来源 |
|---|------|------|------|------|
| 14 | 架构 | Kanban.Collector.Core.csproj:13 | Core 层 `UseWPF=true` + BindingOperations 锁死 net10.0-windows TFM，无头服务被迫加载 WPF 程序集 | 产品官 |
| 15 | 架构 | WorkOrderService.cs:193-369 | 6 个同步 API 包裹 async core（sync-over-async 死锁陷阱），全仓无调用方，18 方法三份重复 | 产品官 |
| 16 | 架构 | MainAPP/ViewModels | 7 个 900+ 行上帝类（Overview 1456/DeviceManager 1231/Home 1090…）；查询页五胞胎（Alarm/Status/Audit/Oee/Production Query）无共享基类 | 产品官 |
| 17 | 正确性 | PlcDataAcquisitionService.cs:70 | `_lastDefectSnapshot` 字典在 RemoveDeviceState 不清理：删除重建同 Id 缺陷后首条快照被去重吞掉（少写历史）；长期增删设备内存缓涨（与排障手 P2-12 合并） | 产品官+排障手 |
| 18 | 可靠 | EventBroadcaster.cs（bounded 4096） | **DropOldest 静默丢事件**：无计数/无日志/客户端无 Seq 间隙检测，报警语义敏感 | 排障手 |
| 19 | 可靠 | PlcScanPipeline.cs、HslNetworkPlcDriver.cs | **PLC 半连接时单轮采集最长可达 地址数×5s**，断连判定推迟数分钟（150+ 读×5s≈12min） | 排障手 |
| 20 | 可靠 | ProductionHistoryWriter.cs | 通道满时在 200ms 轮询线程同步写恢复文件 + 回放每 5s 全文件 ReadAllLines（200MB 上限内仍 IO 抖动） | 排障手 |
| 21 | 审计 | KanbanHub.cs、PlcDataAcquisitionService | **审计双库分裂**：Remote 操作只进 Collector 审计库本地看不到；产量清零/班次切换无审计；WASM 无埋点 | 排障手 |
| 22 | 可观测 | CollectorMetrics.cs | /metrics 缺重连计数、channel 丢弃计数、订阅者/积压 gauge、批写失败计数 | 排障手 |
| 23 | 安全 | PasswordHasher.cs:11-26 | 密码单轮 SHA256+salt（非慢哈希），users.json 明文落盘 | 安全官 |
| 24 | 安全 | CsvUtil.cs:14-20 | **CSV 公式注入**（=cmd 前缀未处理），与 F-01 叠加可 RCE 化 | 安全官 |
| 25 | 安全 | DatabaseProvider.cs:25-33 | SQLite 数据/审计文件默认 ACL，同机任意用户可读全部生产历史与审计 | 安全官 |
| 26 | 测试 | WpfUiTestCollection.cs:3 | 注释称 parallelizeTestCollections=false，实际配置=true（注释漂移，误导回改） | QA 门神 |
| 27 | 测试 | 47 个测试文件 | DateTime.Now/UtcNow 时间依赖，午夜边界/慢机 flaky（ShiftConfig 29 例） | QA 门神 |

### 🟢 P3（22 条，精选 10 条）

| # | 类别 | 位置 | 问题 | 来源 |
|---|------|------|------|------|
| 28 | 架构 | Kanban.Collector.Core InternalsVisibleTo MainAPP | 跨程序集 internal 耦合（注释标"过渡期"），需设删除期限 | 产品官 |
| 29 | 正确性 | UserManagerViewModel.cs:101 | CreatedAt 用 UtcNow，全仓其他时间用 Now——同一库两种时钟源 | 产品官 |
| 30 | 性能 | PlcDataAcquisitionService.cs | 200ms 轮询每轮 4-5 次 GetDevicesSnapshot 全量拷贝 + 每轮重复 Parse 地址 | 产品官 |
| 31 | 卫生 | SettingsViewModel.cs:731 等 2 处 | async void（有 try/catch 兜底，风险受控） | 产品官 |
| 32 | 可靠 | PlcScanPipeline.cs:120-131 | 批读失败 Warning 每轮刷屏（200ms 一条日志风暴） | 排障手 |
| 33 | 配置 | AppSettings.cs:453 | PollingIntervalMs 允许配置到 10ms，误配导致 CPU/PLC 风暴 | 排障手 |
| 34 | 安全 | Program.cs:30、App.xaml.cs:28 | 固定命名 Mutex 可被本地抢占（本地 DoS，接受风险） | 安全官 |
| 35 | 安全 | LicenseInfo.cs:28 | 激活后过期校验依赖本地时钟，无回拨检测（离线授权通病） | 安全官 |
| 36 | 测试 | Benchmarks continue-on-error | 性能失败不阻塞且无基线对比 | QA 门神 |
| 37 | 测试 | wasm-smoke.js | tests.yml paths 未含 ci/** → 改冒烟脚本不触发测试 | QA 门神 |

---

## 3. 跨成员协同确认点

- **批量查询无上限（P1-7）**：安全官与排障手独立发现同一向量，确认与 Hub 无鉴权叠加为远程 DoS——修复应同时加"鉴权 + 查询上限"。
- **_lastDefectSnapshot 泄漏（P2-17）**：产品官与排障手独立发现，确认存在删除重建缺陷后少写历史的正确性影响。
- **审计缺失（P2-21）**：排障手发现产量清零/班次切换无审计，与项目已知"审计待加强"项一致，建议纳入下批。

---

## ✅ 行动清单

| # | 行动 | 负责方 | 紧急度 | 期望完成 |
|---|------|--------|--------|---------|
| 1 | Collector Hub 增加令牌认证 + 管理域角色分离（或先内网 ACL 收口） | 工程负责人 | P0 | 合入前 |
| 2 | 删除/守卫 2 个 run-demo 硬编码测试（LocalReviewDataDiagTests / TrendPreviewRealDataTests），解除 CI 红 | 工程负责人 | P0 | 本周 |
| 3 | 本地认证收紧：强制首次改密、启动不自动 admin、去掉免密账号 | 工程负责人 | P0 | 2 周内 |
| 4 | 密钥治理：KANBAN_HMAC_KEY 缺失拒绝启动；issued/ 入 .gitignore 并清历史 | 工程负责人 | P0 | 2 周内 |
| 5 | 补背压回归测试（EventBroadcaster/SnapshotAggregator/MetaPublisher 的 DropOldest+补拉语义） | QA | P1 | 1 周内 |
| 6 | 补认证链测试（PasswordHasher/UserStore/LoginViewModel） | QA | P1 | 2 周内 |
| 7 | RemoteRuntimeSink Dispatcher 节流合并（500ms~1s 批次） | WPF 负责人 | P1 | 2 周内 |
| 8 | HistoryQueryHandler 服务端上限：子查询 ≤32、时间范围 ≤7 天、行数预算 | Collector 负责人 | P1 | 2 周内 |
| 9 | 事件丢弃计数进 /metrics + 客户端 Seq 间隙检测补拉 | Collector+WPF | P2 | 下月 |
| 10 | PlcScanPipeline 半连接提前判断 + 轮询时间预算 | Collector 负责人 | P2 | 下月 |
| 11 | 审计补齐：产量清零/班次切换埋点 + Remote 审计可见性 | 工程负责人 | P2 | 下月 |
| 12 | Core 层去 WPF 化评估（ObservableDeviceCollection 下沉） | 架构负责人 | P2 | 规划 |

---

## ⚠️ 待完善 / 已知局限

- 本次为**静态只读分析**，未全量运行测试（Debug 产物当日已清理）；P0-4 的 CI 必失败结论基于路径硬编码 + run-demo 未入库的事实推断，未在 CI 上实跑复现。
- oxyplot vendor 源码排除在外（仅提示直接源码引用的升级制约）。
- 工作区存在约 1180 个文件未提交改动，分析以当前工作区为准；部分结论（如产品官对未提交改动的评价）随提交可能变化。
- WASM 端（Kanban.Web）分析深度弱于 WPF 端（无独立专家视角聚焦 Blazor 渲染/内存），建议后续补一轮前端专项。

---

## 📚 成员产出索引

- gstack-product-reviewer（产品官）原始产出：17 条发现（P0 0 / P1 0 / P2 6 / P3 11）+ 5 项加分实践
- gstack-security-officer（安全官）原始产出：15 条发现（P0 3 / P1 4 / P2 5 / P3 3）+ 3 项安全实践
- gstack-qa-lead（质量门神）原始产出：14 条发现（P0 2 / P1 5 / P2 4 / P3 3）+ 补测 Top 5 + 测试资产总览
- gstack-investigator（排障手）原始产出：15 条发现（P0 0 / P1 2 / P2 9 / P3 4）+ 4 项可靠性实践

---

> 本报告由软件工坊 AI 协作生成，关键决策请由工程负责人复核。
