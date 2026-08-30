using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Models;
using MainAPP.Helpers;
using MainAPP.Resources;

namespace MainAPP.ViewModels;

/// <summary>
/// 设备管理器的「设备列表」子 ViewModel：只负责列表展示侧的搜索/状态筛选/摘要与过滤视图。
/// 设备 CRUD、SelectedDevice、保存与脏标记等编辑侧逻辑仍由 DeviceManagerViewModel 协调者承担。
/// </summary>
public partial class DeviceListViewModel : ObservableObject, IDisposable
{
    private readonly DeviceRepository _deviceRepository;

    public ICollectionView FilteredDevices { get; }

    [ObservableProperty]
    private string _searchKeyword = string.Empty;

    [ObservableProperty]
    private DeviceStatusFilter _statusFilter = DeviceStatusFilter.All;

    public bool HasDevices => _deviceRepository.Devices.Count > 0;
    public bool HasFilteredDevices => FilteredDevices.Cast<object>().Any();
    public bool IsFiltering => !string.IsNullOrWhiteSpace(SearchKeyword) || StatusFilter != DeviceStatusFilter.All;

    /// <summary>状态筛选下拉选项（全部 / 运行 / 报警 / 待机 / 离线）。</summary>
    public IReadOnlyList<StatusFilterOption> StatusFilterOptions { get; } = [
        new() { Value = DeviceStatusFilter.All, Label = Strings.M040 },
        new() { Value = DeviceStatusFilter.Running, Label = Strings.Status_Running },
        new() { Value = DeviceStatusFilter.Alarm, Label = Strings.Status_Alarm },
        new() { Value = DeviceStatusFilter.Paused, Label = Strings.Status_Paused },
        new() { Value = DeviceStatusFilter.Offline, Label = Strings.M165 },
    ];

    /// <summary>设备列表摘要：总数及各运行状态数量。</summary>
    public string DeviceSummaryText
    {
        get
        {
            var running = 0;
            var alarm = 0;
            var paused = 0;
            var initial = 0;
            foreach (var device in _deviceRepository.Devices)
            {
                var status = _deviceRepository.RuntimeMap.TryGetValue(device.Id, out var runtime)
                    ? runtime.StatusWord
                    : (int)DeviceStatus.Offline;
                switch (status)
                {
                    case (int)DeviceStatus.Running: running++; break;
                    case (int)DeviceStatus.Alarm: alarm++; break;
                    case (int)DeviceStatus.Paused: paused++; break;
                    default: initial++; break;
                }
            }

            return string.Format(Strings.F023, _deviceRepository.Devices.Count, running, alarm, paused, initial);
        }
    }

    public DeviceListViewModel(DeviceRepository deviceRepository)
    {
        _deviceRepository = deviceRepository;
        FilteredDevices = CollectionViewSource.GetDefaultView(_deviceRepository.Devices);

        _deviceRepository.Devices.CollectionChanged += OnDevicesCollectionChanged;
        _deviceRepository.Runtimes.CollectionChanged += OnRuntimesCollectionChanged;
        foreach (var rt in _deviceRepository.Runtimes) AttachRuntime(rt);
    }

    partial void OnSearchKeywordChanged(string value)
    {
        ApplyFilter();
        NotifyFilterState();
    }

    partial void OnStatusFilterChanged(DeviceStatusFilter value)
    {
        ApplyFilter();
        NotifyFilterState();
    }

    /// <summary>强制刷新设备列表视图（LoadAll 后 CollectionView 可能未立即响应 Reset + Add 序列）。</summary>
    public void RefreshDeviceList()
    {
        UiDispatcher.Dispatch(() =>
        {
            FilteredDevices.Refresh();
            NotifyFilterState();
            Serilog.Log.Information("DeviceListViewModel.RefreshDeviceList：Devices.Count={Count}, FilteredDevices.Filter={Filter}",
                _deviceRepository.Devices.Count, FilteredDevices.Filter == null ? "null" : "set");
        });
    }

    private void ApplyFilter()
    {
        var keyword = (SearchKeyword ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(keyword) && StatusFilter == DeviceStatusFilter.All)
        {
            FilteredDevices.Filter = null;
            return;
        }

        var filter = StatusFilter;
        FilteredDevices.Filter = item => item is Device d
            && (string.IsNullOrEmpty(keyword) || DeviceMatchesKeyword(d, keyword))
            && StatusMatches(d, filter);
    }

    private static bool DeviceMatchesKeyword(Device d, string keyword)
    {
        // Id 是随机 GUID，不能用子串匹配（字母数字噪声太大）；支持等值或 ≥8 位前缀定位
        var id = d.Id ?? string.Empty;
        if (string.Equals(id, keyword, System.StringComparison.OrdinalIgnoreCase)
            || (keyword.Length >= 8 && id.StartsWith(keyword, System.StringComparison.OrdinalIgnoreCase))) return true;
        if ((d.Name ?? string.Empty).Contains(keyword, System.StringComparison.OrdinalIgnoreCase)) return true;
        if ((d.OkCountAddress ?? string.Empty).Contains(keyword, System.StringComparison.OrdinalIgnoreCase)) return true;
        if ((d.NgCountAddress ?? string.Empty).Contains(keyword, System.StringComparison.OrdinalIgnoreCase)) return true;
        if ((d.StatusCountAddress ?? string.Empty).Contains(keyword, System.StringComparison.OrdinalIgnoreCase)) return true;
        if ((d.ProductionResetAddress ?? string.Empty).Contains(keyword, System.StringComparison.OrdinalIgnoreCase)) return true;
        if ((d.RecipeAddress ?? string.Empty).Contains(keyword, System.StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var a in d.Alarms)
        {
            if ((a.Name ?? string.Empty).Contains(keyword, System.StringComparison.OrdinalIgnoreCase)) return true;
            if ((a.PlcAddress ?? string.Empty).Contains(keyword, System.StringComparison.OrdinalIgnoreCase)) return true;
        }
        foreach (var def in d.Defects)
        {
            if ((def.Name ?? string.Empty).Contains(keyword, System.StringComparison.OrdinalIgnoreCase)) return true;
            if ((def.PlcAddress ?? string.Empty).Contains(keyword, System.StringComparison.OrdinalIgnoreCase)) return true;
        }
        foreach (var c in d.CounterAlarms)
        {
            if ((c.Name ?? string.Empty).Contains(keyword, System.StringComparison.OrdinalIgnoreCase)) return true;
            if ((c.PlcAddress ?? string.Empty).Contains(keyword, System.StringComparison.OrdinalIgnoreCase)) return true;
        }
        foreach (var source in d.Sources)
        {
            if ((source.Name ?? string.Empty).Contains(keyword, System.StringComparison.OrdinalIgnoreCase)) return true;
            if ((source.Type ?? string.Empty).Contains(keyword, System.StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var value in source.Values)
            {
                if ((value.Name ?? string.Empty).Contains(keyword, System.StringComparison.OrdinalIgnoreCase)) return true;
                if ((value.PlcAddress ?? string.Empty).Contains(keyword, System.StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }

    private bool StatusMatches(Device d, DeviceStatusFilter filter)
    {
        if (filter == DeviceStatusFilter.All) return true;
        int status = (int)DeviceStatus.Offline;
        if (_deviceRepository.RuntimeMap.TryGetValue(d.Id, out var rt))
            status = rt.StatusWord;
        return filter switch
        {
            DeviceStatusFilter.Running => status == (int)DeviceStatus.Running,
            DeviceStatusFilter.Alarm => status == (int)DeviceStatus.Alarm,
            DeviceStatusFilter.Paused => status == (int)DeviceStatus.Paused,
            DeviceStatusFilter.Offline => status == (int)DeviceStatus.Offline,
            _ => true,
        };
    }

    private void OnDevicesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UiDispatcher.Dispatch(() =>
        {
            FilteredDevices.Refresh();
            NotifyFilterState();
            OnPropertyChanged(nameof(DeviceSummaryText));
        });
    }

    private void NotifyFilterState()
    {
        OnPropertyChanged(nameof(HasDevices));
        OnPropertyChanged(nameof(HasFilteredDevices));
        OnPropertyChanged(nameof(IsFiltering));
    }

    private void OnRuntimesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (DeviceRuntime rt in e.NewItems) AttachRuntime(rt);
        if (e.OldItems != null)
            foreach (DeviceRuntime rt in e.OldItems) DetachRuntime(rt);
        UiDispatcher.Dispatch(() => OnPropertyChanged(nameof(DeviceSummaryText)));
    }

    private void AttachRuntime(DeviceRuntime rt) => rt.PropertyChanged += OnRuntimePropertyChanged;

    private void DetachRuntime(DeviceRuntime rt) => rt.PropertyChanged -= OnRuntimePropertyChanged;

    private void OnRuntimePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DeviceRuntime.StatusWord)) return;
        UiDispatcher.Dispatch(() =>
        {
            OnPropertyChanged(nameof(DeviceSummaryText));
            if (StatusFilter != DeviceStatusFilter.All)
                FilteredDevices.Refresh();
        });
    }

    public void Dispose()
    {
        _deviceRepository.Devices.CollectionChanged -= OnDevicesCollectionChanged;
        _deviceRepository.Runtimes.CollectionChanged -= OnRuntimesCollectionChanged;
        foreach (var rt in _deviceRepository.Runtimes)
            DetachRuntime(rt);
    }
}
