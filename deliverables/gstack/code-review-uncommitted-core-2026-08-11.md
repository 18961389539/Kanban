# 代码审查报告：未提交核心改动（认证/审计/PLC 扩展/License/Contracts）

**日期**：2026-08-11
**场景**：代码审查（未提交工作区改动，210 文件 +28970/-7613 行，排除 oxyplot 第三方源码与生成文件）
**参与成员**：🔍 产品官（gstack-product-reviewer，review skill 7 视角）+ 🛡️ 安全官（gstack-security-officer，OWASP Top10 + STRIDE）
**审查对象**：Audit 审计系统、User/密码认证、PLC 品牌描述符/Keyence 驱动、LicenseManager 加密、Kanban.Contracts 接口重构、历史查询分页、本地化、健康检查

---

## 📌 TL;DR（执行摘要）

- 整体结论：🟡 **有条件通过（条件较硬）** —— 认证体系可被完全绕过，不建议按当前状态合入
- 阻塞项（P0）：1 条（自动 admin 登录，认证整体形同虚设）
- 高优（P1）：7 条（含 1 条会静默丢配置字段的数据丢失级正确性问题）
- 下一步：先修 P0（认证入口）+ P1 全部（配置落盘丢字段、批量查询 DoS、默认口令、审计归属、License 密钥），再合入

---

## 🎯 核心结论卡片

| 项目 | 内容 |
|------|------|
| Go / No-Go | 🟡 条件 Go —— 修复 P0 + P1 后合入 |
| 严重度分布 | 🔴 1 / 🟠 7 / 🟡 8 / 🟢 8 |
| 关键行动项 | 7 条（P0×1 + P1×6 修复） |
| 建议负责人 | 架构主理（认证入口改造）、Collector 端（配置落盘/查询上限/审计归属）、License 维护（密钥策略） |

---

## 1. 各成员核心结论

### 🔍 产品官（代码审查）
- 核心判断：🟡 有条件通过。无 P0，但 **P1#1 配置草稿保存会静默重置 settings.json 大量字段**（语言/主题/数据模式等），是数据丢失级正确性问题；P1#2 批量查询无上限叠加无认证 Hub 构成 DoS 新面。
- 关键建议：草稿全字段拷贝或仅序列化变更字段；批量查询加 Count/行数上限；新增测试覆盖这两处（本次漏网主因是测试缺口）。

### 🛡️ 安全官（安全审计）
- 核心判断：🔴 认证体系设计上可被完全绕过（启动自动 admin 登录、零密码校验），审计不可归属（Hub 零鉴权 + 操作人恒空），License 密钥防护被本次改动主动削弱。
- 关键建议：启动必须走真实认证；管理域接口强制鉴权；License 依赖环境变量密钥且 fail-closed；审计必带连接身份。
- 威胁建模要点：信任边界=本机用户与局域网之间；所有"认证"都在客户端本地完成，等价于无认证；审计的信任假设（操作人可信）不成立；users.json/audit_logs.db 同目录无 ACL，本地用户=完全控制。

---

## 2. 综合审查发现（去重合并后按严重度排序）

| # | 严重度 | 类别 | 位置 | 问题描述 | 建议 | 来源成员 |
|---|--------|------|------|---------|------|---------|
| 1 | 🔴 | 认证 | MainAPP/App.xaml.cs:163-174 | 启动即自动以 admin 登录、零密码校验（未调 Authenticate）；登录窗仅"切换用户"出现 → 任何能启动程序的人=全功能 Admin（用户管理/审计/设置/设备配置） | 启动走真实认证；admin 首登强制改密；自动登录仅限 Viewer 模式 | 安全官 F-001 |
| 2 | 🟠 | 认证 | UserStore.cs:212-244 | 默认账号弱凭据（admin/gly、engineer/gcs 明文 3 字符密码在源码）；operator 免密（PasswordHash 空=任意密码可登录）；EnsureOperatorAccount 升级自动补免密账号；无暴力破解防护 | 移除免密账号；随机初始密码+强制首登改密；加失败锁定/延迟 | 安全官 F-002/F-006 + 产品官 P1#3 |
| 3 | 🟠 | 加密 | PasswordHasher.cs:24,42 | 密码哈希=单轮 SHA256(salt+password)，无 KDF 迭代（盐 16B 随机、FixedTimeEquals 常量比较 OK）→ users.json 泄露后 GPU 离线爆破 | 改 PBKDF2(Rfc2898DeriveBytes)≥10 万次或 Argon2id | 安全官 F-003 |
| 4 | 🟠 | 数据丢失 | ConfigSyncHandler.cs:210-250 | SaveCollectorSettingsAsync 草稿只复制 5 个采集字段，却调 WriteSettingsFile 全量序列化落盘 → Language/AppTitle/DataMode/UiScale/IsDarkTheme 等全部被默认值覆盖；MainAPP 与 Collector 共用 Config 目录，Remote 每保存一次采集设置即重置客户端语言/主题/配置 | 草稿改完整字段拷贝或仅序列化变更字段；补"非采集字段保留"测试 | 产品官 P1#1 |
| 5 | 🟠 | 安全/DoS | HistoryQueryHandler.cs:48-72 | QueryBatchAsync 不限制 Queries.Count、每子查询 QueryCoreAll 全量物化（百万行/子查询），叠加无认证 Hub → 单请求打满线程池/内存；批量路径忽略 LatestFirst（单查 1 条/批查全量语义不一致） | Queries.Count≤64、单子查询行数上限、LatestFirst 复用单查语义 | 产品官 P1#2 |
| 6 | 🟠 | 审计 | CollectorWorker.cs:103 + KanbanHub.cs:99-194 | AuditLog.Initialize 无 operatorProvider → Hub 触发审计 Operator 恒空串（AuditEntry 注释"预留 Remote"未实现）；四个管理写接口仍零鉴权 → 追责链条断裂，新审计给出"已审计"假象 | 审计必带连接身份（Remote:\<IP\>）；管理域接口强制鉴权 | 安全官 F-004 + 产品官 P3#9 |
| 7 | 🟠 | 加密 | LicenseManager.App/Crypto/*.cs + obfuscar.xml | 本次改动删除 4 个 Crypto 类的 [Obfuscation(Exclude=false)] 且明确"不混淆 Crypto 命名空间"；此前 HideStrings 加密存储的 HmacKeyBase64 现明文落 IL（EmbeddedKey.cs:38）→ 反编译即取对称密钥、可伪造永久 License | 生产强制 KANBAN_HMAC_KEY 且缺失 fail-closed；或改非对称签名（私钥仅存签发端） | 安全官 F-005 |
| 8 | 🟡 | 审计 | DatabaseProvider.cs:166-178 + AuditService | ApplyAuditSchemaPatches 只补 BeforeJson/AfterJson 列，不补 Timestamp/Operator/Action/Target 索引 → 旧库审计查询全表扫描；QueryPaged 前导通配 LIKE(%op%) 无法走索引 + Count() 全表计数 | 迁移补索引；查询改前缀匹配或服务端分页 | 产品官 P2#4/P2#7 |
| 9 | 🟡 | 审计 | audit_logs.db + AuditService.cs:148-167 | 审计库与业务库同目录明文 SQLite、无哈希链/ACL/防伪；30 天自动清理直接删除无归档；AuditLog.Record 静默吞异常、队列满 DropWrite 丢审计 | 审计库哈希链+导出归档、写权限收紧、失败计数告警 | 安全官 F-007 |
| 10 | 🟡 | 权限 | MainWindowViewModel.cs:320-362 | 权限仅 UI 层导航控制，VM 命令内无角色校验 → 配合 F-001 实际全开 | 命令层注入 AuthorizationService 二次校验 | 安全官 F-008 |
| 11 | 🟡 | 性能 | CollectorReadinessCheck.cs:79 | ProbeWriteAccess 每次探针执行 PRAGMA quick_check 全库完整性校验（O(库大小)），高频轮询与采集写竞争 IO | quick_check 仅启动期一次，探针只留 BEGIN IMMEDIATE 写锁探测 | 产品官 P2#5 |
| 12 | 🟡 | 架构 | KanbanDataClient.cs | Closed 处理移除后台自愈，重连所有权移交调用方 → 需核对 WASM RetryLoop/WPF Coordinator 之外无遗漏调用方，遗漏即永久离线 | 全量核对调用方清单 | 产品官 P2#6 |
| 13 | 🟡 | 并发 | PlcDataAcquisitionService.cs:70 | _lastDefectSnapshot 字典设备删除后 key 永不清理（单线程无竞态，仅增长）；Collector 重启后首轮全量写缺陷快照 | 设备删除时清理 key | 产品官 P2#8 |
| 14 | 🟢 | 配置 | PlcConfigJsonConverter ApplyLegacyFlatFields | 无条件覆盖嵌套值，与注释"仅未写入时覆盖"不符（当前写入器不产生并存文件，低风险） | 按注释实现或改注释 | 产品官 P3#11 |
| 15 | 🟢 | 卫生 | 仓库根 | HomeViewModelTests.cs.tmp_check、_shots/（截图+kc.dll.bak 二进制）、TestData_demo/*.dat、Strings_merged.csv、issued/ 产品密钥 JSON（含完整 ProductKey）等已入索引 | .gitignore 补规则 + git rm --cached 清理 | 产品官 P3#10 + 安全官 F-010 |
| 16 | 🟢 | 配置 | Program.cs:58,112-126 | Collector UseUrls("http://0.0.0.0:5129") 写死代码；/health/live、/health/ready 未鉴权（仅泄露存活状态） | 绑定移至配置+文档化风险；内网 ACL | 安全官 F-011/F-009 |
| 17 | 🟢 | 校验 | AppSettings PollingIntervalMs | 下限 1→10ms：存量 1-9ms 配置保存时新增校验失败（加载不受影响） | 迁移或提示 | 产品官 P3#13 |
| 18 | 🟢 | 权限 | UserManagerViewModel | 可编辑自己角色/停用自己（禁删自己但不一致） | 一致性校验 | 产品官 P3#14 |

---

## ✅ 行动清单

| # | 行动 | 负责方 | 紧急度 | 期望完成 |
|---|------|--------|--------|---------|
| 1 | 认证入口改造：启动必须真实认证（去掉自动 admin 登录），admin 首登强制改密 | 主理人/客户端 | P0 | 合入前 |
| 2 | 移除免密账号与明文默认口令：随机初始密码 + 强制首登改密 + 登录失败锁定 | 主理人/客户端 | P1 | 合入前 |
| 3 | ConfigSyncHandler 草稿保存改完整字段拷贝（防 settings.json 字段静默重置）+ 补测试 | 主理人/Collector | P1 | 合入前 |
| 4 | HistoryQueryHandler 批量查询加 Queries.Count≤64 + 单子查询行数上限 + LatestFirst 语义对齐 | 主理人/Collector | P1 | 合入前 |
| 5 | PasswordHasher 升级 PBKDF2（≥10 万次）或 Argon2id，存量哈希做迁移策略 | 主理人/Collector | P1 | 合入前 |
| 6 | 审计归属：AuditLog.Initialize 传 operatorProvider，Hub 审计带连接身份；管理域接口鉴权 | 主理人/Collector | P1 | 合入前 |
| 7 | License 密钥策略：生产强制 KANBAN_HMAC_KEY + fail-closed（撤销静默回退），恢复 Crypto 混淆 | 主理人/License | P1 | 合入前 |

---

## ⚠️ 待完善 / 已知局限

- 本次为静态代码审查（无运行期渗透/联调验证），P0/P1 修复后建议跑一轮 LiveCollectorProbeTests + 全量 1700 测试回归
- 批量查询 DoS 的具体压测数据未采集（无运行环境实测吞吐）
- 测试缺口：ConfigSyncHandler 草稿保存无"非采集字段保留"断言、批量查询无上限无测试、Keyence 模拟仅 2 条、PlcConfigJsonConverter 并存字段优先级无测试
- 已知旧问题（未在本次发现列表重复计分）：Collector Hub 无认证授权、监听 0.0.0.0:5129、CORS 全放行、授权端 HMAC 硬编码回退、审计页 Before/After 展示待补——其中 #4/#6 因新增功能扩大暴露面已按新功能计分

---

## 📚 成员产出索引

- gstack-security-officer（安全官）原始产出：F-001~F-011 全量清单（OWASP A01/A02/A07/A08/A09 + STRIDE 提权/欺骗/篡改/抵赖），TL;DR 🔴1/🟠5/🟡3/🟢3
- gstack-product-reviewer（产品官）原始产出：P1×3/P2×5/P3×6 共 14 条（review skill 7 视角），TL;DR 有条件通过

---

> 本报告由软件工坊 AI 协作生成，关键决策请由工程负责人复核。
