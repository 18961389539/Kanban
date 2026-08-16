using Kanban.Collector.Core.Services;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MainAPP.Services;

/// <summary>
/// 跨页面共享的当前选中设备服务。
/// Home 与产线页都注入并双向同步 SelectedDeviceId，
/// 取代原先 ProductionLineViewModel 直接依赖 HomeViewModel 造成的循环依赖。
/// </summary>
public interface IDeviceSelectionService : INotifyPropertyChanged
{
    /// <summary>当前选中设备 Id（null 表示未选中）。</summary>
    string? SelectedDeviceId { get; set; }
}

/// <summary>
/// IDeviceSelectionService 默认实现。基于 CommunityToolkit ObservableObject，
/// SelectedDeviceId 值相等时不触发 PropertyChanged（天然防回环：VM 收到变更后
/// 写回 service，因值相同而不重复触发，避免无限循环）。
/// </summary>
public partial class DeviceSelectionService : ObservableObject, IDeviceSelectionService
{
    [ObservableProperty]
    private string? _selectedDeviceId;
}
