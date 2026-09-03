# Kanban 全面体检报告（第四轮：安全 / 架构 / 正确性 / 测试质量）

- **日期**：2026-09-03
- **范围**：全仓 8 个生产工程 + 4 个测试工程，只读审查
- **方法**：四路并行深扫（安全授权 / 架构依赖 / 时间·数值·边界 / 测试有效性），P0 级结论全部经主会话逐行源码复核
- **与既往报告的关系**：不重复 2026-09-01（性能两轮）与 2026-09-02（线程安全 P0）已覆盖的内容。既往的 4 个 P0 与 6 项 P0 性能项均已确认修复（HEAD `1ed7c72`）。

---

## 一、总结论

前两轮修完性能与线程安全后，代码库的**工程质量处于历史最好状态**。但本轮四个维度各挖出一批**此前从未被审视**的问题，性质比前两轮更"外科"：

| 维度 | 一句话结论 |
|---|---|
| 安全授权 | **认证体系形同虚设**：启动免登录、Hub 零鉴权、审计可伪造——权限模型写了但没通电 |
| 架构依赖 | Remote 瘦客户端架构**名存实亡**（csproj 注释自认"过渡期"），一处 DI 注册错误导致 Remote 模式读错库 |
| 时间·数值·边界 | **两个现场必踩的正确性 P0**：24:00 班次崩溃、计数器回绕静默吞产量 |
| 测试质量 | 2108 个用例纪律良好，但**套件当前是红的**（21 失败），且 E2E/核心模块处于零覆盖盲区 |

---

## 二、P0 — 必须修（8 项）

### 安全（已逐行复核）

| # | 问题 | 位置 |
|---|---|---|
| P0-1 | **WPF 启动无条件以 admin 自动登录**，登录窗只在"切换用户"时出现。任何人双击 exe 即得 Admin 全权限，角色权限模型整体被绕过 | `MainAPP/App.xaml.cs:216-233`（注释自认"默认账号 admin/gly"） |
| P0-2 | **SignalR 双 Hub 零鉴权**（全仓 grep `Authorize` 0 命中）+ 监听 `0.0.0.0:5129` + CORS `SetIsOriginAllowed(_ => true)` 任意来源。同网段任意主机可 `SaveDevicesAsync` 改写 PLC 配置、`ApplyRecipeAsync` 下发配方、删工单——**工业现场可致停机/误动作** | `Kanban.Collector/Program.cs:68-71, 157-158` |
| P0-3 | **审计操作人取自客户端可控 `?operator=` 查询串**（无鉴权下必然走此分支），改写配置后可栽赃任意人名，审计链不可溯源 | `Kanban.Collector/Hubs/KanbanAdminHub.cs:186-188`、`Kanban.Client/KanbanDataClient.cs:44` |

### 正确性（已逐行复核）

| # | 问题 | 位置 |
|---|---|---|
| P0-4 | **班次结束时间配 24:00 直接抛异常**：`LocalTime.FromTicksSinceMidnight` 合法上界 863,999,999,999，`TimeSpan(24,0,0).Ticks` 越界。而 `ShiftValidator` 把 1440 分钟当合法、测试把"16:00-24:00"写成**应当通过**的用例；6 个调用点全部无 try/catch，其中 `HomeViewModel.cs:1321` 在定时器回调里，**WPF 未捕获异常即进程退出**。"晚班 16:00-24:00"是工厂最常规配置 | `Kanban.Collector.Core/Models/ShiftConfig.cs:63-64`、`ShiftValidator.cs:33-34`、`MainAPP.Tests/Unit/ShiftValidatorTests.cs:197` |
| P0-5 | **PLC 计数器回绕/设备重启清零被静默吞掉**：`raw < baseline` 时直接把 baseline 压到 raw，本班次产量差分归零，**无日志无告警**。16 位计数器高速线上约 11 小时回绕一次。现场表现：产量曲线突然掉 0 重新累计，当班 OEE 全错且事后无从排查 | `Kanban.Collector.Core/Services/ProductionBaselineStore.cs:127-133`、`PlcDataAcquisitionService.cs:871-872` |

### 架构（已逐行复核）

| # | 问题 | 位置 |
|---|---|---|
| P0-6 | **`RuntimeMonitoringViewModel` 注入具体类 `HistoryService` 绕过 Remote 重定向**。`IHistoryService` 已重定向到 `RemoteHistoryQueryService`，但该 VM 用 `GetRequiredService<HistoryService>()` 拿具体类 → **Remote 模式下运行监控页读的是本地 SQLite 而非 Collector，数据错误且静默**。同文件其余注册全用接口，此处是漏网 | `MainAPP/Services/MainAppPresentationServiceCollectionExtensions.cs:47`（对比 `:71/:76/:90` 均为 `IHistoryService`） |

### 测试（已验证日志）

| # | 问题 | 位置 |
|---|---|---|
| P0-7 | **测试套件当前是红的：2455 用例中 21 失败 5 跳过**，失败日志就躺在仓库根目录未清理。重灾区：`RecipeManagerViewModelTests` 单文件 8 个全灭、`UserStoreLoginTests` 3 个、报警状态机 3 个、迁移兼容 2 个。工作区尚有 79 个生产文件未提交改动，需先判定是重构未同步测试还是真实缺陷 | `mainapp_tests_fail.log`（Total: 2455, Failed: 21, Skipped: 5） |
| P0-8 | **E2E/CI 盲区**：`MainAPP.E2E` 45 个用例**从未被任何脚本执行**；仓库无 `.github/` 目录（文档却声称有 workflow）；UIAutomation 76 用例仅手工触发且 7 个 Skip 全是"崩溃恢复"类最需跑的防御用例；覆盖率门槛 35% 形同虚设 | `ci/quality-gates.ps1:47,51`、`MainAPP.UIAutomation/ExceptionPathFlowTests.cs:33,48,65,86` |

---

## 三、P1 — 应该修（18 项）

### 安全加固（4）

| # | 问题 | 位置 |
|---|---|---|
| 1 | `operator` 免密账号：任意密码（含空）通过，且 `EnsureOperatorAccount` 每次加载自动重建——**删了会复活** | `Kanban.Collector.Core/Services/UserStore.cs:145-150, 326, 338-354` |
| 2 | 默认弱口令硬编码：`gly` / `gcs`，3 位拼音，口令空间 <2×10⁴ | `UserStore.cs:28-29` |
| 3 | 未登录态 `CurrentRole` 回退 `Operator`——"匿名即有权限"的错误默认值 | `MainAPP/Services/UserSession.cs:23` |
| 4 | 登录锁定仅按账号计数，无全局/来源节流，可轮换用户名枚举撞库；`Unlock` 无服务端角色校验 | `UserStore.cs:154-161, 192` |

### 时间·数值·边界（3）

| # | 问题 | 位置 |
|---|---|---|
| 5 | PLC 地址正则 `^D\d+$` 不限位数，`int.Parse("99999999999999")` 抛溢出——校验器 `DeviceConfigValidator.cs:436` 无 try/catch 自身崩溃，若流入热路径还会波及采集 | `PlcAddressParser.cs:149` 等 15 处同源 |
| 6 | `(int)avgTargetCycle` 截断均值节拍（143.33→143）；`ShiftValidator` 用 `(int)TotalMinutes` 截断含秒班次（08:00:30→480 分） | `OverviewDashboardService.cs:306`、`ShiftValidator.cs:46-47` |
| 7 | `AuditService.cs` 同文件混用 `DateTime.Now`（:170）与 `UtcNow`（:29/74/77/265）双约定，极易被后人改错 | `AuditService.cs` |

### 架构依赖（7）

| # | 问题 | 位置 |
|---|---|---|
| 8 | **MainAPP 深度绑定 Collector.Core 实现层**：63% 的 .cs 文件 `using Kanban.Collector.Core`，61 处构造函数注入具体类（DeviceRepository 54 / AppSettings 104 / WorkOrderRepository 28…）；`MainAPP.csproj` 未显式引用 `Kanban.Contracts`，契约层被整体绕过 | `MainAPP.csproj:96` 注释自认"过渡期，第 4 步改为客户端代理后移除" |
| 9 | **时间窗口快捷档位语义分叉**：WPF `5=本班次 6=上一班次 7=本周 8=本月`，Web `5=本周 6=本月`——同一序号两端含义不同，Web 注释还声称"与 WPF 口径对齐" | `HistoryQueryViewModel.cs:290-302` vs `Kanban.Web/Pages/HistoryQuery.Filters.cs:83-95` |
| 10 | 班次切分逻辑双份实现（自述"与 WPF 一致"但无机制保证）：`SplitShiftInstances` 两处 + `StartOfWeek` 两处 | `HistoryQueryHelper.cs:63` vs `Kanban.Web/Services/ProductionAnalysis.cs:27` |
| 11 | `AppSettings` 上帝对象：770 行、47 公开成员、被 166 个文件引用，职责含持久化/路径/班次/档案/校验/迁移，且 WPF 直接绑定 | `Kanban.Collector.Core/Services/AppSettings.cs` |
| 12 | ServiceLocator 反模式 16 处（含 try/catch 静默吞"避免循环依赖"）+ `new` 绕 DI 构造 ViewModel 21 处 | `SettingsViewModel.cs:42,714`、`DeviceManagerViewModel.cs:225-235` 等 |
| 13 | Remote 重定向靠"后注册胜出"隐式覆盖 7 个接口，注释自承"新增须同步"——无编译期保护，漏改即静默走本地（P0-6 就是这么漏的） | `MainAppServiceCollectionExtensions.cs:102-116` |
| 14 | `Kanban.Contracts` 混入业务规则（`DeviceHealthScore` 权重 0.30/0.20/0.25/0.25 等），契约层不再纯 | `Kanban.Contracts/Metrics/SnapshotMetrics.cs` |

### 测试有效性（4）

| # | 问题 | 位置 |
|---|---|---|
| 15 | **无时钟抽象**：生产代码 `TimeProvider/IClock` 0 命中，测试侧 `DateTime.Now` 254 次、`Thread.Sleep` 87 处（核心项目 8 处，`AlarmCenterViewModelTests.cs:59` 硬睡 1 秒）——flaky 温床，本次 21 个失败中防抖/定时类用例已实证 | 全仓 |
| 16 | 高复杂度**零测试**模块：`OverviewDashboardService`（760 行，OEE/复盘聚合核心，测试引用 0 次）、Web 侧 `OeeAnalysis/ReviewAnalysis/ProductionAnalysis/StatusAnalysis` 四大静态计算类全零、生产 ≥120 行文件 46/158 在测试中零引用 | `MainAPP/Services/OverviewDashboardService.cs` 等 |
| 17 | 58 个无断言"哑测试"（部分名字承诺行为体内空空）；5 个 `Assert.Skip` 含 `LocalizationGuardTests`——**本地化守卫在 CI 上形同虚设** | `ProductionDailyReportServiceTests.cs:92`、`LocalizationGuardTests.cs:159,199` |
| 18 | 测试组织：文件-生产映射率仅 46%；`Unit/` 扁平堆 167 文件；26 个文件超 400 行；12 文件无 Trait 无法按类筛选 | `HistoryQueryViewModelTests.cs`(1493 行) 等 |

---

## 四、P2 — 可选优化（12 项）

| # | 问题 | 位置 |
|---|---|---|
| 1 | CSV 导入无行数/大小上限，数百 MB 文件即 OOM（3 个 IOService 同模式） | `AlarmCsvIOService.cs:188-191` 等 |
| 2 | 报警 CSV **导出**无公式注入防护（历史导出侧已有防护，此处漏了） | `AlarmCsvIOService.cs:151` |
| 3 | License 运行期不复检：60s 刷新走 `recheck:false`，7×24 不重启则过期后可无限续用 | `SettingsViewModel.cs:498` |
| 4 | `users.json`（含哈希）明文落盘未收紧 ACL，`.bak/.corrupt` 副本扩大暴露面 | `UserStore.cs:97-98` |
| 5 | oxyplot/ 32MB 第三方源码直提交主仓（313 个 .cs，无 submodule） | `oxyplot/` |
| 6 | `scripts/` 5 个工具硬编码个人绝对路径 `C:/Users/35953/...`，且反向引用 MainAPP.csproj | `scripts/compare_output.py` 等 |
| 7 | 根目录杂物未 gitignore：`70000`(0字节)、`build_errors.txt`、`mainapp_tests_fail.log`、`sample2.md`、`run-demo/data/`(含真实 db/trial.dat) | 仓库根 |
| 8 | 批量读地址偏移算术无 `checked`，极端值可静默读到错误寄存器 | `PlcAddressCodecs.cs:119,64` |
| 9 | 无班次配置时 `ShiftName` 落库为空串，"非班次时段"产量与空名分组合并 | `PlcDataAcquisitionService.cs:1192` |
| 10 | 死代码：`MainWindowViewModel.cs:547` 空方法（却被 `:427` 接入）、`UserManagerViewModel.cs:145`；空 catch 13 处（均在清理路径，危害低） | — |
| 11 | `MainAPP.Benchmarks` 12 个基准无任何脚本执行、无回归阈值，纯摆设 | `MainAPP.Benchmarks/` |
| 12 | 测试 flaky 兜底缺失：`Task.Delay` 裸等待无 CancellationToken；`xunit.runner.json` 4 并行 × 静态状态手工重置 | `PlcMultiProfileAcquisitionTests.cs:203` 等 |

---

## 五、已核查确认干净的区域（不要重复投入）

- **密码哈希**：PBKDF2-SHA256 10 万迭代 + CSPRNG 盐 + `FixedTimeEquals` 常量时间比较（`PasswordHasher.cs:22-52`）——教科书级。
- **License 体系**：ECDSA P-256 仅内置公钥、时钟回拨三重防护、激活爆破锁定、机器码重验签（`LicenseSigningKey.cs` / `TrialTracker.cs` / `LicenseGate.cs:103`）——水准高于同类商业软件。
- **SQL**：全参数化，`tableName` 仅来自 EF模型元数据；8 个 DbContext索引与查询条件完全对齐。
- **JSON**：全仓无 Newtonsoft/TypeNameHandling/BinaryFormatter，统一 System.Text.Json 无多态配置。
- **单源收敛**：OEE 公式、CSV 转义、阈值、PLC 地址解析均已按 ADR-4 收敛，无重复实现。
- **DI 结构**：无循环依赖、无 Captive Dependency、DbContext 纯工厂模式、无 Scoped 被捕获。
- **测试纪律**：`Assert.True(true)` 类空断言 0 命中、无注释掉的测试体、catch 全部重抛、Mock:Assert = 66:5424 无 SUT 自 mock、命名规范统一。
- **数值守卫**：OEE 四率除零守卫齐全、无浮点 `==` 比较、状态时长按段差分无累加误差、跨零点班次归属正确（NodaTime `PlusDays(-1)`）、路径拼接 23 处全 `Path.Combine`、CSV BOM 读写对称。
- **TODO/FIXME**：真实命中 0 处（葡语 "Todos" 误报已排除）。

---

## 六、处置顺序建议

| 批次 | 内容 | 预估 |
|---|---|---|
| **0（立即）** | ① 班次 24:00 归一化一行修复（`% NodaConstants.TicksPerDay`）+ ShiftValidator 口径收敛；② `RuntimeMonitoringViewModel` 改 `IHistoryService`（一行）；③ 修 21 个红测试并判定是否为重构未同步 | 2 小时 |
| **1（本周）** | ④ SignalR Hub 加 Token 鉴权 + CORS 白名单 + `operator` 改服务端身份；⑤ 自动登录降级为 Viewer 只读（或弹登录窗）；⑥ 计数器回绕加 LogWarning 并区分回绕/清零 | 半天 |
| **2（下周）** | ⑦ E2E 接入 quality-gates + `OverviewDashboardService` 补测试 + 引入 `TimeProvider`；⑧ 地址解析加位数上限、CSV 导入上限、License `recheck:true` | 1-2 天 |
| **3（规划中）** | ⑨ Contracts 化去 Core 直绑（csproj 已规划的"第 4 步"）；⑩ 时间档位/班次切分抽到 `Kanban.Analysis` 单源 | 专项 |
