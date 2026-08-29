using System.Collections.ObjectModel;
using Kanban.Collector.Core.Models;
using MainAPP.Helpers;

namespace MainAPP.ViewModels;

/// <summary>主页活跃报警刷新结果。</summary>
/// <param name="HasHighLevelAlarm">是否存在 High 级别报警（供标题徽章提示）。</param>
/// <param name="TotalActiveCount">截断前的活跃报警总数（供截断提示与计数徽章）。</param>
public readonly record struct HomeAlarmRefreshResult(bool HasHighLevelAlarm, int TotalActiveCount);

/// <summary>
/// 主页实时故障采集器：从指定设备快照收集活跃报警（PLC 边沿 + 计数阈值），
/// 处理计数报警首次触发时间缓存与 10 秒恢复去抖，并差分更新目标集合。
/// </summary>
public sealed class HomeAlarmCollector
{
    private const double DebounceSeconds = 10;

    private readonly Dictionary<string, DateTime> _triggerTimes = new();
    private readonly Dictionary<string, DateTime> _recoveryTimes = new();

    public HomeAlarmRefreshResult Refresh(
        ObservableCollection<ActiveAlarmInfo> target,
        IReadOnlyList<Device> devices,
        DateTime now,
        bool isMuted,
        int maxAlarms,
        string? selectedDeviceId = null)
    {
        var desired = BuildDesired(devices, now, selectedDeviceId);

        // 排序：级别降序 + 触发时间升序
        desired.Sort((a, b) =>
        {
            var levelCmp = b.Level.CompareTo(a.Level);
            return levelCmp != 0 ? levelCmp : a.EventTime.CompareTo(b.EventTime);
        });

        var totalActiveCount = desired.Count;

        // 限制最大显示条数：截断后保留最关键/最新的报警
        if (desired.Count > maxAlarms)
            desired.RemoveRange(maxAlarms, desired.Count - maxAlarms);

        // 保留 IsNew/AddedAt 连续性，但不复用旧 ActiveAlarmInfo 实例——旧实例会快照过期的
        // NameEn（配置热更新或首次 tick 早于 LoadAll 完成时可能为空），导致 DisplayName 永久中文。
        for (int i = 0; i < desired.Count; i++)
        {
            var existing = target.FirstOrDefault(a => a.Equals(desired[i]));
            if (existing == null)
                continue;
            desired[i].IsNew = existing.IsNew;
            desired[i].AddedAt = existing.AddedAt;
        }

        // 新加入的项标记 IsNew=true 触发闪烁并重置 AddedAt；已存在项保持原状态（静音时跳过闪烁）
        foreach (var item in desired)
        {
            if (target.Any(a => a.Equals(item)))
                continue;
            item.IsNew = !isMuted;
            item.AddedAt = now;
            item.RefreshDuration(now);
        }

        ObservableCollectionSyncHelper.Sync(target, desired);
        return new HomeAlarmRefreshResult(target.Any(a => a.Level == AlarmLevel.High), totalActiveCount);
    }

    private List<ActiveAlarmInfo> BuildDesired(
        IReadOnlyList<Device> devices,
        DateTime now,
        string? selectedDeviceId)
    {
        List<ActiveAlarmInfo> desired = [];
        HashSet<string> activeKeys = [];

        foreach (var device in devices)
        {
            if (selectedDeviceId != null && !string.Equals(device.Id, selectedDeviceId, StringComparison.Ordinal))
                continue;

            foreach (var alarm in device.Alarms)
            {
                if (alarm.StartTime != default && alarm.EndTime == default)
                {
                    activeKeys.Add($"{device.Id}_{alarm.Id}");
                    desired.Add(new ActiveAlarmInfo(alarm.StartTime, device.Name, alarm.Name, alarm.Level, AlarmKind.Plc,
                        alarm.NameEn, alarm.NameJa, alarm.NamePt));
                }
            }

            foreach (var ca in device.CounterAlarms)
            {
                var key = $"{device.Id}_{ca.Id}";
                if (ca.Enabled && ca.IsTriggered)
                {
                    activeKeys.Add(key);
                    // 触发中：清除恢复缓存，首次发现触发时记录时刻
                    _recoveryTimes.Remove(key);
                    if (!_triggerTimes.ContainsKey(key))
                        _triggerTimes[key] = now;
                    // 计数报警无级别字段，统一视为 Medium
                    desired.Add(new ActiveAlarmInfo(_triggerTimes[key], device.Name, ca.Name, AlarmLevel.Medium, AlarmKind.Count,
                        ca.NameEn, ca.NameJa, ca.NamePt));
                }
                else if (ca.Enabled && _triggerTimes.ContainsKey(key))
                {
                    // 去抖：刚恢复（IsTriggered=false）但仍在去抖窗口内，继续显示
                    if (!_recoveryTimes.ContainsKey(key))
                        _recoveryTimes[key] = now;
                    if ((now - _recoveryTimes[key]).TotalSeconds < DebounceSeconds)
                    {
                        activeKeys.Add(key);
                        desired.Add(new ActiveAlarmInfo(_triggerTimes[key], device.Name, ca.Name, AlarmLevel.Medium, AlarmKind.Count,
                            ca.NameEn, ca.NameJa, ca.NamePt));
                    }
                }
            }
        }

        // 清除已恢复的计数报警时间记录
        var staleKeys = _triggerTimes.Keys.Where(k => !activeKeys.Contains(k)).ToList();
        foreach (var k in staleKeys)
        {
            _triggerTimes.Remove(k);
            _recoveryTimes.Remove(k);
        }
        // 清除去抖窗口已过期的恢复记录
        var expiredRecovery = _recoveryTimes
            .Where(kv => (now - kv.Value).TotalSeconds >= DebounceSeconds)
            .Select(kv => kv.Key).ToList();
        foreach (var k in expiredRecovery)
            _recoveryTimes.Remove(k);

        return desired;
    }
}
