# Kanban 看板系统事故响应就绪评估 & 桌面事故演练报告

**评估人**：雷克斯（Rex）· SRE 工程师
**日期**：2026-08-16
**范围**：D:\code\Kanban\Kanban（.NET 10，PlcSimulator → Collector → WPF/Blazor 客户端），只读评估，未修改任何文件。

---

# 第一部分：事故响应就绪评估

## 1. 健康检查覆盖度

**现状证据：**
- 三个探针分层清晰：`/healthz` 与 `/health/live` 为纯存活探针（`Predicate = _ => false`，恒 Healthy），`/health/ready` 走业务级 readiness（Kanban.Collector/Program.cs:145-162）。
- readiness 覆盖：初始化状态、采集循环活性（3 分钟判活窗口）、连续失败 ≥10 轮判 Degraded、**生产库可写性探测（PRAGMA quick_check + BEGIN IMMEDIATE 写锁回滚）**、恢复文件积压（>200MB 判 Degraded）、审计丢弃（CollectorReadinessCheck.cs:52-113；DatabaseProvider.cs:375-406）。

**缺口：**
1. **quick_check 只探测 production_logs.db 一个库**（DatabaseProvider.cs:380），其余 5 库（alarm_events / status_transitions / work_orders / defect_history / audit_logs）损坏时 readiness 不会发现——这 5 库任何一个 malformed 都会在启动迁移阶段直接炸掉进程（见第 7 项），或运行期首次读写时才暴露。
2. **PLC 连接状态不在探针内**：PLC 断线只体现为"采集新鲜度"间接信号（3 分钟窗口），断线 30s~3min 内探针仍 Healthy。对车间场景可接受，但需知晓语义。
3. **致命短板：没有任何东西在轮询这些探针**。车间局域网无监控 Agent、无告警通道，/health/ready 设计得再好，进程死了就是 5129 端口无人应答——发现故障完全依赖操作工抬头看大屏。这是 MTTD 的最大来源。**严重度：高**。
4. 部署脚本的验收命令用的是 `/healthz`（ci/install-sim-services.ps1:96 "curl http://127.0.0.1:5129/healthz 应为 Healthy"）——纯存活语义，采集全死也返回 Healthy，给运维错误的"健康"错觉。**严重度：中**。

**严重度总评：高**（探针本身设计良好，但"无人看"使其在真实事故中不发挥作用）。

## 2. /metrics 指标充分性

**现状证据：**
- 9 个 Prometheus 风格计数器：快照/元数据/报警/状态发布数、采集周期数、订阅者峰值 ×3、发布错误计数（CollectorMetrics.cs:14-26, 41-58）。无锁 Interlocked 实现，高频路径安全。

**缺口：**
1. **全是累计计数器与峰值，无瞬态 gauge**：看不到"当前 PLC 是否连接""当前订阅者数""恢复文件当前字节数""各 DB 文件大小"。计数器单调递增，Collector 进程僵死时 `/metrics` 直接无响应，值本身不带状态。
2. 无直方图/延迟指标：采集周期 200ms 的实际耗时、SignalR 推送延迟不可观测，性能劣化类事故（大屏卡顿但进程活着）无从定位。
3. `kanban_publish_error_total` 只有总数，无错误类型维度。
4. 无外部抓取方（同第 1 项），指标只是"事故现场可查"，不能"事前预警"。**严重度：中**。

**严重度总评：中**。

## 3. 日志（分级/落盘/轮转）

**现状证据：**
- Serilog 异步落盘：`%KANBAN_DATA_DIR%/Config/Logs/collector_.log`，按天滚动、**保留 14 份**、单文件 20MB 上限（Program.cs:42-50）。
- 分级使用规范：PLC 重连 Warning、初始化失败 Critical、进程退出 Fatal（CollectorWorker.cs:80；Program.cs:186）。
- PlcSimulator 经 NSSM 包装有独立 stdout/stderr 日志（install-sim-services.ps1:82-83）。

**缺口：**
1. **20MB 单文件上限 + 按天滚动存在截断风险**：`fileSizeLimitBytes` 打满后当天后续日志被丢弃（Serilog 默认行为），而事故现场恰恰日志爆量。建议改为"打满即滚动下一份"或取消大小上限。
2. 日志只在故障机本地，无集中收集；主机断电场景下日志本身也可能随磁盘问题丢失（本次演练场景未发生，但属风险）。
3. 无 Windows 事件日志写入——服务崩溃循环时 SCM 事件在系统事件日志里有记录，但应用层 "为什么起不来" 只在自己的文件里，IT 第一反应看事件查看器会看不到根因。**严重度：低-中**。

**严重度总评：低**（基本可用，是本次评估中相对最成熟的一项）。

## 4. 备份策略

**现状证据：**
- 唯一的备份机制是 **MigrateContext 迁移/legacy patch 前的 `BackupDatabase`**：生成 `{db}.pre-migration-{yyyyMMddHHmmssfff}.bak`（SQLite 在线备份 API，DatabaseProvider.cs:110, 130, 143, 151-160）。
- 配置文件（settings/devices/recipes/users/baselines.json）有原子写 + 单代 `.bak`（AppSettings.cs:226-274）。
- 生产恢复文件（recovery file）是**写缓冲兜底**而非备份：通道满/落库失败时转存本地、回放幂等（EventId 唯一索引去重）、200MB 上限（ProductionHistoryWriter.cs:16-23, 180-300）。

**缺口（本评估最严重的缺口区）：**
1. **无任何定时备份**。备份只在"有迁移要跑"时产生——即备份频率 = 发版频率。今天（2026-08-16）恰逢 6 库迁移统一跑过一次，所以今天的事故能捡回今晨的备份；**若事故发生在两次发版之间，RPO = 上次发版日期，可能是几周**。车间产量数据（85611 行）按这个频率丢失是不可接受的。**严重度：高**。
2. **备份与主库同机同盘同目录**。主机断电/磁盘故障场景（本次演练场景）备份与主库一起处于风险中。无异地/异机副本。**严重度：高**。
3. `.pre-migration-*.bak` 无保留策略、无清理，随发版次数无限累积；反之若有人手工清盘，备份又被一锅端。无清单化管理。
4. 备份有效性从未被验证过（无恢复演练、无 quick_check 校验备份文件本身）。对损坏主库做迁移前备份会把损坏一起拷走（BackupDatabase 对 malformed 源会抛异常，见第 7 项）。**严重度：中**。
5. 恢复文件只覆盖生产库（production_logs）的"未落库窗口"，报警/状态/缺陷/审计四库无对应兜底（它们走边沿事件直写）。**严重度：中**。

**严重度总评：高**。

## 5. 故障自愈（重连/重试边界）

**现状证据：**
- PLC 断连：1s→30s 指数退避、**无限次重试**、连接配置每次重试前重新应用（PlcConnectionManager.cs:102-115, 145-182）。3 分钟无成功采集 → readiness 翻 Unhealthy（CollectorReadinessCheck.cs:21, 72-76）。边界清晰，不会静默卡死。
- SignalR 客户端：`WithAutomaticReconnect(1s/5s/15s/30s)`（KanbanDataClient.cs:103）；报警补拉游标带 ServerEpoch，Collector 重启后客户端识别纪元变化、归零游标靠快照重建（RemoteRuntimeSink.cs:366-402；EventBroadcaster.cs:34）。
- 快照发布单轮异常隔离，不再"进程活着采集已死"（CollectorWorker.cs:52-56）。
- WAL 启动期强制 `wal_checkpoint(TRUNCATE)`，从"上次强杀导致 WAL 满、写入静默失败"状态自愈（DatabaseProvider.cs:320-345）；运行期每 5 分钟 PASSIVE checkpoint。
- 单实例互斥防双写 SQLite，崩溃遗留 abandoned mutex 可接管（Program.cs:26-40, 208-227）。

**缺口：**
1. **WPF 大屏运行中断线耗尽重连后无人兜底**：`WithAutomaticReconnect` 四次退避（约 1 分钟内）耗尽后触发 `Closed`，处理器明确注释"不再自行循环重连——等待上层重试策略"（KanbanDataClient.cs:139-145），但 MainAPP 上层唯一的 5s 重连循环**只覆盖"从未连上过"的启动期场景**（ApplicationStartupCoordinator.cs:283-285）。**Collector 停机超过约 1 分钟 → 大屏永久黑屏，必须人工重启 MainAPP**。这是本次演练时间线里的真实杀伤点。**严重度：高**。
2. Blazor Web 端首连失败有 5s 重试循环（DashboardState.cs:293-360），但运行中 `Closed` 后同样未见挂接 ScheduleRetry 的证据（仅挂了 Reconnecting/Reconnected，DashboardState.cs:63-70），需实测验证；保守按"同样需人工刷新"对待。
3. PLC 无限重试无升级机制：连 3 分钟翻 Unhealthy、连 1 小时还是 Unhealthy，无人升级通知。**严重度：中**（与第 1 项监控缺口叠加）。
4. SCM 崩溃重启策略仅 3 次（restart/5000/10000/30000，install-sim-services.ps1:59），崩溃循环 45 秒后服务永久停止——对"启动必崩"类故障（DB 损坏、漏配 env）等于没有自愈。**严重度：中**（设计上合理，避免无限 crash loop，但需与告警联动才有意义）。

**严重度总评：高**（WPF 大屏断连兜底缺失 + 监控缺位）。

## 6. 配置变更安全（热切换）

**现状证据：**
- Remote 采集设置同步是**教科书级的事务式更新**：草稿实例合并 → `ShiftValidator` + `draft.Validate()` 完整校验 → **先原子落盘 → 后应用运行实例**；落盘失败运行实例保持原状，校验失败抛回客户端（ConfigSyncHandler.cs:226-345；AppSettings.cs:300-357）。
- 落盘原子性：临时文件 + File.Move + 单代 .bak；备份失败仅告警不阻断（AppSettings.cs:226-274，含备份失败曾致 MainAPP 崩溃的回归修复注释 :248-255）。
- 设备/配方整体替换同样先校验后落盘（ConfigSyncHandler.cs:363-388）。

**缺口：**
1. 顺序是"先落盘后生效"，**应用阶段若部分字段应用失败**（:332 有异常过滤），会出现"磁盘新/内存旧"分裂，重启后才一致。窗口小、影响低，但应在失败时显式告警提示"重启生效"。**严重度：低**。
2. PLC 配置热改后由下次重连周期生效（PlcConnectionManager.cs:163），改错 IP/端口时无"试连接验证"步骤，错误配置直接生效，表现为采集静默失败直到 3 分钟判活窗口触发。**严重度：中**。
3. `.bak` 只有单代——连续两次误改后旧配置不可回滚。**严重度：低**。

**严重度总评：低-中**（热切换路径是本系统可靠性设计的亮点）。

## 7. 启动失败模式

**现状证据：**
- 初始化失败 → `MarkFailed` + **重新抛出**让宿主拉起，杜绝假健康（CollectorWorker.cs:75-82）；进程级 `Log.Fatal` 记录（Program.cs:184-188）。
- DB 损坏：启动期 `EnsureCreatedAll` → 打开 malformed 库抛 SqliteException → 初始化失败 → 进程退出 → SCM 3 次重启同样失败 → 服务停止（链路证据：DatabaseProvider.cs:97-104 + CollectorWorker.cs:79-81 + install-sim-services.ps1:59）。
- 端口占用：Kestrel 绑定 0.0.0.0:5129 失败抛异常 → 同样的 Fatal 退出路径。单实例互斥只防自家双开，防不了别的程序占端口（Program.cs:35-40, 66-70）。
- 团队已有同类事故的复盘沉淀：nssm 复活残留 PlcSimulator 抢 4999 端口 + 单实例互斥使新实例静默退出 → Collector 连到脏实例（deliverables/engineering-assurance/review-incident-plcsimulator-2026-08-16.md:83）。

**缺口：**
1. **KANBAN_HMAC_KEY（今日新变更）是新的启动炸弹**：`EmbeddedKey.LoadKey()` 在 env 缺失/非法/长度错误时抛 `InvalidOperationException`（LicenseManager.App/Crypto/EmbeddedKey.cs:32-59）。命中路径在 MainAPP 启动授权检查（App.xaml.cs:104 → LicenseGate.CheckStatus:73 → LicenseStore.LoadTrial→HmacValidator.ComputeTag→EmbeddedKey.HmacKey），`LicenseGate.CheckStatus` 全程无 try/catch（LicenseGate.cs:62-106）→ **大屏端 MainAPP 启动即崩**。而部署脚本 `install-sim-services.ps1` 只给服务注入了 `KANBAN_DATA_DIR`，**从未注入 `KANBAN_HMAC_KEY`**（:56-58）——按此脚本新装/重装的环境今天起必现启动失败。 Collector 进程本身不引用 EmbeddedKey（grep 全仓确认），但"大屏起不来"对车间等于系统不可用。**严重度：高（今日回归风险）**。
2. **DB 损坏无任何自动 salvage 路径**：启动迁移对 malformed 库没有"尝试 .recover / 自动还原最近 .bak / 降级只读"任何分支，一律崩。**严重度：高**（这是本次演练的核心）。
3. malformed 库上的 `BackupDatabase` 会抛异常（源库不可读），即"连损坏前的备份都来不及做"——当前备份点永远早于损坏点一个发版周期，见第 4 项。
4. 端口占用失败只在一行 Fatal 日志里，对现场运维不友好（建议启动失败时写 Windows 事件日志）。**严重度：低**。

**严重度总评：高**。

## 就绪评估结论

| 维度 | 评分 | 一句话结论 |
|---|---|---|
| 健康检查 | ★★★☆ | 探针设计优秀，无人轮询等于没有 |
| 指标 | ★★☆☆ | 计数器可用，缺状态 gauge 与抓取方 |
| 日志 | ★★★★ | 最成熟，注意 20MB 截断 |
| 备份 | ★☆☆☆ | 只有发版触发备份，同盘存放，RPO 不可控 |
| 故障自愈 | ★★★☆ | PLC/SignalR 退避良好，WPF 大屏 Closed 后无兜底 |
| 配置变更 | ★★★★ | 校验→落盘→生效事务式，亮点 |
| 启动失败 | ★★☆☆ | 失败即崩是对的，但 DB 损坏无 salvage、HMAC env 部署未覆盖 |

---

# 第二部分：桌面事故演练记录

**演练场景**：车间 Collector 主机异常断电重启 → production_logs.db 损坏（malformed）→ Collector 启动失败 → 大屏与 Web 端无数据，正值早班生产高峰。

## 1. 分诊

**SEV 评级：SEV2（主要功能降级）**
评级理由：
- 实时可视化与历史记录全面中断、全部用户（车间大屏 + Web 端 + 管理端）受影响——够 SEV1 的"全用户"标准；
- 但产线本身在跑、PLC 未失控，无安全/质量直接风险，且断电期间 Collector 本就无采集，数据损失窗口可控（有恢复手段）——不满足 SEV1"服务宕机且不可恢复"的紧迫性；
- 早班高峰 + 生产管理主系统不可用，15 分钟内必须全员响应。

**影响范围**：车间全部大屏（WPF MainAPP Remote 模式）、Web 看板（Blazor WASM）、历史查询/报表；PLC 采集与历史落库中断（断电起即无采集）；报警边沿事件中断。

**角色分配**：
- **事故指挥官（IC）**：IT 值班工程师——定级、拍板恢复方案（还原 vs 重建）、控制节奏；
- **响应者**：IT 系统管理员（执行主机/服务/文件操作）+ 一名熟悉系统的开发（远程支持 .recover 抢救与数据校验）；
- **沟通负责人**：生产计划员/班组长——向车间主任同步，管理现场预期（纸质/白板临时记录产量）；
- **记录员**：由沟通负责人兼任，记录时间线（事后无责复盘输入）。

## 2. 模拟时间线

| 时刻 | 事件 |
|---|---|
| 08:05 | 早班高峰。车间配电跳闸，Collector 主机异常断电 |
| 08:07 | 主机上电；Windows 服务 KanbanCollector 自动启动（start=auto） |
| 08:07:10 | 启动初始化：`EnsureCreatedAll` 打开 production_logs.db → SqliteException "database disk image is malformed"（DatabaseProvider.cs:97-104 路径）→ CollectorWorker MarkFailed + rethrow → 进程退出（日志 `Config/Logs/collector_20260816.log` 记录 Critical + Fatal） |
| 08:07~08:08:30 | SCM 崩溃重启策略执行 restart/5s→10s→30s 三次，均同样崩于迁移阶段 → **服务彻底停止**（install-sim-services.ps1:59，reset=86400 内不再拉起） |
| 08:07~08:09 | 大屏 SignalR 断连 → WithAutomaticReconnect 1/5/15/30s 四次退避耗尽 → **Closed，无上层兜底**（KanbanDataClient.cs:139-145 + ApplicationStartupCoordinator.cs:283-285）→ 大屏定格黑屏/离线徽标 |
| 08:15 | 操作工发现大屏无数据 → 班组长 → 电话 IT（**发现手段=人眼**，MTTD≈10 分钟，/health/ready 无人轮询此时端口已无人应答） |
| 08:20 | IT 值班工程师接手，宣布 SEV2，建临时沟通群（战情室） |
| 08:30 | IT 远程登录主机：`sc query KanbanCollector` = STOPPED；查日志确认 "malformed"；`sc start` 手动试一次复现崩溃，确认非偶发 |
| 08:35 | 检查 Config 目录：**发现今晨 6 库迁移统一留下的 `production_logs.db.pre-migration-20260816HHMMSSfff.bak`**（DatabaseProvider.cs:130, 153）→ 确认 RPO = 今晨备份点，早班 08:00 后产量数据在损失窗口内 → IC 拍板：先还原 .bak 恢复服务，损坏库留存待抢救 |
| 08:42 | 执行还原（详见 Runbook 缺口清单一节的步骤）：损坏 db + `-wal` + `-shm` 改名隔离 → 复制 .bak 为 production_logs.db → `sc start KanbanCollector` |
| 08:44 | 服务启动：恢复的 .bak 为迁移前状态 → 检测到 pending migrations → 自动再备份 → Migrate 成功 → readiness quick_check 通过 |
| 08:45 | 验证：`curl http://127.0.0.1:5129/health/ready` = Healthy（注意：**不能用 /healthz**，它恒 200）；抽查 ProductionLogs 行数与时间戳连续性 |
| 08:47 | Web 端重连恢复（若首连重试循环仍在）；**WPF 大屏需逐台人工重启 MainAPP**（Closed 无兜底）——沟通负责人通知各工位 |
| 08:50 | 大屏全部恢复，采集恢复，报警状态经 ServerEpoch 变化后客户端游标归零、快照重建（RemoteRuntimeSink.cs:366-402） |
| 09:10 | 开发远程用 `sqlite3 production_logs.db.corrupt .recover` 抢救今晨窗口数据，按 EventId 幂等补录（唯一索引去重，DatabaseProvider.cs:167-168） |
| 09:30 | 恢复通告；损失确认：08:05 断电后无采集属"无数据产生"而非丢失，实际损失 = 今晨备份点~08:05 已落库但随损坏库不可读的部分（.recover 抢救后基本归零） |
| 09:35 | 恢复确认，降级为观察；约定 48h 内无责复盘 |

**总计：MTTD ≈10min，MTTR ≈45min（恢复服务），完全收尾 ≈90min。若 .bak 不存在（非发版日场景），MTTR 将恶化至"重建空库 + 数据永久丢失"。**

## 3. 沟通模板

**① 开工通报（08:20，面向车间主任 + IT 群）**
> 【故障通报 SEV2】08:05 起车间看板系统中断：采集服务主机断电重启后数据库损坏，服务无法启动，大屏与 Web 看板均无数据。产线本身不受影响，PLC 设备正常。IT 已按最高优先级处理，预计 1 小时内恢复。期间请各工位用白板/纸质临时记录产量，恢复后补录核对。下一次进展更新 08:50。——IT 值班（IC）

**② 进展更新（08:50，同群）**
> 【进展更新 SEV2】已定位：主机断电导致生产历史数据库文件损坏。已用今日凌晨系统自动备份还原，采集服务 08:44 恢复运行，健康检查通过。Web 看板已自动恢复；各工位大屏需重启看板程序（操作：退出后双击桌面"生产看板"图标），请班组长协助逐台确认。数据损失评估中，预计 09:10 前出恢复通告。——IT 值班（IC）

**③ 恢复通告（09:35，面向车间主任 + IT + 生产经理）**
> 【恢复通告】看板系统已于 08:50 全面恢复，总中断 45 分钟。产量数据经损坏库抢救后基本无丢失（08:05-08:44 断电/停机期间无生产数据采集，不计丢失；个别工位手工记录请今日下班前与系统核对）。根因初步认定为异常断电导致数据库文件损坏，48 小时内完成无责复盘并输出预防措施（定时备份、断电保护、自动告警）。对早班造成的不便致歉。——IT 值班（IC）

## 4. 根因 5 Why

1. **为什么大屏/Web 无数据？** → Collector 进程崩溃重启 3 次后服务停止，5129 端口无人应答；大屏 SignalR 重连耗尽后永久断开。
2. **为什么 Collector 起不来？** → 启动迁移阶段打开 production_logs.db 即抛 "malformed"，初始化失败按设计 rethrow 退出（杜绝假健康的设计在此场景反而表现为"快速失败但无人修复"）。
3. **为什么数据库损坏？** → 主机异常断电，SQLite 主库页写入/checkpoint 被中断（synchronous=NORMAL 保 WAL 提交记录，但无法防止断电瞬间主库页部分写入造成页损坏）。
4. **为什么一次断电就演变成 45 分钟全厂停看？** → 三个叠加缺口：① 损坏后无任何自动 salvage/自动还原路径，只能人工；② 恢复手段（.bak）只在发版迁移时产生且同盘存放，无 Runbook 指引，恢复速度取决于当事 IT 是否知道 .bak 存在；③ 无监控告警，发现靠人眼，服务停了 8 分钟才有人报修。
5. **为什么这些缺口存在？** → 系统韧性设计聚焦"运行期自愈"（重连/恢复文件/原子写），但"灾难恢复"维度（定时备份、异地副本、损坏 salvage、监控告警、恢复 Runbook）未纳入工程要求——本次今日变更（HMAC env 化、6 库迁移统一）也未同步更新部署脚本与运维文档。

**根因**：缺少面向灾难恢复的运维体系（备份策略、监控告警、Runbook），而非单点技术缺陷。

## 5. 行动项

| # | 行动项 | 负责人角色 | 期限 |
|---|---|---|---|
| 1 | 实现 SQLite 定时备份：每日（或每 4 小时）对 6 库执行 SQLite 在线备份（复用 BackupDatabase 模式），保留最近 7 代 + 每周 1 代；写入 Windows 计划任务或 HistoryRetentionService 同款 BackgroundService | 后端开发 | 1 周 |
| 2 | 备份异地化：备份完成后复制到车间另一台主机/文件服务器（robocopy 计划任务即可），与主库异机异盘 | IT 管理员 | 1 周 |
| 3 | 部署脚本补注入 `KANBAN_HMAC_KEY`（install-sim-services.ps1 当前只注入 KANBAN_DATA_DIR，:56-58），并在脚本验收步骤改用 `/health/ready` 而非 `/healthz` | DevOps/IT | 3 天（今日回归风险） |
| 4 | WPF MainAPP 增加运行中 Closed 兜底：订阅 KanbanDataClient.Closed 复用 5s 重连循环（对齐启动期 StartRemoteRetryLoop） | 客户端开发 | 2 周 |
| 5 | 轻量监控告警：任一台常开机器上 5s 轮询 `/health/ready` + 进程存活，连续 3 次失败发企业微信/邮件/声光告警 | IT 管理员 | 2 周 |
| 6 | 编写并演练《SQLite 损坏恢复 Runbook》（内容见下节），纳入值班文档，每季度桌面演练一次 | SRE（我） | 1 周编写，本月内首演 |
| 7 | 启动期 DB 损坏 salvage 分支：检测到 malformed 时自动尝试最近 .bak 还原（保留损坏副本），失败才退出 | 后端开发 | 1 个月 |
| 8 | 车间 Collector 主机增配 UPS 或至少开启来电自启 + 断电告警；评估 synchronous=FULL 对写性能的影响 | IT + 设备科 | 1 个月 |
| 9 | Serilog 单文件 20MB 上限改为打满滚动新文件（rollOnFileSizeLimit），避免事故爆量丢日志 | 后端开发 | 2 周 |
| 10 | readiness 探针 quick_check 扩展到全部 6 库 | 后端开发 | 2 周 |

## 6. 预防措施与 Runbook 缺口：《SQLite 损坏恢复 Runbook》应包含的确切步骤

基于代码现实（文件位置、备份命名、迁移行为均有上文行号证据），Runbook 应包含：

**前置信息**
- 数据目录：`%APPDATA%\Kanban\Config\`（默认）或 `%KANBAN_DATA_DIR%\Config\`（服务部署时被 install-sim-services.ps1 指向 `run-demo\data\Config`，**必须先 `sc qc KanbanCollector` / 查注册表 `HKLM:\SYSTEM\CurrentControlSet\Services\KanbanCollector\Environment` 确认实际 DataRoot**）。
- 6 个库文件：production_logs.db / alarm_events.db / status_transitions.db / work_orders.db / defect_history.db / audit_logs.db（DatabaseProvider.cs:27-35），每个可能伴随 `-wal` / `-shm` 文件。
- 日志目录：`Config\Logs\collector_YYYYMMDD.log`（确认 "malformed" 字样定位到具体哪个库）。
- 备份文件命名：`{库名}.pre-migration-{yyyyMMddHHmmssfff}.bak`，与库同目录（DatabaseProvider.cs:153）。

**恢复步骤（以 production_logs.db 为例）**
1. `sc stop KanbanCollector`；确认进程退出（单实例互斥存在，残留进程会阻止新实例启动，Program.cs:35-40）。
2. 用 `sqlite3 production_logs.db "PRAGMA quick_check;"` 确认损坏（若无 sqlite3 客户端，以日志报错为准）。
3. **隔离不删除**：`ren production_logs.db production_logs.db.corrupt-20260816`，同时改名 `-wal` / `-shm` 同名文件（残留的 WAL 若配对新还原的 .bak 会造成二次损坏/数据错乱——必须一起隔离）。
4. 选最新备份：按时间戳取最新 `production_logs.db.pre-migration-*.bak`，先对它跑 `PRAGMA quick_check` 验证备份本身完好，再 `copy` 为 `production_logs.db`。
5. `sc start KanbanCollector`；观察日志确认：检测到 pending migrations → 自动再备份 → Migrate 成功（.bak 是迁移前状态，属预期路径，DatabaseProvider.cs:128-131）。
6. 验证：`curl http://<host>:5129/health/ready`（**必须 /health/ready，/healthz 恒 200 无意义**，Program.cs:145-148）；核对 ProductionLogs 最大时间戳与行数（基线 85611 行量级）。
7. 客户端恢复：Web 端刷新页面；**WPF 大屏逐台重启 MainAPP**（当前版本 Closed 后无自动重连）。若大屏 MainAPP 启动即崩，检查 `KANBAN_HMAC_KEY` 环境变量（今日新变更新增的启动前置条件）。
8. 数据抢救（可选，开发协助）：`sqlite3 production_logs.db.corrupt-20260816 .recover` 导出可抢救行，按 EventId 幂等补录（唯一索引去重，重复插入自动忽略）。
9. 无 .bak 可用的降级路径：移走损坏库后直接启动——`MigrateContext` 对不存在的新库会 `Migrate()` 重建空库（DatabaseProvider.cs:97-101），**服务可用但历史数据全部丢失**；恢复文件（recovery_*.jsonl，若存在）仅含最近未落库窗口，可回放极小量数据。此路径必须 IC 拍板并在通告中声明数据损失。

**当前缺口**：以上 Runbook 文档不存在；步骤 3（WAL 一起隔离）、步骤 5（.bak 会触发再迁移属预期）、步骤 7（HMAC env 检查）都是只有读过代码才知道的坑，口耳相传必然失传。

---

# 第三部分：Runbook 缺口清单

1. ❌ 《SQLite 损坏恢复 Runbook》——不存在（全文见上节）。
2. ❌ 《服务部署/重装 Runbook》——install-sim-services.ps1 未同步今日变更（KANBAN_HMAC_KEY 未注入）；验收命令用错探针。
3. ❌ 《断电/重启恢复自检清单》——主机重启后应验证什么（服务状态、/health/ready、大屏实际亮屏）无文档。
4. ❌ 《告警响应 Runbook》——因为根本没有告警，收到"大屏黑了"电话后的标准排查路径（服务状态→日志→探针）未成文。
5. ❌ 《备份恢复演练记录》——备份从未被恢复验证过；建议每季度一次恢复演练（本次桌面演练即为第一次）。
6. ⚠️ docs/ 下有《真机冒烟清单.md》和一篇 PlcSimulator 事故复盘（deliverables/engineering-assurance/review-incident-plcsimulator-2026-08-16.md），说明团队有复盘文化，但运维手册体系未建立。

# 第四部分：最该补的 3 个运维能力

1. **定时备份 + 异地副本**（行动项 #1/#2）。当前 RPO = 距上次发版的天数，且备份与主库同盘——这是所有缺口中唯一"可能永久丢数据"的，其余都是可用性问题。每天一次 SQLite 在线备份 + robocopy 到异机，半天工作量，收益最大。
2. **主动监控告警**（行动项 #5）。/health/ready 已经把所有该查的都查了（初始化、采集活性、库可写、积压、审计丢弃），缺的只是一个 5 秒轮询它并在非 200 时打电话/发消息的"人"。没有这个，本次演练的 MTTD 10 分钟会一再重演。
3. **SQLite 损坏自恢复能力 + Runbook**（行动项 #6/#7）。短期：把上节 Runbook 成文并演练（纯文档工作，立即可做）；中期：启动期检测到 malformed 自动从最近 .bak 还原降级运行（代码改动小，把"45 分钟人工"压缩到"5 分钟自动"）。

---
**备注**：本报告所有就绪性判断均基于今日（2026-08-16）对源码的实际阅读，关键证据已标注文件:行号。演练时间线中的故障行为（崩溃循环 3 次后服务停止、大屏重连耗尽永久断开、.bak 还原触发再迁移）均为代码可推导的确定行为，非假设。
