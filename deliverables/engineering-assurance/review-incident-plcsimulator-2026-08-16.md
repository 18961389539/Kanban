# PlcSimulator 全面工程审查 + 事故响应演练综合报告

**日期**：2026-08-16
**工作流**：工作流 1（全面代码审查）+ 工作流 3（事故响应）组合
**参与成员**：Cody（代码审查）/ Archi（架构）/ Rex（SRE）/ Tessa（测试）/ Docu（文档审查）

---

## 📌 TL;DR（执行摘要）

- 整体结论：PlcSimulator 顶层架构正确（真实 HslCommunication 协议栈仿真 + TcpRelay 故障注入），与 Collector 的核心数据契约（状态字/计数报警/清零握手/配方）对齐良好；但存在 **3 个高优正确性缺陷（2 个状态机/并发 + 1 个数据语义）**、**零自动化测试覆盖**、**无可观测性（无 /healthz、无实例身份）** 三大系统性短板。
- 严重度分布：🔴严重 0 项 / 🟠高 6 项 / 🟡中 10 项 / 🟢低 6 项
- 阻塞 / 非阻塞：3 项高优正确性缺陷（发现 #1/#2/#3，即 Cody 原始编号 H2/H3/H5）建议修复后再投入联调，其余为非阻塞改进。
- 事故演练结论：选定 SEV2 场景（nssm 复活残留实例抢占 4999 端口，Collector 采到 fault 场景脏数据，误判排查 2 小时），**5 Why 根因 = Sim 缺乏可观测性，无法区分新旧实例**。

---

## 🎯 核心结论卡片

| 项目 | 内容 |
|------|------|
| 整体评级 | 🟡 有条件通过（修复发现 #1/#2/#3 + 补 P0 测试后转绿） |
| 阻塞项数量 | 3（均为正确性缺陷） |
| 关键行动项 | 12 条（见下方「✅ 行动清单」） |
| 建议下一步 | 先修 3 个正确性缺陷 + 建 PlcSimulator.Tests（P0），再做 HTTP 控制面与实例身份（P1） |

---

## 🔍 审查发现（按严重度排序，已去重合并）

> **编号图例**：发现表以 #1-22 统一编号；来源列括号内为成员原始编号——H/M/L = Cody 原始高/中/低（H2→#1、H3→#2、H5→#3、H1→#7、H4→#8，合并后严重度以本表为准，H1/H4 已降为🟡中）；R/O/C/E = Archi 的可靠性/可观测性/配置/扩展性风险；F = Rex 故障模式；A = Rex 复盘行动项；ADR-S = 架构决策记录。

| # | 严重度 | 类别 | 位置 | 问题描述 | 建议修复 | 来源 |
|---|--------|------|------|---------|---------|------|
| 1 | 🟠高 | 正确性 | DeviceSimulator.cs:587-601 ↔ 1059-1073 | **缺料×阈值报警状态机交叉缺陷**：缺料中触发停机阈值报警（Status→Alarm）后，若缺料先到期，缺料退出分支无条件 `Status=Running` 覆盖 Alarm 态 → 报警恢复分支永不执行，_alarmEndTime 残留、连续不良清零/漂移回收/爬坡全部丢失；同类：Pause→阈值报警后 Resume 失效 | 缺料退出分支加 `if (Status == Idle)` 守卫，不覆盖 Alarm 态 | Cody (H2) |
| 2 | 🟠高 | 正确性 | Program.cs:516-532 / DeviceSimulator.cs:1351-1364 | **读失败静默吞为 0**：ReadInt/TryReadInt catch-all 返回 0，与真实 0 不可区分 → client 模式断线时 RestoreFromPlc 把产量读成 0 并改写真实 PLC 状态字为 Idle；缺陷计数从 0 重启写绝对值导致 Collector 看到负增长 | 读失败返回 int? 与真 0 区分；恢复时跳过失败地址并记 Warning | Cody (H3) + Archi (R4) |
| 3 | 🟠高 | 并发 | Program.cs:731-748 / 383-384 / 445 | **ReloadScenario 双 tick 竞态**：旧 tick 任务 3s 内未退出（卡在 _ioLock）即起新循环，旧循环每轮读字段当前值会开始 tick 新 simulator 列表 → 双循环并发驱动同一批设备，产量翻倍 | StartTickLoop 前确认旧任务已结束，否则拒绝 reload；或 tick 循环捕获列表快照 | Cody (H5) |
| 4 | 🟠高 | 可靠性 | Program.cs TickLoopAsync | **TickLoop 无故障隔离**：`sim.Tick(now)` 无 try/catch，任一设备抛异常静默终止整个 tick 循环 → 所有设备同时"冻产"，无告警（对照 ResetWatcherLoop 有保护）；StopTickLoopAsync 的 catch 还会吞掉该异常 | per-device try/catch（异常设备置 Error 态）+ 监督层重启循环，约 20 行 | Archi (R2) |
| 5 | 🟠高 | 配置 | devices.json 双端维护 | **设备地址配置双真相源**：simulator 的 devices.json 是 Collector Device 模型的精简镜像，各自演化无 schema 校验；地址漂移表现为"数据不涨/张冠李戴"，排查成本高；解析失败静默回退默认 3 台设备 | 以 Collector 配置为唯一真相源 + 导出工具；解析失败退出码非 0；地址冲突升级为拒绝启动 | Archi (C1/C2/C3) + Rex (F8) |
| 6 | 🟠高 | 测试 | 全项目 | **零自动化测试覆盖**：csproj:14 预留 InternalsVisibleTo PlcSimulator.Tests 但项目从未建立；仅被最慢最脆的 UIAutomation（Thread.Sleep+控制台文本匹配）间接覆盖；模拟器→Collector 段无任何自动化验证 | 新建 PlcSimulator.Tests（详见测试策略），约 1 天工作量 | Tessa + Cody（测试观察） |
| 7 | 🟡中 | 安全 | Program.cs:104,225 | `--listen-all` 绑 0.0.0.0 暴露无鉴权的三菱 MC（Melsec Communication）协议，局域网可任意读写虚拟 PLC 内存；help 中未列出、无风险提示（默认 loopback 是正确决策） | PrintHelp/启动横幅加醒目警告；考虑 IP 白名单 | Cody (H1，合并后降为🟡) |
| 8 | 🟡中 | 稳定性 | Program.cs:372 | 退出路径仍调 `_server?.ServerClose()`——代码注释自述 HSL ServerClose 在后台线程抛未处理异常可致进程崩溃，断线仿真期间退出风险更高 | 退出时直接让进程回收 socket，至少验证一次 Ctrl+C 路径 | Cody (H4，合并后降为🟡) |
| 9 | 🟡中 | 可观测性 | 全项目 | **无 /healthz、无实例身份、控制面仅 stdin**：非交互模式（CI）下 start/stop/alarm/reload 全部不可用，故障演练无法编排进流水线；这是事故演练 5 Why 的根因 | 内嵌 127.0.0.1 Minimal API（:4998）暴露 /healthz+/status+/devices/{id}/alarm 等；启动时写 sim_instance.json（实例 ID+时间戳+场景） | Archi (ADR-S2) + Rex (A1/F5) |
| 10 | 🟡中 | 运维 | nssm + start_env.ps1 | **僵尸实例抢端口**（事故场景 F1）：nssm 崩溃重启策略复活旧实例抢 4999，start_env.ps1 普通权限停不掉服务仅黄色警告，新实例因单实例互斥静默退出 | 启动脚本检测服务 Running 即中止报错；联调机卸载 nssm 由脚本统一管理生命周期 | Rex (F1/A2/A3) |
| 11 | 🟡中 | 数据 | Program.cs RestoreFromPlc | **数据漂移致联调误判**（F2）：不带 --fresh 重启恢复旧产量，连续不良部分恢复部分清零，联调看到"昨天产量+今天场景"混合数据 | --scenario 未知名显式警告（当前静默回退 normal）；故障演练后必须 --fresh 或打污染标签 | Rex (F2/F3) + Cody (M3) + Archi (C5) |
| 12 | 🟡中 | 可观测性 | SimLog.cs:62-78 | 日志无轮转无大小上限（stress 长跑无限增长）；控制台颜色三步无锁会串色；写失败静默吞掉 | 按大小滚动（10-50MB×5）；控制台段加同一把锁；中期改 JSON Lines 结构化 | Cody (M5) + Archi (O1/O2) + Rex (F7) |
| 13 | 🟡中 | 覆盖缺口 | Program.cs _server/_client | **仅三菱 MC 一种协议仿真**：Collector 5 品牌中 Siemens/ModbusTcp/Omron/Keyence 的端到端链路（连接管理→退避重连→采集→入库→SignalR）无仿真覆盖 | ADR-S1：抽 ISimPlcServer 接口，二期补 ModbusTcpServer（HSL 现成） | Archi (E1) |
| 14 | 🟡中 | 正确性 | DeviceSimulator.cs:218-232 | 恢复报警态仅扫前 10 个报警位，第 11+ 位 ON 时 Status 误判 Idle 且该 M 位永远无人清除 → MainAPP 看到孤立报警位 | 恢复时全量扫描或全量写 false 一次 | Cody (M4) |
| 15 | 🟡中 | 性能/正确性 | Program.cs:571-590 / DeviceSimulator.cs:131 等 | ReadAllValues 同地址重复读 3 次（client 模式 3 倍往返且值可能不一致）；Log?.Invoke 在持 _stateLock 期间同步做文件 IO，放大锁竞争 | 每地址读一次存字典；Log 出锁再 Invoke 或改异步队列 | Cody (M8/M9) |
| 16 | 🟡中 | 架构 | DeviceSimulator.cs 全量 | **上帝类**：1425 行、30+ 可变字段、12 种行为交织同一 Tick，新增行为触碰 6+ 处；场景全局共享无法做"A 机正常+B 机高 NG"差异化编排；墙钟时间特性（老化/疲劳）在 --speed 加速下失真 | ADR-S3/S7：场景外置 JSON+per-device override；SimClock 仿真时钟（登记为技术债随重构处理） | Archi (E2/E3/E4) |
| 17 | 🟢低 | 运维 | sim_pid.txt | 空文件全仓无代码读写，历史运维残留；PID 复用有误杀风险 | 删除或恢复实际用途 | Rex (F6) |
| 18 | 🟢低 | 正确性 | Program.cs:610-617 | StatusName 映射 0=待机/3="待机(3)" 与 MainAPP Paused=3 不一致，read 输出误导 | 统一 3=待机，0=初始/未知 | Cody (M2) |
| 19 | 🟢低 | 资源 | Program.cs:48,381 | 字段初始化 _cts 在 StartTickLoop 被替换，首个 CTS 未 Dispose | 字段改可空，StartTickLoop 中创建 | Cody (M6) |
| 20 | 🟢低 | 可维护性 | check_db.csx:4 / csproj:24 | check_db 硬编码 AppData 路径不读 KANBAN_DATA_DIR；System.IO.Ports 无版本且全项目无串口代码属冗余引用 | 对齐 GetConfigDir；删除冗余引用 | Cody (L5/L2) |
| 21 | 🟢低 | 边界 | Program.cs:204 / DeviceSimulator.cs:81 | port=65535 时 internalPort=port+1 溢出；OK/NG 计数 int32 语义与真实 PLC 16 位字溢出行为分叉（文档注明即可） | 启动校验 port∈[1,65534]；文档注明 int32 语义 | Cody (L1/L4) |
| 22 | 🟢低 | 架构 | 部署脚本 | 内部端口 5000 与 Keyence 默认端口相同；模拟器应永不进入生产部署清单 | ADR-S8：端口可配置 + 发布脚本显式隔离 | Archi (ADR-S8) |

### 做得好的地方（保留并发扬）
- 单实例互斥（含 AbandonedMutex 接管）实现规范；默认 loopback 绑定的安全默认值正确
- 锁顺序（_stateLock → _ioLock）有文档且全代码遵守，未发现反向获取
- 断线仿真用 TcpRelay 绕开 HSL ServerClose 已知崩溃并记录原因
- ResetWatcher 用 ReadInt 匹配 MainAPP WriteInt32 语义（踩过位/字语义坑并修正）
- DeviceSimulator 时间注入 + 回调 IO 设计，可测试性极佳
- 与 Collector 的四项核心契约（状态字/计数报警严格大于/清零握手/配方闭环）逐一对齐核实无误

---

## 🏗️ 架构影响评估（Archi 产出摘要）

顶层架构两项关键决策正确：① 复用 HslCommunication MelsecMcServer 托管虚拟 PLC 内存，对外即真实三菱 MC 协议端点，Collector 无需任何"模拟分支"；② 断线仿真不动 Server 而在外侧套 TcpRelay，规避 HSL ServerClose 崩溃坑并承担安全边界。

**8 项 ADR 建议（优先级序）**：
- **P0** ADR-S5 Tick 故障隔离（~20 行消除静默全冻）；ADR-S4 配置单一真相源 + 地址冲突拒启
- **P1** ADR-S2 控制面迁移 127.0.0.1 HTTP API（解锁 CI 故障演练与健康探针）；ADR-S6 日志轮转+结构化
- **P2** ADR-S1 多品牌协议仿真（先抽象 ISimPlcServer，二期 ModbusTcp）；ADR-S3 场景外置 JSON+per-device override
- **P3** ADR-S7 SimClock 仿真时钟 / ADR-S8 部署边界声明（技术债登记）

---

## 🚨 事故响应演练（Rex 产出摘要）

### 分诊
- **场景**：nssm 服务崩溃重启策略复活残留 PlcSimulator 实例抢占 4999 端口 + 单实例互斥使新实例静默退出 → Collector 连到 fault 场景旧实例，采到脏数据，团队误判为 ServerEpoch 游标 bug 排查 2 小时
- **SEV 评级：SEV2**（联调链路整体阻塞，但非生产环境且存在 workaround，取 SEV2 上限）
- **故障模式目录**：F1 僵尸实例抢端口 / F2 数据漂移 / F3 场景配置静默回退 / F4 批量更新丢数 / F5 端口占用静默退出 / F6 sim_pid.txt 孤儿 / F7 日志无轮转 / F8 地址冲突仅警告 / F9 KANBAN_DATA_DIR 漂移

### 5 Why 根因
脏数据 ← 连到旧实例 ← nssm 复活抢占端口 ← 脚本停服务失败仅警告 ← **无任何机制区分新旧实例（无 /healthz、无实例 ID/启动时间戳）= 系统缺乏可观测性（根因）**

### 复盘行动项（A1-A8 已并入总行动清单，落位核对见清单表下方备注）
关键手法沉淀：`netstat -ano | findstr :4999` → tasklist 反查 PID → 比对进程启动时间，识别僵尸实例。

> 故障模式处置说明：F5（端口占用静默退出）并入发现 #9（可观测性缺口）处置；F4（强杀时批量更新未 Flush 丢数）与 F9（KANBAN_DATA_DIR 漂移）由运维检查清单与联调规范覆盖，本次不列代码行动项。

---

## 🧪 测试覆盖评估（Tessa 产出摘要）

- **现状**：PlcSimulator.Tests 项目不存在（csproj 预留 IVT 是从未实施的规划）；MainAPP.Tests 的 5 品牌×15 契约测试用进程内 HSL 虚拟服务器，验证不到模拟器场景引擎；E2E 完全跳过 PLC；唯一真实链路覆盖在最慢最脆的 UIAutomation（9 用例，Thread.Sleep+控制台文本匹配，串行独占 4999）
- **可测试性极佳**：DeviceSimulator 时间注入 + 4 个 IO 回调注入 + Log 事件，纯内存即可确定性驱动
- **分层策略**：
  - **P0** 新建 PlcSimulator.Tests 单测（ScenarioConfig 不变量 / 状态机转换 / 发现 #1/#2 复现用例 / 阈值边界 / 班次时段边界 / DetectAddressConflicts），约 1 天，前提：构造函数注入可选 Random 种子
  - **P1** 进程内集成测试（MelsecMcServer+TcpRelay+真实 Tick，动态端口，严禁绑 4999）
  - **P2** 链路冒烟（环境变量双闸门控，仿 LiveCollectorProbeTests 模式，CI 默认跳过）
  - **P3** UIAutomation 瘦身，非 UI 断言下沉 P1/P2
- **CI 约束**：PlcSimulator.Tests 不共享 MainAPP obj 缓存可并行，但建议串行编排进测试阶段最前；net10.0-windows 需 Windows agent；过滤用 --filter-class/--filter-method（xunit.v3 Microsoft.Testing.Platform (MTP) runner 的 trait 过滤不生效是已踩过的坑）

---

## ✅ 行动清单（按优先级排序）

| # | 行动 | 负责角色 | 紧急度 | 预期完成 | 来源 |
|---|------|---------|--------|---------|------|
| 1 | 修复缺料×阈值报警状态机缺陷（缺料退出加 Idle 守卫） | Sim 负责人 | P0 | 3 天 | 发现 #1 (H2) |
| 2 | 修复读失败吞 0（int? 区分 + 恢复跳过失败地址） | Sim 负责人 | P0 | 3 天 | 发现 #2 (H3) |
| 3 | 修复 reload 双 tick 竞态（确认旧任务结束再启动） | Sim 负责人 | P0 | 3 天 | 发现 #3 (H5) |
| 4 | Tick 循环故障隔离 + 监督重启（~20 行） | Sim 负责人 | P0 | 3 天 | 发现 #4 / ADR-S5 |
| 5 | 新建 PlcSimulator.Tests 单测项目（含 Random 注入小重构 + 发现 #1/#2 复现用例） | 测试负责人 | P0 | 1 周 | 发现 #6 |
| 6 | 设备配置单一真相源 + 地址冲突拒启 + 解析失败非 0 退出 | Sim 负责人 | P0 | 1 周 | 发现 #5 / ADR-S4 |
| 7 | start_env.ps1 启动后校验：服务 Running 即中止报错 + 4999 对端进程启动时间晚于脚本时刻 | SRE | P1 | 3 天 | 发现 #10 (A2) |
| 8 | Sim 实例身份写入（sim_instance.json：GUID+启动时间+场景），Collector 首读校验失配告警 | Sim + Collector 负责人 | P1 | 1 周 | 发现 #9 (A1) |
| 9 | 内嵌 127.0.0.1:4998 HTTP 控制面（/healthz+/status+/scenario/reload） | Sim 负责人 | P1 | 2 周 | 发现 #9 / ADR-S2 (A4 依赖) |
| 10 | SimLog 按大小轮转（10-50MB×5）+ 控制台写加锁；--scenario 未知名显式警告；故障演练后 --fresh 或打污染标签写入联调规范 | Sim 负责人 + 联调主持人 | P1 | 1 周 | 发现 #12 (A7) + 发现 #11 (A5) |
| 11 | 联调机卸载 nssm 服务统一由 start_env 管理；清理孤儿 sim_pid.txt；Runbook 沉淀"端口反查 PID"手法 | SRE | P1 | 1 周 | 发现 #10/#17 (A3/A6/A8) |
| 12 | 协议抽象 + ModbusTcp 仿真（二期）；--listen-all 加醒目警告 | Sim 负责人 | P2 | 2-4 周 | 发现 #13 / ADR-S1 + 发现 #7 |

> Rex 复盘行动项 A1-A8 落位核对：A1→#8，A2/A3→#7/#11，A4→#9 的 Collector 联动部分，A5→#10，A6→#11，A7→#10，A8→#11。

---

## ⚠️ 待完善 / 已知局限

- 本次为静态审查 + 演练，未实际运行模拟器复现发现 #1/#2（对应复现测试用例已在行动清单 #5 中）。
- 事故时间线为合理假设（基于代码自述的反复踩坑与脚本行为推演），非真实事故记录。
- 发现 #7（--listen-all）与 #8（ServerClose 退出崩溃）需团队决策：修复 vs 接受现状并文档化。
- 发现 #14（报警位仅扫前 10 个）：接受为技术债，随 ADR-S3 场景外置一并处理；发现 #15（同地址重复读 / 锁内同步文件 IO）：列入性能优化 backlog，随 ADR-S2 控制面改造一并评估。
- 模拟器仅覆盖三菱协议，本报告的契约对齐结论不适用于其余 4 品牌。
- SimClock（墙钟时间特性加速失真）与 DeviceSimulator 拆分登记为技术债，未计入本次行动项。

---

## 📚 数据来源 & 成员产出索引

- Cody（代码审查师）：Program.cs/DeviceSimulator.cs 等 6 文件全量审查，5 高 9 中 6 低分级发现 + 测试观察，结论 Request Changes
- Archi（系统架构师）：内部架构梳理 + 5 项契约对齐核对 + R/O/C/E 四类风险 + 8 项 ADR（P0-P3）
- Rex（SRE）：F1-F9 故障模式目录 + SEV2 正式分诊 + 复盘文档（时间线/5 Why/A1-A8）+ 运维检查清单 + 监控指标建议
- Tessa（测试专家）：零覆盖现状确认 + 可测试性评估 + P0-P3 分层测试策略 + CI 接入约束
- Docu（技术文档师）：清晰度审查评分 8/10，指出 6 项必改（编号映射/复现范围矛盾/负责人缺失/孤儿引用等），本版已全部修订

---

> 本报告由工程保障团队 AI 协作生成，关键决策请由人类工程负责人复核。
