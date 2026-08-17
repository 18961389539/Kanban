# Kanban 系统架构评估报告（只读评估，未修改任何代码）

评估人：阿奇（Archi）· 系统架构师 | 范围：D:\code\Kanban\Kanban（.NET 10）| 所有结论均经 Glob/Grep/Read 实际验证

---

## ① 架构现状与数据流

### 组件依赖（依据 slnx + 各 csproj ProjectReference 实测）

```
Kanban.Contracts          ← 零依赖（DTO/Hub 契约/HubPaths），符合 ADR-6"不得引用 Core/MainAPP"
  ↑           ↑
Kanban.Analysis  Kanban.Client(SignalR 客户端，MessagePack+JSON 双协议)
  ↑    ↑        ↑        ↑
  │    │        │        └──── Kanban.Web (Blazor WASM, net10.0-browser, 只读展示)
  │    │        │
Kanban.Collector.Core ───┬──── Kanban.Collector (SignalR 服务端/Windows 服务/唯一写者/5129)
  ↑ (采集/存储/领域)      └──── MainAPP (WPF, net10.0-windows)
MainAPP 另引用：Kanban.Analysis、Kanban.Client、LicenseManager.App、oxyplot 本地源码(4 个项目)、HslCommunication.dll(裸引用)
```

实测要点：
- **MainAPP.csproj 注释自认是"展示端瘦身过渡期"**：`ProjectReference Include="..\Kanban.Collector.Core\..."` 上方注释写明"第 4 步改为客户端代理后移除"——架构债 #9 与代码内注释互证。
- MainAPP 与 Collector 均裸引用 `lib\HslCommunication\HslCommunication.dll`（非 NuGet），Collector 保留 net10.0-windows TFM 仅为兼容 HslCommunication 与 System.IO.Ports。
- Core 已去 WPF 化（net10.0，BindingOperations 下沉到 MainAPP.WpfCollectionBindingRegistrar），无头 Collector 不加载 WPF 程序集——边界治理做得干净。
- 解决方案含 4 个测试/质量项目（Tests/E2E/Benchmarks/UIAutomation），Core 与 Collector 均 InternalsVisibleTo 测试项目。

### 运行时数据流（实测验证每一环）

```
PLC ──► IPlcDriver(Hsl*) ──► PlcConnectionManager ──► PlcDataAcquisitionService
                                                          │
        ┌──────────────────────┬──────────────────────────┤
        ▼                      ▼                          ▼
 ProductionHistoryWriter   AuditService(Channel)      EventBroadcaster/SnapshotAggregator
 Channel.CreateBounded     (审计异步入库)              (Collector 侧 Channel 扇出)
 (10000, Wait, 单Reader)        │
 批写 200条/5s flush           ▼
        │                6 个 SQLite 库（各配独立 DbContext + Migrations + WAL）
        └───────────────► production_logs / alarm_events / status_transitions /
                          work_orders / defect_history / audit
                                                          │
                              KanbanHub(无认证, MaxParallel=16, MaxRecv=10MB)
                          MessagePack(NativeDateTimeResolver+Contractless 复合)
                                                          │
                    ┌─────────────────────────────────────┤
                    ▼                                     ▼
            MainAPP (Remote 模式)                   Kanban.Web (WASM, JSON 协议)
            KanbanDataClient                        DashboardState
            WithAutomaticReconnect(1/5/15/30s)      RetryLoopAsync
```

证据补强：
- **Channel 批写链路**：`ProductionHistoryWriter` 用 `Channel.CreateBounded<ProductionLog>(10000, FullMode=Wait, SingleReader)`，BatchSize=200、FlushIntervalMs=5000；通道满时转存 `production_logs.recovery.jsonl` 恢复文件（200MB 上限 + 回放失败 30s 退避）。背压与崩溃恢复路径完整，是这条链路上设计最扎实的部分。
- **6 库分立实测**：6 个 DbContext（ProductionLog/AlarmEvent/StatusTransition/WorkOrder/DefectHistory/Audit），各自独立 Migrations 目录；4 个有设计时工厂。WAL 模式经 `DatabaseProvider.EnsureWalModeEnabled` + `SqlitePragmaInterceptor` 统一开启（集成测试 DatabasePragmaTests 验证 .db-wal sidecar 存在）。
- **服务端防护**：`MaximumParallelInvocationsPerClient=16`（注释说明默认 1 会让长驻订阅永久占槽导致查询挂起——这是踩过坑后修的正确决策）；`MaximumReceiveMessageSize=10MB` 覆盖管理写大报文。
- **单实例保护**：`Global\Kanban.Collector.SingleInstance` 互斥（WaitOne(0) 接管 abandoned mutex，处理 UnauthorizedAccessException 保守退出），注释明确"宁可误杀也不裸奔防双写 SQLite"。

**结论：背景描述与代码现实一致，且 ADR-1~7（单写者/单源计算/进程边界等）已在 docs/架构决策记录.md 成文，与代码互证。**

---

## ② 关键决策复审表

| # | 决策 | 现状（代码证据） | 判断 | 理由 |
|---|------|------------------|------|------|
| 1 | IPlcBrandDescriptor + IPlcBrandRegistry 品牌单一事实源 | `PlcBrandDescriptors.cs` 单文件 5 个 internal sealed 描述符（三菱/西门子/ModbusTcp/欧姆龙/基恩士），注册表构造时校验重复品牌并抛异常；错误分类走"Socket 码→品牌码表→消息文本"三级回退 | **维持** | 扩展一个新品牌 = 加一个类 + CreateDefault 注册一行；ModbusTcp 的 0x80 异常码表证明错误分类已实际落地而非空接口。唯一改进点：5 个描述符 internal 在 Core 内，未来若第三方品牌插件化需开放 public 扩展点——当前无此需求，不动 |
| 2 | 页面导航懒加载（Lazy\<object\> + 10 个 Lazy VM） | `NavigationPageDefinition` 双 Lazy（view + viewModel，ExecutionAndPublication）；`MainWindowViewModel` 459-468 行 10 个 Lazy\<T\> 字段 + CreateLazy 工厂 | **维持** | 车间大屏冷启动时间直接可感；10 个页面 VM 全量构造会拖慢首屏。IServiceProvider 手写 Lazy 是 MS.DI 不支持 Func\<T\> 注入的务实绕行，模式统一、有 helper 收敛，不是债 |
| 3 | 重连所有权分层（客户端不内置首连重试） | `KanbanDataClient.ConnectAsync`：_connectGate 串行化 + 10s 超时 + 失败释放连接实例；注释明写"重连所有权归调用方"；实际持有方=WPF `ApplicationStartupCoordinator.StartRemoteRetryLoop`（_remoteRetryLoopStarted 单实例守卫）、`RemoteRuntimeSink`（events/subscribe 两个循环）、WASM `DashboardState.RetryLoopAsync`；运行中断线靠 WithAutomaticReconnect(1/5/15/30s) | **维持（建议补 ADR）** | 分层清晰、防双重循环互踩的意图明确且有三处一致实现。但这是"口口相传型约定"——现有 ADR-1~7 均未覆盖，新人加第四个调用方时极易破坏，应成文 |
| 4 | 服务端分页 MaxPage=200 / MaxPageSize=500 | `HistoryPagination`：clamp 实现，注释明写"防深分页 DoS——Skip 为 O(offset)"，200×500≈10万行上限；客户端 `MaxFetchAllPages=200` 与服务端对齐且有单元测试锁死 | **维持** | 上限选取有明确威胁模型（深分页 DoS），客户端翻页聚合对齐并有测试防漂移（RemoteHistoryQueryServiceFetchAllTests）。注意 MaxPageSize=500 与背景材料"MaxPage=200"表述的口径：两者都存在，无矛盾 |
| 5 | 6 库分立（每类历史一个 SQLite 文件） | 6 DbContext + 独立 Migrations + 统一 WAL；保留策略 HistoryRetentionService 按库分批清理 + checkpoint | **维持（边界上偏过度，但回撤成本高于收益）** | 优点真实：保留期/VACUUM/损坏隔离/单库体积（ADR-3 扩容触发条件"单库>2GB"依赖此拆分才能度量）；缺点也真实：跨库查询（如报警关联工单）只能应用层 join、6 套迁移治理成本。 Audit/DefectHistory 数据量小，2 库合并可议，但既然已分立且迁移已固化，**不动** |
| 6 | Hub 无认证 + CORS 全放行 | `Program.cs`：MapHub 无 RequireAuthorization、无 Authentication 中间件、CORS `SetIsOriginAllowed(_=>true)`；KanbanHub 注释"无认证（有意设计，局域网查看），操作人由查询串 operator 传入" | **有条件维持，立即立 ADR 锁定演进约束** | 见 ⑤ 风险节。当前 LAN 单机可接受，但这是全系统对未来架构演进（上云/多租户/跨网段）制约最大的一个决策，且"有意设计"只写在代码注释里，未进 ADR |
| 7 | MessagePack + NativeDateTimeResolver 复合解析器 | 服务端与 WPF 客户端两端对称配置，注释记录 2026-08-14 实测坑（直接替换 resolver 会 FormatterNotRegisteredException） | **维持** | 时区漂移是已发生过的真实线上问题，修复路径两端对称。WASM 走 JSON 不与 MessagePack 混用，协议矩阵清晰 |

---

## ③ 架构债清单

| 编号 | 债务 | 证据 | 处置建议 |
|------|------|------|----------|
| #9（已有） | MainAPP ProjectReference Collector.Core 支持 Local 模式 | MainAPP.csproj 注释"过渡期…第 4 步改为客户端代理后移除"；IRuntimeMode.cs 单一定义，但消费点散落 | **维持 2026-08-16 拍板（不急动），但补两个动作**：①把"Local 模式消亡的判定条件"写成 ADR-8（见 ④），防止无限期悬挂；②IsRemote 实测 **67 处/18 文件**（生产代码约 60 处，其中 RemoteHistoryQueryService.cs 独占 29 处、MainWindowViewModel 7、SettingsViewModel/RecipeManagerViewModel 各 4）——比背景估的 ~50 多约 30%，且 RemoteHistoryQueryService 是"门面内 29 个分支"，这类集中式 facade 分支是健康形态，真正需要警惕的是散落在 6 个 ViewModel + 2 个 XAML 里的约 20 处。建议冻结新增：Code Review 规则"新增 IsRemote 分支需走 facade 层" |
| #10（新发现） | HslCommunication 裸 DLL 引用（lib\ 目录，3 个项目引用） | MainAPP/Collector/Core 三个 csproj 均 `<Reference Include="HslCommunication"><HintPath>..\lib\...` | 低优先级。记录版本来源与升级路径（买源码版 or 锁定版本号），避免"lib 里的 DLL 是什么版本"成为考古题 |
| #11（新发现） | oxyplot 本地源码 4 个项目入 slnx | slnx 注释"便于调试/修改渲染行为"，Wpf 目标框架最高 net8.0-windows7.0 | 低优先级。本地分叉若已产生实际修改，记录 diff 清单；否则评估回归 NuGet 包以缩小构建图 |
| #12（新发现） | KanbanHub 管理域与监控域同 Hub 同连接 | KanbanHub 同时实现 IKanbanHubServer + IKanbanAdminServer，MaxRecv=10MB 是为管理写开的口子 | 中优先级。无认证前提下，任何能连 5129 的人都能调 SaveDevicesAsync。短期靠 LAN 边界，长期必须在 ADR 中明确"管理域鉴权是 Remote-only 化的前置" |

---

## ④ 建议立的 ADR 列表（接续现有 ADR-1~7，编号 8 起）

### ADR-8: Local/Remote 双模式的退役条件与冻结策略
- **背景**：MainAPP 仍引用 Collector.Core 支撑 Local 模式，IsRemote 分支 67 处；用户已拍板"不急着动"。
- **决策**：① 冻结——新增功能只允许 Remote 路径，新增 IsRemote 分支需 reviewer 特批且优先落在 facade（RemoteHistoryQueryService 模式）；② 退役触发条件成文化：连续 N 个交付现场零 Local 部署 + Remote-only 功能占比 100% 时启动"第 4 步"（移除 Core 引用，MainAPP 全面客户端代理化）；③ 退役步骤预定义：RemoteHistoryQueryService 等 facade 删除本地分支 → csproj 移除引用 → IRuntimeMode 删除。
- **后果**：变容易——双模式心智负担有明确终点；变困难——短期内新功能不能图省事走 Local 捷径。

### ADR-9: SignalR 重连所有权分层
- **背景**：KanbanDataClient 不做首连重试，三处调用方各持 5s 循环；约定目前只存在于注释。
- **决策**：成文分层——客户端库负责运行中自愈（WithAutomaticReconnect 1/5/15/30s），首连/重订阅重试归调用方且必须单实例守卫（_remoteRetryLoopStarted 模式）；新增调用方禁止在客户端库内加循环。
- **后果**：变容易——第四、第五个订阅方有章可循；需要重新审视——若将来引入 Polly 等库统一策略，本 ADR 作废重议。

### ADR-10: Hub 信任的边界声明（LAN 无认证是有意负债）
- **背景**：Hub 无认证、CORS 全放行、operator 由查询串自报、管理写接口（SaveDevicesAsync 等）与监控读接口同端点暴露。
- **决策**：显式声明"当前信任边界 = 车间局域网物理边界"；并列出触发即改的红线：① 部署跨网段/上云 → 必须先加认证（建议 Hub 层 JWT 或至少共享密钥 + TLS）；② 多租户 → operator 自报模式作废，改服务端身份解析；③ 管理域接口拆分独立 Hub + 授权策略，作为 Remote-only 化（ADR-8 第 4 步）的前置条件。
- **后果**：变容易——将来上云不被"锁死"，因为演进步骤已预写；变困难——承认当前任何 LAN 内设备可写配置，需网络层（VLAN/防火墙）补偿。

### ADR-11: 存储层维持 6 库分立 + SQLite 的适用边界
- **背景**：6 DbContext 已固化，ADR-3 已定"单库>2GB 触发扩容"。
- **决策**：确认 6 库不合并；补充边界声明——SQLite 适用上限按"单采集进程 + 单写者 + <100 设备 + 保留期受控"成立；出现以下任一信号即启动 ADR-3 扩展路径评估（多 Collector 分区）或换库评估：持续 PendingCount 积压、WAL checkpoint 延迟、单库逼近 2GB。
- **后果**：变容易——"什么时候 SQLite 不够用"有了度量标准而非感觉。

（可选 ADR-12: HslCommunication/oxyplot 依赖治理——价值较低，建议并入 ADR-11 附录或直接记录进 CodeWiki。）

---

## ⑤ 风险与权衡

**1. 单 Collector 单点（风险：中，已有对冲，维持）**
ADR-3 已明确不为 <100 台规模做 HA，兜底是 `sc failure` 自动重启 + Windows 服务 + 双端重连自愈 + ProductionHistoryWriter 恢复文件。评估认可此权衡：车间大屏场景"秒级黑屏后自愈"可接受，多 Collector 的数据源路由复杂度是负收益。**真正的单点残留是 %APPDATA%/Kanban 数据目录所在的单磁盘**——6 个 SQLite + devices.json 无备份机制（只看到 14 天日志保留），建议运维侧补一个每日 robocopy/SQLite online backup 脚本，这是比"多 Collector"便宜一个数量级的风险消除。

**2. SQLite 适用边界（风险：低，边界清晰）**
当前设计（单写者 + Channel 批写 200/5s + WAL + 保留期清理 + 深分页上限）把 SQLite 用在了它的甜区。明确不适用信号：多进程写（已被 ADR-2 单写者排除）、跨机查询（出现即说明该上服务端 DB）、单库 >2GB。判断：2-3 年内不需要换库。

**3. Hub 无认证对架构演进的制约（风险：当前低/演进高，最需关注）**
这是评估中唯一"现在没事、但会锁死未来"的决策。制约链：无认证 → operator 自报 → 审计数据不可信（仅可用于操作留痕，不可用于追责）；无认证 + 管理写同 Hub → 上云/跨网段时不是"加个认证"而是"协议面重做"；CORS 全放行 → 任何内网网页都能连 Hub 拉数据。**ADR-10 的价值在于把"锁死"变成"有路标的债务"**。同时提醒：LicenseManager.App 已具备密钥/指纹基础设施（EmbeddedKey/HardwareFingerprint 的 HMAC 机制），未来 Hub 加认证时可复用授权体系做屏端身份，不必从零造。

**4. 双模式的隐性成本（风险：中，建议冻结）**
67 处 IsRemote 意味着每个涉及数据访问的新需求都要回答两遍"Local 怎么办"。RemoteHistoryQueryService 的 29 处分支证明 facade 模式能收敛，但 6 个 ViewModel + 2 个 XAML 的散落分支说明纪律有缺口。建议把 ADR-8 的冻结规则写进 Code Review checklist。

**5. 构建期 Python 代码生成（观察项，非风险）**
MainAPP/Web 编译前跑 python 脚本从 resx 生成强类型资源——多语言单源做得好，但构建机多了 Python 依赖；CI 已稳定则不动，新环境搭建文档需注明。

---

**总体结论**：这套架构的成熟度高于典型车间软件——ADR 文化已建立、单写者/单源计算/背压恢复等硬决策都有代码+文档+测试三重证据。核心建议只有两条：把三个"注释级约定"（重连分层、无认证边界、双模式退役）升级为 ADR-8/9/10，以及给 SQLite 数据目录补备份。不建议现在做任何结构性重构。
