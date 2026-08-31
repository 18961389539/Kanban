using System.Collections.ObjectModel;
using Kanban.Collector.Core.Models;
using MainAPP.Models;
using OxyPlot;
using OxyPlot.Axes;
using MainAPP.Resources;

namespace MainAPP.ViewModels;

internal static class RuntimeDeviceStatusText
{
    public static string Format(int statusWord) => (DeviceStatusWord)statusWord switch
    {
        DeviceStatusWord.Running => Strings.Status_Running,
        DeviceStatusWord.Alarm => Strings.Status_Alarm,
        DeviceStatusWord.Standby => Strings.Status_Paused,
        DeviceStatusWord.Offline => Strings.Status_Offline,
        _ => string.Format(Strings.F164, statusWord),
    };
}

internal sealed class PollingTrendBuffer
{
    private readonly Queue<DataPoint> _points = new();

    public int Count => _points.Count;

    public IReadOnlyList<DataPoint> Add(DateTime timestamp, long milliseconds)
    {
        _points.Enqueue(new DataPoint(DateTimeAxis.ToDouble(timestamp), milliseconds));
        while (_points.Count > 60)
            _points.Dequeue();
        return _points.ToArray();
    }

    public void Clear() => _points.Clear();
}

internal static class DeviceStatusCollectionSynchronizer
{
    /// <summary>
    /// 按设备 Id 增量同步（复用已有条目实例，避免整表重建引起列表闪烁与选中丢失）。
    /// 键必须用 DeviceId 而非 DeviceName：设备重名时 ToDictionary(DeviceName) 抛
    /// ArgumentException，而本方法在 1s 刷新定时器回调里被调用，异常直通 Dispatcher 会终止进程。
    /// 建索引时对重复键取"后者覆盖"而不是抛异常——重复 Id 属配置错误，由配置校验负责报出，
    /// 不该让运行监控页崩溃。
    /// </summary>
    public static void Synchronize(
        ObservableCollection<DeviceAcquisitionStatusItem> target,
        IReadOnlyList<DeviceAcquisitionStatusItem> desired)
    {
        var existing = new Dictionary<string, DeviceAcquisitionStatusItem>(desired.Count, StringComparer.Ordinal);
        foreach (var item in target)
            existing[GetKey(item)] = item;

        var activeKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in desired)
        {
            var key = GetKey(item);
            activeKeys.Add(key);
            if (existing.TryGetValue(key, out var current))
            {
                current.DeviceName = item.DeviceName; // 设备改名后列表同步显示新名称
                current.StatusText = item.StatusText;
                current.AcquisitionText = item.AcquisitionText;
                current.ConfiguredAddressCount = item.ConfiguredAddressCount;
                current.OkProduction = item.OkProduction;
                current.NgProduction = item.NgProduction;
            }
            else
            {
                target.Add(item);
                existing[key] = item; // 立即入索引：desired 内部重键时不会重复 Add
            }
        }

        // 倒序删除：既移除已下线设备，也顺带去掉同键的重复行（保留末条，与 existing 索引一致）
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = target.Count - 1; index >= 0; index--)
        {
            var key = GetKey(target[index]);
            if (!activeKeys.Contains(key) || !seen.Add(key))
                target.RemoveAt(index);
        }
    }

    /// <summary>同步键：优先设备 Id；Id 缺失（如旧版 Collector 未下发 DeviceId）时回退到设备名并加前缀，
    /// 保证退化场景仍能精确匹配，而不是退化成"每分钟整表重建"。</summary>
    private static string GetKey(DeviceAcquisitionStatusItem item)
        => string.IsNullOrWhiteSpace(item.DeviceId)
            ? "name:" + item.DeviceName
            : "id:" + item.DeviceId;
}
