using System.Collections.ObjectModel;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Models;
using MainAPP.Helpers;
using MainAPP.Services;

namespace MainAPP.ViewModels;

/// <summary>主页活跃报警刷新结果。</summary>
/// <param name="HasHighLevelAlarm">是否存在 High 级别报警（供标题徽章提示）。</param>
/// <param name="TotalActiveCount">截断前的活跃报警总数（供截断提示与计数徽章）。</param>
public readonly record struct HomeAlarmRefreshResult(bool HasHighLevelAlarm, int TotalActiveCount);

/// <summary>
/// 主页实时故障采集器：从指定设备快照收集活跃报警（PLC 边沿 + 计数阈值 + 活跃状态表数据源报警），
/// 计数报警优先使用 <see cref="CounterAlarm.StartTime"/>，恢复侧保留 10 秒去抖，并差分更新目标集合。
/// </summary>
public sealed class HomeAlarmCollector
{
    private const double DebounceSeconds = 10;

    /// <summary>计数报警触发时刻缓存：恢复去抖期间 StartTime 已清零，仍用此值展示持续时间。</summary>
    private readonly Dictionary<string, DateTime> _triggerTimes = new();
    private readonly Dictionary<string, DateTime> _recoveryTimes = new();

    public HomeAlarmRefreshResult Refresh(
        ObservableCollection<ActiveAlarmInfo> target,
        IReadOnlyList<Device> devices,
        DateTime now,
        bool isMuted,
        int maxAlarms,
        string? selectedDeviceId = null,
        IReadOnlyList<ActiveAlarmStateRecord>? activeSourceStates = null,
        IDeviceRepository? deviceRepository = null)
    {
        var desired = BuildDesired(devices, now, selectedDeviceId, activeSourceStates, deviceRepository);

        desired.Sort((a, b) =>
        {
            var levelCmp = b.Level.CompareTo(a.Level);
            return levelCmp != 0 ? levelCmp : a.EventTime.CompareTo(b.EventTime);
        });

        var totalActiveCount = desired.Count;

        if (desired.Count > maxAlarms)
            desired.RemoveRange(maxAlarms, desired.Count - maxAlarms);

        // 既有项按身份键建索引（一次 O(N)），替代原先 FirstOrDefault / Any 两个 O(N²) 循环。
        // 键含 EventTime：静止报警直接复用既有实例 → Sync 引用差分零集合事件，
        // 虚拟化容器不重建、滚动位置保留（原先是把 IsNew/AddedAt 拷贝到新对象再全量替换，
        // 引用必然失配，差分从未生效，每次刷新等于 Clear+AddRange）。
        // IsNew/AddedAt 状态天然随实例保留，DurationText 由 HomeViewModel.SyncRuntime
        // 每 tick 对全集合 RefreshDuration 原地刷新（INPC 驱动 UI）。
        var existingByKey = new Dictionary<(string, string, AlarmLevel, AlarmKind, DateTime, string?, string?, string?), ActiveAlarmInfo>(target.Count);
        foreach (var item in target)
        {
            var key = (item.DeviceId, item.AlarmName, item.Level, item.Kind, item.EventTime,
                item.AlarmNameEn, item.AlarmNameJa, item.AlarmNamePt);
            if (!existingByKey.ContainsKey(key))
                existingByKey[key] = item;
        }

        for (var i = 0; i < desired.Count; i++)
        {
            var key = (desired[i].DeviceId, desired[i].AlarmName, desired[i].Level, desired[i].Kind, desired[i].EventTime,
                desired[i].AlarmNameEn, desired[i].AlarmNameJa, desired[i].AlarmNamePt);
            if (existingByKey.TryGetValue(key, out var existing))
            {
                desired[i] = existing;
            }
            else
            {
                // 新报警（或重新触发导致 EventTime 变化）：重新进入 IsNew 高亮（30s 后由 SyncRuntime 清除）
                desired[i].IsNew = !isMuted;
                desired[i].AddedAt = now;
                desired[i].RefreshDuration(now);
            }
        }

        ObservableCollectionSyncHelper.Sync(target, desired);
        return new HomeAlarmRefreshResult(target.Any(a => a.Level == AlarmLevel.High), totalActiveCount);
    }

    private List<ActiveAlarmInfo> BuildDesired(
        IReadOnlyList<Device> devices,
        DateTime now,
        string? selectedDeviceId,
        IReadOnlyList<ActiveAlarmStateRecord>? activeSourceStates,
        IDeviceRepository? deviceRepository)
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
                    desired.Add(new ActiveAlarmInfo(alarm.StartTime, device.Id, device.Name, alarm.Name, alarm.Level, AlarmKind.Plc,
                        alarm.NameEn, alarm.NameJa, alarm.NamePt));
                }
            }

            foreach (var ca in device.CounterAlarms)
            {
                var key = $"{device.Id}_{ca.Id}";
                if (ca.Enabled && ca.IsTriggered)
                {
                    activeKeys.Add(key);
                    _recoveryTimes.Remove(key);
                    if (ca.StartTime != default)
                        _triggerTimes[key] = ca.StartTime;
                    else if (!_triggerTimes.ContainsKey(key))
                        _triggerTimes[key] = now;

                    desired.Add(new ActiveAlarmInfo(_triggerTimes[key], device.Id, device.Name, ca.Name, AlarmLevel.Medium, AlarmKind.Count,
                        ca.NameEn, ca.NameJa, ca.NamePt));
                }
                else if (ca.Enabled && _triggerTimes.ContainsKey(key))
                {
                    if (!_recoveryTimes.ContainsKey(key))
                        _recoveryTimes[key] = now;
                    if ((now - _recoveryTimes[key]).TotalSeconds < DebounceSeconds)
                    {
                        activeKeys.Add(key);
                        desired.Add(new ActiveAlarmInfo(_triggerTimes[key], device.Id, device.Name, ca.Name, AlarmLevel.Medium, AlarmKind.Count,
                            ca.NameEn, ca.NameJa, ca.NamePt));
                    }
                }
            }
        }

        if (activeSourceStates != null && deviceRepository != null)
        {
            foreach (var record in activeSourceStates)
            {
                if (selectedDeviceId != null
                    && !string.Equals(record.DeviceId, selectedDeviceId, StringComparison.OrdinalIgnoreCase))
                    continue;

                var (nameEn, nameJa, namePt) = AlarmCenterDisplayHelper.ResolveEventLocalizedFields(
                    deviceRepository, record.DeviceId, record.AlarmId);
                desired.Add(new ActiveAlarmInfo(
                    record.TriggeredAt, record.DeviceId, record.DeviceName, record.AlarmName,
                    AlarmLevel.Medium, AlarmKind.DataSource, nameEn, nameJa, namePt));
            }
        }

        foreach (var key in _triggerTimes.Keys.Where(k => !activeKeys.Contains(k)).ToList())
        {
            _triggerTimes.Remove(key);
            _recoveryTimes.Remove(key);
        }

        foreach (var key in _recoveryTimes
                     .Where(kv => (now - kv.Value).TotalSeconds >= DebounceSeconds)
                     .Select(kv => kv.Key).ToList())
            _recoveryTimes.Remove(key);

        return desired;
    }
}
