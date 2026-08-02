# 项目约定

## 业务规则
- OEE 可用率公式：`RunTime / (RunTime + AlarmTime)`，**不包含 PausedTime**（故意设计，不修改）
- OEE/可用率/性能率/合格率均限制在 [0, 1] 范围内，避免 > 100%
- OK/NG 产量来自 PLC 累加计数器，使用基线快照计算会话增量（当前值 - 基线）
- **PLC 计数器回退保护**：当 raw < baseline 时（PLC 程序清零或操作员手动清零），自动更新 baseline 为当前 raw，避免整个班次剩余时间产量永久显示 0
- 缺陷计数同样来自 PLC 累计值，持续递增不清零
- 所有 PLC 数值读写均为 **Int32**（含 StatusWord）
- StatusWord 定义：1=运行, 2=报警, 3=暂停（使用等值判断，非位掩码）
- OEE 时间累计使用 Stopwatch 测量真实间隔（包含 PLC 读取耗时 + Task.Delay），非固定 PollingIntervalMs
- **PLC 断线/异常时重置 Stopwatch**：断线分支和 catch 块都调用 Restart()，避免重连后第一次成功读取时 elapsed 包含整个断线期间导致 OEE 时间暴涨
- 班次切换时同步重置：OEE 时间、会话累计产量、报警时间戳（Alarm.StartTime/EndTime）、报警状态字典；产量基线字典延迟1秒清空
- 报警内存状态清空后，下次 PLC 读取从 AlarmEvents 表重建（若最近事件为"触发"且未恢复）
- 删除设备后调用 PlcDataAcquisitionService.RemoveDeviceState 清理 _prevAlarmStates / _prevStatusWords 残留 key；产量基线由 ProductionBaselineStore.ClearDevice 清理（内存活动缓存 + 磁盘快照）
- 应用退出时 App.OnExit 持久化设备列表 + AppSettings，失败弹窗提示（不静默吞掉）
- RecipeValue 写入 PLC 前校验范围 0-999999
- **部分设备读取失败不阻塞历史快照写入**：只有所有设备都失败才 MarkDisconnected，至少一台成功仍按间隔写历史

## 持久化架构（EF Core）
- DeviceStore 类已删除，设备持久化由 DeviceRepository（DI 单例）封装
- DeviceRepository.Devices 是运行时共享设备列表（ViewModel 和 PlcDataAcquisitionService 通过构造函数注入引用）
- DeviceRepository.LoadAll() / SaveAll() 通过 EF Core 操作 app.db
- AppDbContext 统一管理 5 张表：Devices / Alarms / Defects / ProductionLogs / AlarmEvents
- Alarm/Defect 有 DeviceId 外键关联 Device.Id（UI 增删时不关心，SaveAll 时统一回填）
- 运行时状态字段（OkProduction/RunTime/AlarmTime/PausedTime/StatusWord/TotalOkProduction/TotalNgProduction/Alarm.StartTime/EndTime/Defect.Count）用 [NotMapped] + [JsonIgnore] 同时排除 EF Core 和 JSON 序列化
- HistoryService 改用 EF Core（IDbContextFactory<AppDbContext>），不再手写 SqliteConnection/SQL
- AppDbContextFactory 是 Singleton，每次 CreateDbContext 读取最新 AppSettings.ConfigDirectory 保证路径生效
- 数据库文件统一位于 Config/app.db（原 Config/devices.json 和 Config/history.db 已废弃）
- **SaveAll 采用 diff 策略**：对比内存与 DB，只对变更部分操作（删除/新增/更新），递归 diff Alarm/Defect 子集合，失败回滚
- LoadAll 用 AsNoTracking 加载设备 + Include(Alarms/Defects) 导航属性

## HistoryService 写入策略
- **写入队列 + 后台批量 flush**：ProductionLog 和 EventType=1/2 的 AlarmEvent 入 ConcurrentQueue，后台线程每5秒批量写入（单次上限200条），避免阻塞采集线程
- **写入失败数据不丢失**：批量写入失败时把已 Dequeue 的数据重新入队，下次 flush 重试（不 throw 避免后台任务崩溃）
- **EventType=3 班次切换事件走同步写入路径**：需确保持久化才能避免重建错乱，调用方据此重试3次
- **历史数据自动清理**：App 启动时调用 CleanupOldAlarmEvents(365) + CleanupOldProductionLogs(365)，删除超过一年的报警事件和生产快照
- HistoryService 实现 IDisposable，退出时 flush 队列剩余数据 + 取消后台任务

## 班次切换业务规则
- 班次切换时 ResetShift 会向 AppSettings.ProductionResetAddress 配置的统一清零地址写 1（D 字地址，默认 D115），由 PLC 程序负责清零所有产量计数器；软件不再向每个 OK/NG 地址写 0
- 产量清零地址为空时跳过 PLC 清零触发（仅清软件内部累计），格式非 D 字地址时记日志跳过
- **PLC 清零与基线清空延迟同步**：触发 PLC 清零后延迟1秒再结束清空窗口；软件基线已通过 ProductionBaselineStore.ClearAll 在 ResetShift 时清空（内存活动缓存 + 磁盘快照），避免 PLC 程序清零未完成时旧累计值被当作新基线
- **PLC 未连接时推迟清空基线**：标记 _pendingPlcResetOnReconnect，PLC 重连后再触发清零 + 延迟1秒清空基线
- 班次切换时对仍触发中的报警记录 EventType=3"班次切换"事件到 AlarmEvents 表（重试3次，仍失败标记到 _shiftChangeFailedAlarms 集合）
- 重建报警状态时若最近事件是 EventType=3，当作未触发处理（StartTime 留空，新班次重新触发上升沿）
- EventType=3 写入失败的报警：ScanAlarms 重建时跳过历史查询直接当作未触发，避免 StartTime 回填为上个班次时刻
- ProductionLog 含 ShiftName 快照字段，写入时从 _currentShiftId 解析当前班次名
- 班次配置修改时 SettingsViewModel.Save 提示用户"新配置立即生效，可能导致班次进行中被中断"
