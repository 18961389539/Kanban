# 技术文档债评估报告 — Kanban 工厂生产看板系统

> 评估人：tech-writer（多库/Docu）｜ 评估方式：只读实地核查，所有结论均有文件/代码证据 ｜ 基准日：2026-08-16 重大变更之后

## ① 文档资产盘点表

仓库内实际存在的文档（`.workbuddy/memory/` 与 `.zcode/plans/` 为工具记忆，不计入正式文档资产）：

| 文件 | 主题 | 最后修改 | 新鲜度 | 主要问题 |
|---|---|---|---|---|
| `docs/CodeWiki.md`（53KB） | 全仓库代码维基：架构/模块/类索引/运行方式/CI | 2026-08-14 | ⚠️ 部分过期 | 命名空间、HMAC 密钥回退、激活码格式、EF 迁移范围、HslCommunication.dll 路径、CI 工作流、测试过滤参数共 7 处与代码现实失配（详见②） |
| `docs/架构决策记录.md` | ADR-1~7 架构约束 | 2026-08-03 | ⚠️ 部分过期 | ADR-4/ADR-6 仍写命名空间 `Kanban.Core.*`（已改 `Kanban.Collector.Core.*`）；ADR-2 写"4 个历史库"（现 6 库）；决策本身仍有效，仅事实性引用过期 |
| `docs/授权激活码设计方案.md` | 授权体系设计文档 | 2026-07-26 | ❌ 与实现严重偏离 | 设计写的是 **RSA 签名**方案，实现是 **HMAC 对称密钥**（`ProductKeyCodec`/`HmacValidator`）；设计的激活码格式 4 组 `XXXX-XXXX-XXXX-XXXX` 与实现（5 组→2026-08-16 后 38 字符新格式）完全不同；license.dat 位置写 `C:\ProgramData\Kanban\Config`，实现是 `%APPDATA%\Kanban`。CodeWiki 自己也承认"与实现有出入"。无废弃/ superseded 标注，新人极易误读为现行方案 |
| `docs/真机冒烟清单.md` | 2026-08-13 审查批次的真机验证清单 | 2026-08-13 | ✅ 基本有效 | 引用的 `ci/publish-all.ps1`、`ci/wasm-smoke.js`、服务名 `KanbanCollector`/`KanbanPlcSimulator`、端口 5129/4999 均核实存在；属一次性批次清单，建议归档标注 |
| `docs/MainAPP易用性建议.md` | UX 评审建议 | 2026-08-01 | ✅ 有效（建议类文档，无时效硬依赖） | 无 |
| `MainAPP/README.md` | 主程序构建/启动/数据目录/调试 | 未核实日期 | ⚠️ 部分过期 | ①引用的 `..\README.md`（仓库根 README）**不存在**，死链；②HslCommunication.dll 路径写 `MainAPP\DLLS\`（已迁至 `lib\HslCommunication\`，`MainAPP/DLLS` 目录已不存在）；③仍主动教读者设置 `KANBAN_DATA_DIR`，与 2026-08-16"数据源统一、不再设 KANBAN_DATA_DIR"的现行约定相悖 |
| `MainAPP/手册/MainAPP用户使用手册.md` | 终端用户操作手册 | 未逐一核对 | — | 存在（本轮未逐条核对 UI 论断） |
| `oxyplot/README.md` | vendored 第三方库自带文档 | — | ✅ | 第三方资产，无需维护 |
| `deliverables/engineering-assurance/review-incident-plcsimulator-2026-08-16.md` | 事故复盘 | 2026-08-16 | ✅ | 其中含有**全仓库唯一**正确的测试过滤知识（`--filter-class/--filter-method`，MTP trait 过滤不生效的坑），但这些知识未回流到任何正式文档 |

**README 覆盖验证**：仓库根目录**无 README.md**（Glob 全仓 `**/*.md` 结果确认）；17 个项目中仅 `MainAPP/` 有 README，`Kanban.Collector`、`Kanban.Web`、`Kanban.Client`、`Kanban.Contracts`、`Kanban.Collector.Core`、`PlcSimulator`、`LicenseManager.App`、`LicenseIssuer.*`、4 个测试项目均无 README。

## ② 失配点清单（文档论断 vs 代码现实）

| # | 文档论断（出处） | 代码现实（证据） | 严重度 |
|---|---|---|---|
| 1 | 命名空间为 `Kanban.Core.*`（架构决策记录 ADR-6"命名空间 `Kanban.Core.*`"、ADR-4 证据"`Kanban.Core.Mapping.*Mapper`"；CodeWiki §1.2、§3.3、§7.4） | 实际全部为 `Kanban.Collector.Core.*`——抽查 `Kanban.Collector.Core/` 下 20 个文件（`Data/DatabaseProvider.cs`、`Services/AlarmHistoryStore.cs`、`Localization/ConnectionStatusMessages.cs` 等）namespace 均为 `Kanban.Collector.Core.*` | 高（复制文档中的 using/类引用会直接编译失败） |
| 2 | `KANBAN_HMAC_KEY` "优先，回退内嵌常量（Lazy 解析）"、"否则用内嵌默认密钥"（CodeWiki §3.7.2、§6.7） | **内嵌回退已移除，缺失即抛异常**：`LicenseManager.App/Crypto/EmbeddedKey.cs` `LoadKey()`——env 缺失/非 Base64/非 32 字节均 `throw InvalidOperationException`；`MainAPP.Tests/Unit/LicenseBoundaryTests.cs` 注释"修复 #2：已移除内嵌回退密钥"。文档读者按旧论断部署会**启动即失败且毫无头绪** | 高（与 2026-08-16 破坏性变更直接冲突） |
| 3 | 激活码"载荷 15 字节 = 5B 机器码哈希 + 2B 过期 + 8B HMAC 截断；Base32 → 24 字符 + 1 校验字符，格式 `XXXXX-XXXXX-XXXXX-XXXXX-XXXXX`"（CodeWiki §3.7.2） | **格式已破坏性变更**：`EmbeddedKey.cs` 常量 `PayloadSize=7`、`TagSize=16`（HMAC 截 16 字节非 8）、`TotalSize=23`、`EncodedLength=37`、`FormattedLength=38`。旧 25 字符码全部失效，文档无只字提及重签 | 高（客户交付风险） |
| 4 | "前四库走 EF Migrate + 基线补丁"，`defect_history.db`/`audit_logs.db` 用 "EnsureCreated 建表"（CodeWiki §3.3.2 两处） | **6 库已统一 EF 迁移**：`Kanban.Collector.Core/Data/Migrations/` 下有 6 个子目录（ProductionLogs/AlarmEvents/StatusTransitions/WorkOrders/DefectHistory/Audit），DefectHistory 与 Audit 均有 `2026081619000x_InitialSchema.cs`；`DatabaseProvider.EnsureCreatedAll()` 注释明确"缺陷历史/审计统一走 EF 迁移（收敛双轨制，修复 #15）"，6 个 `MigrateContext(...)` 调用 | 中 |
| 5 | HslCommunication.dll 位于 `MainAPP/DLLS/HslCommunication.dll`（CodeWiki §1.1、§6.1；MainAPP/README.md §构建与启动） | `MainAPP/DLLS` 目录**已不存在**；全部 5 个 csproj（MainAPP/Collector/Collector.Core/PlcSimulator/MainAPP.Tests）HintPath 统一为 `..\lib\HslCommunication\HslCommunication.dll` | 中（新人按文档找文件会扑空） |
| 6 | CI 为 `.github/workflows/` 下 tests.yml / publish.yml / ui-automation.yml 三个工作流（CodeWiki §6.11 整节 + §5.3 目录树） | 仓库**无 `.github/` 目录**（核实不存在）。实际自动化为 `ci/` 本地脚本（publish-all.ps1、run-ui-automation.ps1、wasm-smoke.js 等 19 个脚本） | 中（整节虚构/过期） |
| 7 | 测试过滤用 `dotnet test --filter "Category=Simulation"`，覆盖率用 `--coverlet --coverlet-output-format cobertura`（CodeWiki §6.9） | 测试框架是 xunit.v3 + Microsoft.Testing.Platform（csproj：`xunit.v3.mtp-v2` + `coverlet.MTP`）；事故复盘文档明确记录"**MTP runner 的 trait 过滤不生效是已踩过的坑**，过滤用 `--filter-class/--filter-method`"。文档教的是已知无效参数 | 高（按文档操作必踩坑） |
| 8 | Obfuscar 的 `HideStrings` "保护内嵌 HMAC 密钥"（CodeWiki §3.7.4） | 内嵌密钥已移除；`LicenseManager.App/obfuscar.xml` 注释自述"HMAC 密钥保护依赖环境变量注入（KANBAN_HMAC_KEY），而非混淆" | 低 |
| 9 | MainAPP/README.md 末尾链接"[仓库总览](../README.md)" | 根 README.md 不存在，**死链** | 中 |
| 10 | ADR-2 持久化范围写"devices.json / settings.json 采集子集 / work_orders.db / 4 个历史库" | 现 6 个 SQLite 库（production_logs/alarm_events/status_transitions/work_orders/defect_history/audit_logs），"4 个历史库"口径过期 | 低 |
| 11 | CodeWiki §6.3 / MainAPP/README.md §数据目录：主动教学设置 `$env:KANBAN_DATA_DIR` 做开发/演示 | 代码层面该 env 仍被读取（`LicenseStore.cs`、`HardwareFingerprint.cs`、`PlcSimulator/Program.cs`、多个测试），**不算硬失配**；但现行运维约定（`start_env.ps1` 注释："数据源统一约定：不设置 KANBAN_DATA_DIR，三组件都使用默认 %APPDATA%/Kanban"）与文档导向相反，且该约定只存在于脚本注释，docs/ 无任何体现 | 中（约定与文档导向冲突） |
| 12 | 授权设计方案：license.dat 放 `C:\ProgramData\Kanban\Config\license.dat`（设计方案 §5） | 实现为 `%APPDATA%\Kanban\license.dat`（`LicenseStore.cs` 目录优先级：构造参数 → KANBAN_DATA_DIR → %AppData%\Kanban\） | 低（设计文档，但无过期标注） |

## ③ 缺口清单（按优先级）

| 优先级 | 缺失文档 | 依据与风险 |
|---|---|---|
| **P0** | **部署手册**（含 KANBAN_HMAC_KEY 配置） | HMAC env 缺失即启动失败（EmbeddedKey.cs 抛异常），而密钥生成方法（32 字节 Base64 的 PowerShell 三行）**只存在于 `EmbeddedKey.cs` 源码注释**；Windows 服务安装（`ci/install-collector-service.ps1`、nssm、sc failure 自愈）、`publish-all.ps1` 产物结构、防火墙端口（5129/4999）均无文档 |
| **P0** | **激活码签发/迁移操作手册** | 2026-08-16 激活码格式破坏性变更（25 字符→38 字符，旧码全失效），但**没有任何文档**说明：旧客户如何通知、如何重签（LicenseIssuer.CLI `issue/list/revoke/verify` 用法只在 CodeWiki 两行命令里）、`issued/`/`revoked/` 审计目录约定、换机重激活流程。这是直接对客户交付的阻断性缺口 |
| **P0** | **联调指南** | 联调顺序 PlcSimulator(4999，内部 relay 5000)→Collector(5129)→客户端、"不设 KANBAN_DATA_DIR"统一约定、nssm 服务复活抢端口需管理员 `sc.exe stop`——全部只散落在 `start_env.ps1` 注释和记忆中 |
| **P1** | **测试运行指南** | MTP 的 `--filter-class/--filter-method`（trait 过滤已踩坑无效）、Tests/E2E/UIAutomation 共享 MainAPP obj 缓存**必须串行**、UI 自动化需 WinAppDriver、CI/UI 自动化禁并行——这些关键知识唯一载体是事故复盘文档的一句话 |
| **P1** | **故障恢复 Runbook** | 现实已有丰富的恢复机制但无操作文档：`production_logs.recovery.jsonl` 兜底回放（200MB 上限/30s 退避）、`/healthz` vs `/health/ready` 语义、`/metrics` 扩容判据（ADR-3 提到但无操作指引）、服务崩溃自愈与手动介入边界、配置损坏 `.corrupt` 文件处置 |
| **P1** | **仓库根 README** | 缺失且已被 MainAPP/README.md 引用为死链；新人进仓第一入口不存在 |
| **P2** | **SignalR Hub 协议/API 参考** | 契约（`IKanbanHubServer`/`IKanbanAdminServer`/`IKanbanHubClient` 方法清单、MessagePack/JSON 双协议、Seq/ServerEpoch 补拉语义）只在 CodeWiki 表格里以内部视角描述，无面向集成方的协议文档（错误码、连接纪律 ADR-1/7、重连约定） |
| **P2** | **新手上手指南** | 环境搭建（wasm-tools workload、lib DLL、global.json SDK 固定）、首次联调跑通路径、"有问题找谁"均无；CodeWiki 体量大（53KB）但不替代 onboarding 路径 |
| **P3** | **设计方案文档治理** | `授权激活码设计方案.md`（RSA 方案）与实现（HMAC）南辕北辙却无 superseded 标注；建议加头注指向 CodeWiki §3.7 或重写为"As-Built" |

## ④ 文档体系骨架建议

```
README.md                        ← 新建（P1）：项目是什么/5 分钟快速开始/指向 docs
docs/
├── CodeWiki.md                  ← 保留，修复②中 7 处失配；建议拆出"运行方式"章
├── 架构决策记录.md               ← 保留，补 ADR-8（2026-08-16 批次：HMAC env 强制/激活码格式 v2/6 库迁移统一/命名空间更名/数据源统一），更新 ADR-2/4/6 事实引用
├── deployment/                  ← 新建（P0）
│   ├── 部署手册.md               （KANBAN_HMAC_KEY 生成与注入、Windows 服务安装、publish-all、端口/防火墙、备份 %APPDATA%\Kanban）
│   └── 联调指南.md               （启动顺序、start_env.ps1 用法、数据源统一约定、nssm 坑）
├── operations/                  ← 新建（P0/P1）
│   ├── 激活码签发与迁移手册.md    （新格式说明、旧码重签流程、客户通知模板、revoke 语义）
│   └── 故障恢复Runbook.md        （recovery.jsonl、健康检查语义、metrics 判据、常见故障处置）
├── development/                 ← 新建（P1）
│   ├── 新手上手指南.md
│   └── 测试运行指南.md           （MTP 参数、串行约束、UI 自动化前置——把事故复盘里的知识回流此处）
├── api/
│   └── Hub协议.md               ← 新建（P2）
└── archive/                     ← 真机冒烟清单.md（批次性文档归档）、授权激活码设计方案.md（标注 superseded）
```

**维护责任建议**：CodeWiki/ADR 由架构 owner 随破坏性变更同 PR 更新（本次 7 处失配全是 2026-08-16 变更未同步文档所致，建议把"文档同步"列入破坏性变更的 Definition of Done）；deployment/operations 由 SRE/交付角色维护；testing 指南由测试 owner 维护；每篇文档头部加"最后验证日期 + 验证人"字段。

**一句话结论**：文档资产底子不差（CodeWiki + ADR 质量高），但 2026-08-16 破坏性变更几乎未同步到任何文档（7 处实证失配），且面向交付/运维的 P0 文档（部署手册、激活码迁移手册、联调指南）完全缺失——当前最大的文档债不在"写得不好"，而在"变更后没人更新"和"运维知识只在脚本注释与记忆里"。
