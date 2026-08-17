# PlcSimulator 代码级缺陷修复记录

**日期**：2026-08-16
**范围**：代码级缺陷全修（3 个阻塞正确性缺陷 + 全部中/低优代码缺陷 + 新建单测），不含大型架构重构（多品牌协议/HTTP 控制面/仿真时钟/配置单一真相源按 ADR 登记为 backlog）。

## 修复清单（对应审查报告发现编号）

### 🔴 阻塞正确性缺陷（3 项）

| # | 发现 | 修复 | 文件 |
|---|------|------|------|
| 1 | H2 缺料×阈值报警状态机交叉缺陷 | 缺料退出分支加 `Status == Idle` 守卫，不覆盖 Alarm 态 | DeviceSimulator.cs |
| 2 | H3 读失败静默吞 0 | 读失败与真 0 区分：`ReadInt/ReadBool` 失败抛异常 + `TryReadIntNullable` + 恢复时读失败跳过状态同步 | Program.cs + DeviceSimulator.cs |
| 3 | H5 ReloadScenario 双 tick 竞态 | reload 前校验旧循环确已结束，未结束则拒绝切换 | Program.cs |

### 🟡/🟢 中低优代码缺陷（10 项）

| 发现 | 修复 | 文件 |
|------|------|------|
| R2 Tick 循环无故障隔离 | per-device try/catch，单设备异常不拖垮整循环 | Program.cs |
| M3 scenario/speed 静默回退 | 未知名场景/非法 speed 显式警告 + InvariantCulture 解析 | Program.cs |
| M4 报警位仅扫前 10 | 全量扫描报警位 | DeviceSimulator.cs |
| M6 _cts 句柄泄漏 | StartTickLoop 创建新 CTS 前释放旧实例 | Program.cs |
| M7 场景参数零校验 | 新增 `ScenarioConfig.Validate()` + 启动时校验告警 | ScenarioConfig.cs + Program.cs |
| M8 ReadAllValues 重复读 | 每地址读一次缓存 + 拆出 ReadIntSafe/ReadBoolSafe | Program.cs |
| M2 StatusName 映射不一致 | 3=待机、0=初始/未知 | Program.cs |
| L1 port 溢出 | 启动校验 port ∈ [1, 65534] | Program.cs |
| L3 通信抖动空转 | 无候选地址时不设 EndTime | DeviceSimulator.cs |
| F7/O1 日志无轮转 + M5 控制台串色 | SimLog 按 10MB×5 轮转 + 控制台加锁 | SimLog.cs |
| L5 check_db 路径 | 读取 KANBAN_DATA_DIR | check_db.csx |
| M1 死代码 | ProduceOne 报警分支加防御注释 | DeviceSimulator.cs |

### 可测试性改造
- `DeviceSimulator` 构造函数新增可选 `Random? rng`（默认 `Random.Shared`），替换全部 `Random.Shared` 为 `_rng`，支持确定性单测。
- `Program.DetectAddressConflicts` 由 `private` 改为 `internal`（供单测）。

### 新建 PlcSimulator.Tests（15 个测试，全绿）
- `ScenarioConfigTests`：Get 回退语义 + 6 预设 Validate 不变量。
- `DeviceSimulatorTests`：状态转换（Start/Pause/Resume/TriggerAlarm/ResetCounts）+ **H2 回归用例** + 报警恢复。
- `DetectAddressConflictsTests`：无冲突/共享地址/大小写不敏感。

## 验证
- `dotnet build PlcSimulator`：0 警告 0 错误。
- `dotnet test PlcSimulator.Tests`：**15/15 通过**。
- 已把 PlcSimulator.Tests 加入 `Kanban.slnx`。

## 未处理（按审查结论登记为后续 backlog）
- 大型架构重构：ADR-S1 多品牌协议仿真、ADR-S2 HTTP 控制面/实例身份、ADR-S3 场景外置、ADR-S7 仿真时钟、ADR-S4 配置单一真相源、ADR-S8 端口可配置。
- 需团队决策：#7 --listen-all（已加醒目警告，白名单/IP 绑定待定）、#8 ServerClose 退出路径。
- 运维侧：start_env.ps1 服务校验、nssm 卸载、sim_pid.txt 清理（属 SRE 行动项，非 PlcSimulator 代码）。
- `System.IO.Ports` 引用经核实为 HslCommunication 服务器类的传递依赖（串口 RTU 支持），非冗余，保留。

> 本记录由工程保障团队 AI 协作生成，关键决策请由人类工程负责人复核。
