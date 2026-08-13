using System.Collections.Generic;
using Kanban.Contracts.Dtos;
using Kanban.Core.Models;
using Microsoft.Extensions.Logging;

namespace Kanban.Core.Services;

/// <summary>
/// 设备状态字追踪器：拥有 <see cref="_prevStatusWords"/> 字典及其专用锁。职责：
/// - <see cref="ReadAndUpdate"/>：PLC 读取状态字后检测状态转换并落库（OEE 历史回溯核心数据）
/// - <see cref="LogOfflineTransition"/>：软件停机或 PLC 断线时写入 CurrentState=0 离线转换
/// - <see cref="RemoveDevice"/>：设备删除时清理 _prevStatusWords 中的 key
/// - <see cref="ResetAll"/>：班次切换时清空全部状态字记录
/// </summary>
internal sealed class DeviceStatusTracker
{
    /// <summary>
    /// 记录每个设备上一次读取的 StatusWord，用于检测状态转换。
    /// key 为 Device.Id，value 为上次读取的 StatusWord（1=运行, 2=报警, 3=待机）。
    /// 不存在 key 表示首次读取或班次切换后尚未初始化。
    /// </summary>
    private readonly Dictionary<string, int> _prevStatusWords = new();

    private readonly object _lock = new();

    /// <summary>
    /// 接收 PLC 读取到的设备新状态字，检测状态转换并落库。
    /// 写入失败时不更新 _prevStatusWords，下轮重试，避免丢失转换记录导致 OEE 时长永久偏差。
    /// 返回值与原 PlcDataAcquisitionService.TryReadStatusWord 的语义一致（仅做状态转换追踪，不做 PLC 读取）。
    /// </summary>
    /// <param name="device">目标设备。</param>
    /// <param name="newStatus">本轮 PLC 读取到的状态字。</param>
    /// <param name="historyService">历史服务，用于写入状态转换记录。</param>
    /// <param name="shiftName">当前班次名称，附带到状态转换记录。</param>
    /// <returns>true=已更新内存状态；false=DB 写入失败或设备已被删除。</returns>
    /// <remarks>
    /// DB 写入移到锁外执行：锁内仅读取 prevStatus 并判断是否需要写转换记录，
    /// 锁外执行 LogStatusTransition（网络/磁盘 IO），完成后再加锁更新 _prevStatusWords。
    /// 避免持锁期间阻塞 UI 线程的 RemoveDevice（低频删设备）。
    /// 写入失败时不更新 _prevStatusWords，下轮重试，保证转换记录不丢失。
    /// </remarks>
    internal bool ReadAndUpdate(Device device, int newStatus, IStatusTransitionHistoryService historyService, string shiftName, ILogger logger, Action<StatusEventDto>? onStatusEdge = null)
    {
        // 归一化：非 1/2/3 的值统一视为 0（初始/离线），避免 0→65535 等不同未知值间的
        // 虚假转换日志（如通信抖动导致的非法值）。
        newStatus = NormalizeStatus(newStatus);

        // ── 锁内：读取 prevStatus + 决定是否需要写转换记录 ──
        bool hasPrev;
        int prevStatus;
        lock (_lock)
        {
            hasPrev = _prevStatusWords.TryGetValue(device.Id, out prevStatus);
        }

        // 无状态变化时不写 DB
        if (hasPrev && prevStatus == newStatus)
        {
            // 状态未变，直接更新（虽然值相同，保持一致性）
            lock (_lock)
            {
                if (_prevStatusWords.ContainsKey(device.Id))
                    _prevStatusWords[device.Id] = newStatus;
            }
            return true;
        }

        // ── 锁外：DB 写入 ──
        var effectivePrev = hasPrev ? prevStatus : 0;
        var eventTime = System.DateTime.Now;
        var writeOk = historyService.LogStatusTransition(
            device.Id, device.Name, effectivePrev, newStatus, eventTime, shiftName);
        if (!writeOk)
        {
            logger.LogWarning("设备 {Device} 状态转换写入失败（{Prev}→{New}），_prevStatusWords 暂不更新", device.Name, effectivePrev, newStatus);
            return false;
        }

        logger.LogInformation("设备 {Device} 状态转换 {Prev}→{New}",
            device.Name, GetStateText(effectivePrev), GetStateText(newStatus));

        // 状态边沿事件广播（修复 Remote 事件流缺口）：成功落库后推送。
        if (onStatusEdge != null)
        {
            try
            {
                onStatusEdge(new StatusEventDto
                {
                    DeviceId = device.Id,
                    DeviceName = device.Name,
                    PreviousState = (Kanban.Contracts.Enums.DeviceStatus)effectivePrev,
                    CurrentState = (Kanban.Contracts.Enums.DeviceStatus)newStatus,
                    EventTime = eventTime,
                    ShiftName = shiftName,
                });
            }
            catch (System.Exception ex)
            {
                logger.LogWarning(ex, "状态转换事件广播失败（设备={Device}）", device.Name);
            }
        }

        // ── 锁内：更新 _prevStatusWords ──
        lock (_lock)
        {
            // 若 UI 在锁外期间删除了该设备，跳过更新避免复活已删除的 key
            if (_prevStatusWords.ContainsKey(device.Id) || !hasPrev)
                _prevStatusWords[device.Id] = newStatus;
        }
        return true;
    }

    /// <summary>
    /// 将设备标记为"离线"：写入一条 CurrentState=0 的状态转换（仅当设备当前处于真实状态 1/2/3 时）。
    /// 用于软件停机或 PLC 断线场景：停机时段在 OEE 历史回溯中不被计入任何状态时长
    /// （AccumulateState 忽略 state=0），避免停机前的状态（往往是运行）被延续计入导致 OEE 虚高。
    /// 写入后将 _prevStatusWords 置 0，重连后真实状态读取会自然写入 0→真实状态 转换，不会重复刷写。
    /// 若设备已处于离线(0)或从未有过状态记录，则跳过。
    /// </summary>
    /// <remarks>
    /// DB 写入移到锁外执行：锁内读取 prev 判断是否需要写离线转换，锁外执行 LogStatusTransition，
    /// 完成后再加锁更新 _prevStatusWords。避免持锁期间阻塞 UI 线程的 RemoveDevice。
    /// </remarks>
    internal void LogOfflineTransition(Device device, IStatusTransitionHistoryService historyService, string shiftName, ILogger logger, Action<StatusEventDto>? onStatusEdge = null)
    {
        // ── 锁内：读取 prev 判断是否需要写离线转换 ──
        bool shouldWrite;
        int prev;
        lock (_lock)
        {
            shouldWrite = _prevStatusWords.TryGetValue(device.Id, out prev) && prev != (int)DeviceStatus.Unknown;
        }

        if (!shouldWrite) return;

        // ── 锁外：DB 写入 ──
        var writeOk = historyService.LogStatusTransition(device.Id, device.Name, prev, 0, System.DateTime.Now, shiftName);
        if (!writeOk)
        {
            logger.LogWarning("设备 {Device} 离线状态转换写入失败，_prevStatusWords 暂不更新", device.Name);
            return;
        }

        // 离线状态事件广播
        if (onStatusEdge != null)
        {
            try
            {
                onStatusEdge(new StatusEventDto
                {
                    DeviceId = device.Id,
                    DeviceName = device.Name,
                    PreviousState = (Kanban.Contracts.Enums.DeviceStatus)prev,
                    CurrentState = Kanban.Contracts.Enums.DeviceStatus.Unknown,
                    EventTime = System.DateTime.Now,
                    ShiftName = shiftName,
                });
            }
            catch (System.Exception ex)
            {
                logger.LogWarning(ex, "离线状态事件广播失败（设备={Device}）", device.Name);
            }
        }

        // ── 锁内：更新 _prevStatusWords ──
        lock (_lock)
        {
            // 若 UI 在锁外期间删除了该设备，跳过更新避免复活已删除的 key
            if (_prevStatusWords.ContainsKey(device.Id))
                _prevStatusWords[device.Id] = (int)DeviceStatus.Unknown;
        }
    }

    /// <summary>清理已删除设备的状态字追踪记录。</summary>
    internal void RemoveDevice(string deviceId)
    {
        lock (_lock)
            _prevStatusWords.Remove(deviceId);
    }

    /// <summary>班次切换时清空全部状态字记录。</summary>
    internal void ResetAll()
    {
        lock (_lock)
            _prevStatusWords.Clear();
    }

    /// <summary>
    /// 将状态字转换为可读文本，用于业务事件日志。
    /// 1=运行, 2=报警, 3=待机, 0/其他=初始
    /// </summary>
    private static string GetStateText(int state) => state switch
    {
        (int)DeviceStatus.Running => "运行",
        (int)DeviceStatus.Alarm => "报警",
        (int)DeviceStatus.Paused => "待机",
        _ => "初始"
    };

    /// <summary>
    /// 归一化状态字：非 1/2/3 的值统一归为 0（初始/离线）。
    /// 避免通信抖动等导致的非法值（如 65535）与 0 之间产生虚假转换日志。
    /// </summary>
    private static int NormalizeStatus(int status) => status switch
    {
        (int)DeviceStatus.Running => status,
        (int)DeviceStatus.Alarm => status,
        (int)DeviceStatus.Paused => status,
        _ => (int)DeviceStatus.Unknown,
    };

    // ──────────── 测试访问助手 ────────────

    internal IReadOnlyDictionary<string, int> GetPrevStatusWordsSnapshot()
    {
        lock (_lock)
            return _prevStatusWords.ToDictionary(kv => kv.Key, kv => kv.Value);
    }
}
