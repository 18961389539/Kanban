using System.Collections.Specialized;
using System.ComponentModel;
using Kanban.Core.Models;

namespace MainAPP.ViewModels;

/// <summary>
/// 设备配置脏标记追踪器：订阅设备及其报警/缺陷/计数报警子项的属性/集合变更，
/// 排除运行时字段（采集线程写入）后触发脏标记回调；支持保存期间的临时抑制。
/// 从 DeviceManagerViewModel 抽出，供其它编辑型 ViewModel 复用。
/// </summary>
public sealed class DirtyTracker
{
    private static readonly HashSet<string> RuntimeProperties = new(StringComparer.Ordinal)
    {
        nameof(Alarm.StartTime),
        nameof(Alarm.EndTime),
        nameof(Defect.Count),
        nameof(CounterAlarm.CurrentValue),
        nameof(CounterAlarm.IsTriggered),
    };

    private readonly Action _markDirty;
    private bool _suppressDirty;

    public DirtyTracker(Action markDirty) => _markDirty = markDirty;

    /// <summary>保存期间临时抑制脏标记（SaveAll 回填子项 DeviceId 会触发属性变更）。</summary>
    public bool IsSuppressed
    {
        get => _suppressDirty;
        set => _suppressDirty = value;
    }

    public void MarkDirty()
    {
        if (_suppressDirty) return;
        _markDirty();
    }

    public void AttachDevice(Device d)
    {
        d.PropertyChanged += OnDevicePropertyChanged;
        d.Alarms.CollectionChanged += OnChildCollectionChanged;
        d.Defects.CollectionChanged += OnChildCollectionChanged;
        d.CounterAlarms.CollectionChanged += OnChildCollectionChanged;
        foreach (var a in d.Alarms) a.PropertyChanged += OnChildItemPropertyChanged;
        foreach (var def in d.Defects) def.PropertyChanged += OnChildItemPropertyChanged;
        foreach (var c in d.CounterAlarms) c.PropertyChanged += OnChildItemPropertyChanged;
    }

    public void DetachDevice(Device d)
    {
        d.PropertyChanged -= OnDevicePropertyChanged;
        d.Alarms.CollectionChanged -= OnChildCollectionChanged;
        d.Defects.CollectionChanged -= OnChildCollectionChanged;
        d.CounterAlarms.CollectionChanged -= OnChildCollectionChanged;
        foreach (var a in d.Alarms) a.PropertyChanged -= OnChildItemPropertyChanged;
        foreach (var def in d.Defects) def.PropertyChanged -= OnChildItemPropertyChanged;
        foreach (var c in d.CounterAlarms) c.PropertyChanged -= OnChildItemPropertyChanged;
    }

    public void DetachAll(IEnumerable<Device> devices)
    {
        foreach (var d in devices) DetachDevice(d);
    }

    private void OnDevicePropertyChanged(object? sender, PropertyChangedEventArgs e) => MarkDirty();

    private void OnChildCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (var item in e.NewItems)
                if (item is INotifyPropertyChanged np) np.PropertyChanged += OnChildItemPropertyChanged;
        if (e.OldItems != null)
            foreach (var item in e.OldItems)
                if (item is INotifyPropertyChanged np) np.PropertyChanged -= OnChildItemPropertyChanged;
        // 集合增删的脏标记由对应命令显式标记，此处仅维护事件订阅
    }

    private void OnChildItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 运行时字段（计数/当前值等）由采集线程写入，不计入未保存标记
        if (e.PropertyName != null && RuntimeProperties.Contains(e.PropertyName))
            return;
        MarkDirty();
    }
}
