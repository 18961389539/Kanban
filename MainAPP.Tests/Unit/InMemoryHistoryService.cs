using MainAPP.Data;
using MainAPP.Entities;
using MainAPP.Services;

namespace MainAPP.Tests.Unit;

/// <summary>
/// IHistoryService 的内存桩：用 List 承载数据，完全不依赖数据库/EF。
/// 用于在纯单元测试中验证查询页各 ViewModel 的逻辑（窗口差分、状态时长、报警计数等），
/// 也用于 PlcDataAcquisitionService 单测的写入路径验证（报警边沿、状态转换、班次切换事件）。
/// 不触发任何 SQLite 文件 IO，跑得快、可独立运行。
/// 线程安全：查询/写入方法加锁拷贝快照，支持 ConcurrencyTests 并发场景
/// （真实 HistoryService 通过 SQLite 连接池/事务保证线程安全，桩需对齐）。
/// 公开 List 属性保留可写，便于非并发测试通过 AddRange 批量注入数据。
/// </summary>
internal sealed class InMemoryHistoryService : IHistoryService
{
    private readonly object _lock = new();

    // 公开 List 供测试直接注入数据（AddRange）；查询/写入方法加锁读取，避免并发枚举异常。
    public List<ProductionLog> ProductionLogs { get; } = new();
    public List<StatusTransitionRecord> StatusTransitions { get; } = new();
    public List<AlarmEventRecord> AlarmEvents { get; } = new();

    public List<ProductionLog> QueryProductionLogs(DateTime from, DateTime to, string? deviceId = null, string? shiftName = null)
    {
        List<ProductionLog> snapshot;
        lock (_lock) snapshot = ProductionLogs.ToList();
        return snapshot
            .Where(p => p.Timestamp >= from && p.Timestamp <= to)
            .Where(p => deviceId == null || p.DeviceId == deviceId)
            .Where(p => shiftName == null || p.ShiftName == shiftName)
            .OrderBy(p => p.Timestamp)
            .ToList();
    }

    public List<ProductionLog> QueryProductionLogsByWorkOrder(int workOrderId)
    {
        List<ProductionLog> snapshot;
        lock (_lock) snapshot = ProductionLogs.ToList();
        return snapshot
            .Where(p => p.WorkOrderId == workOrderId)
            .OrderBy(p => p.Timestamp)
            .ToList();
    }

    public ProductionLog? GetLatestProductionBefore(string deviceId, DateTime before, string shiftName)
    {
        List<ProductionLog> snapshot;
        lock (_lock) snapshot = ProductionLogs.ToList();
        return snapshot
            .Where(p => p.DeviceId == deviceId
                        && (p.ShiftName ?? string.Empty) == shiftName
                        && p.Timestamp < before)
            .OrderByDescending(p => p.Timestamp)
            .FirstOrDefault();
    }

    public List<StatusTransitionRecord> QueryStatusTransitions(string deviceId, DateTime from, DateTime to, string? shiftName = null)
    {
        List<StatusTransitionRecord> snapshot;
        lock (_lock) snapshot = StatusTransitions.ToList();
        return snapshot
            .Where(s => s.DeviceId == deviceId && s.EventTime >= from && s.EventTime <= to)
            .Where(s => shiftName == null || s.ShiftName == shiftName)
            .OrderBy(s => s.EventTime)
            .ToList();
    }

    public StatusTransitionRecord? GetLatestStatusBefore(string deviceId, DateTime before, string? shiftName = null)
    {
        List<StatusTransitionRecord> snapshot;
        lock (_lock) snapshot = StatusTransitions.ToList();
        return snapshot
            .Where(s => s.DeviceId == deviceId && s.EventTime < before)
            .Where(s => shiftName == null || s.ShiftName == shiftName)
            .OrderByDescending(s => s.EventTime)
            .FirstOrDefault();
    }

    public List<AlarmEventRecord> QueryAlarmEvents(DateTime from, DateTime to, string? deviceId = null, string? shiftName = null)
    {
        List<AlarmEventRecord> snapshot;
        lock (_lock) snapshot = AlarmEvents.ToList();
        return snapshot
            .Where(e => e.EventTime >= from && e.EventTime <= to)
            .Where(e => deviceId == null || e.DeviceId == deviceId)
            .Where(e => shiftName == null || e.ShiftName == shiftName)
            .OrderBy(e => e.EventTime)
            .ToList();
    }

    public Dictionary<string, List<ProductionLog>> QueryProductionLogsBatch(DateTime from, DateTime to, IReadOnlyList<string> deviceIds)
    {
        List<ProductionLog> snapshot;
        lock (_lock) snapshot = ProductionLogs.ToList();
        var idSet = deviceIds.ToHashSet();
        return snapshot
            .Where(p => idSet.Contains(p.DeviceId) && p.Timestamp >= from && p.Timestamp <= to)
            .OrderBy(p => p.Timestamp)
            .GroupBy(p => p.DeviceId)
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    public Dictionary<string, List<AlarmEventRecord>> QueryAlarmEventsBatch(DateTime from, DateTime to, IReadOnlyList<string> deviceIds)
    {
        List<AlarmEventRecord> snapshot;
        lock (_lock) snapshot = AlarmEvents.ToList();
        var idSet = deviceIds.ToHashSet();
        return snapshot
            .Where(e => idSet.Contains(e.DeviceId) && e.EventTime >= from && e.EventTime <= to)
            .OrderBy(e => e.EventTime)
            .GroupBy(e => e.DeviceId)
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    public Dictionary<string, List<StatusTransitionRecord>> QueryStatusTransitionsBatch(DateTime from, DateTime to, IReadOnlyList<string> deviceIds)
    {
        List<StatusTransitionRecord> snapshot;
        lock (_lock) snapshot = StatusTransitions.ToList();
        var idSet = deviceIds.ToHashSet();
        return snapshot
            .Where(s => idSet.Contains(s.DeviceId) && s.EventTime >= from && s.EventTime <= to)
            .OrderBy(s => s.EventTime)
            .GroupBy(s => s.DeviceId)
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    public AlarmEventRecord? GetLatestAlarmEvent(string alarmId)
    {
        List<AlarmEventRecord> snapshot;
        lock (_lock) snapshot = AlarmEvents.ToList();
        return snapshot
            .Where(e => e.AlarmId == alarmId)
            .OrderByDescending(e => e.EventTime)
            .FirstOrDefault();
    }

    // ──────────── 写入方法 ────────────
    //
    // 写入路径直接 List.Add 并返回 true，模拟 HistoryService 的同步写入成功路径。
    // 测试需要模拟写入失败时（如验证 PlcDataAcquisitionService 的"写失败不更新 _prevAlarmStates"重试逻辑），
    // 可设置 ShouldFailAlarmEventWrite / ShouldFailStatusTransitionWrite 开关。

    /// <summary>设为 true 时 LogAlarmEvent 返回 false，模拟 DB 写入失败。</summary>
    public bool ShouldFailAlarmEventWrite { get; set; }

    /// <summary>设为 true 时 LogStatusTransition 返回 false，模拟 DB 写入失败。</summary>
    public bool ShouldFailStatusTransitionWrite { get; set; }

    /// <summary>
    /// 设置后 LogProduction 将抛出指定异常（而非静默成功），用于模拟数据库异常逃逸到
    /// PlcDataAcquisitionService.PollingLoopAsync 外层 catch 的场景，
    /// 验证外层 catch 不调 MarkDisconnected（避免业务异常误判为 PLC 断连）。
    /// </summary>
    public Exception? LogProductionException { get; set; }

    public void LogProduction(ProductionLog log)
    {
        if (LogProductionException is not null)
            throw LogProductionException;
        // 复制一份加入，避免调用方修改同一对象实例导致历史数据被污染
        var copy = new ProductionLog
        {
            DeviceId = log.DeviceId,
            DeviceName = log.DeviceName,
            ShiftName = log.ShiftName,
            OkProduction = log.OkProduction,
            NgProduction = log.NgProduction,
            StatusWord = log.StatusWord,
            Timestamp = log.Timestamp
        };
        lock (_lock) ProductionLogs.Add(copy);
    }

    public bool LogAlarmEvent(string deviceId, string deviceName, string alarmId,
        string alarmName, string plcAddress, AlarmEventType eventType, DateTime eventTime,
        string? shiftName = null)
    {
        if (ShouldFailAlarmEventWrite) return false;
        var rec = new AlarmEventRecord
        {
            DeviceId = deviceId,
            DeviceName = deviceName,
            AlarmId = alarmId,
            AlarmName = alarmName,
            PlcAddress = plcAddress,
            EventType = eventType,
            EventTime = eventTime,
            ShiftName = shiftName ?? string.Empty
        };
        lock (_lock) AlarmEvents.Add(rec);
        return true;
    }

    public bool LogStatusTransition(string deviceId, string deviceName,
        int previousState, int currentState, DateTime eventTime,
        string? shiftName = null)
    {
        if (ShouldFailStatusTransitionWrite) return false;
        var rec = new StatusTransitionRecord
        {
            DeviceId = deviceId,
            DeviceName = deviceName,
            PreviousState = previousState,
            CurrentState = currentState,
            EventTime = eventTime,
            ShiftName = shiftName ?? string.Empty
        };
        lock (_lock) StatusTransitions.Add(rec);
        return true;
    }
}
