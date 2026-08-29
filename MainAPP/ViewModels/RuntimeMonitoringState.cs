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
        DeviceStatusWord.Offline => Strings.Status_Initial,
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
    public static void Synchronize(
        ObservableCollection<DeviceAcquisitionStatusItem> target,
        IReadOnlyList<DeviceAcquisitionStatusItem> desired)
    {
        var existing = target.ToDictionary(item => item.DeviceName, StringComparer.Ordinal);
        var activeNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in desired)
        {
            activeNames.Add(item.DeviceName);
            if (existing.TryGetValue(item.DeviceName, out var current))
            {
                current.StatusText = item.StatusText;
                current.AcquisitionText = item.AcquisitionText;
                current.ConfiguredAddressCount = item.ConfiguredAddressCount;
                current.OkProduction = item.OkProduction;
                current.NgProduction = item.NgProduction;
            }
            else
            {
                target.Add(item);
            }
        }

        for (var index = target.Count - 1; index >= 0; index--)
        {
            if (!activeNames.Contains(target[index].DeviceName))
                target.RemoveAt(index);
        }
    }
}
