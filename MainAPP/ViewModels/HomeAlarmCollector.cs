using System.Collections.ObjectModel;
using Kanban.Core.Models;
using MainAPP.Helpers;

namespace MainAPP.ViewModels;

/// <summary>
/// 主页实时故障采集器：从设备快照收集所有活跃报警（PLC 边沿 + 计数阈值），
/// 处理计数报警首次触发时间缓存与 10 秒恢复去抖，并差分更新目标集合。
/// 返回是否存在 High 级别报警（供标题徽章提示）。
/// </summary>
public sealed class HomeAlarmCollector
{
    private const double DebounceSeconds = 10;

    private readonly Dictionary<string, DateTime> _triggerTimes = new();
    private readonly Dictionary<string, DateTime> _recoveryTimes = new();

    public bool Refresh(
        ObservableCollection<ActiveAlarmInfo> target,
        IReadOnlyList<Device> devices,
        DateTime now,
        bool isMuted,
        int maxAlarms)
    {
        var desired = BuildDesired(devices, now);

        // 排序：级别降序 + 触发时间升序
        desired.Sort((a, b) =>
        {
            var levelCmp = b.Level.CompareTo(a.Level);
            return levelCmp != 0 ? levelCmp : a.EventTime.CompareTo(b.EventTime);
        });

        // 限制最大显示条数：截断后保留最关键/最新的报警
        if (desired.Count > maxAlarms)
            desired.RemoveRange(maxAlarms, desired.Count - maxAlarms);

        // 复用已有实例：保留 IsNew 状态连续性，避免每次新建导致引用不匹配、排序失效
        for (int i = 0; i < desired.Count; i++)
        {
            var existing = target.FirstOrDefault(a => a.Equals(desired[i]));
            if (existing != null)
                desired[i] = existing;
        }

        // 新加入的项标记 IsNew=true 触发闪烁并重置 AddedAt；已存在项保持原状态（静音时跳过）
        foreach (var item in desired)
        {
            if (!target.Contains(item, ReferenceEqualityComparer.Instance))
            {
                item.IsNew = !isMuted;
                item.AddedAt = now;
                item.RefreshDuration(now);
            }
        }

        ObservableCollectionSyncHelper.Sync(target, desired);
        return target.Any(a => a.Level == AlarmLevel.High);
    }

    private List<ActiveAlarmInfo> BuildDesired(IReadOnlyList<Device> devices, DateTime now)
    {
        List<ActiveAlarmInfo> desired = [];
        HashSet<string> activeKeys = [];

        foreach (var device in devices)
        {
            foreach (var alarm in device.Alarms)
            {
                if (alarm.StartTime != default && alarm.EndTime == default)
                {
                    activeKeys.Add($"{device.Id}_{alarm.Id}");
                    desired.Add(new ActiveAlarmInfo(alarm.StartTime, device.Name, alarm.Name, alarm.Level, AlarmKind.Plc));
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
                    desired.Add(new ActiveAlarmInfo(_triggerTimes[key], device.Name, ca.Name, AlarmLevel.Medium, AlarmKind.Count));
                }
                else if (ca.Enabled && _triggerTimes.ContainsKey(key))
                {
                    // 去抖：刚恢复（IsTriggered=false）但仍在去抖窗口内，继续显示
                    if (!_recoveryTimes.ContainsKey(key))
                        _recoveryTimes[key] = now;
                    if ((now - _recoveryTimes[key]).TotalSeconds < DebounceSeconds)
                    {
                        activeKeys.Add(key);
                        desired.Add(new ActiveAlarmInfo(_triggerTimes[key], device.Name, ca.Name, AlarmLevel.Medium, AlarmKind.Count));
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
