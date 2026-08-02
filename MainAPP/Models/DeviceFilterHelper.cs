using System.Collections.ObjectModel;
using System.Linq;

namespace MainAPP.Models;

/// <summary>
/// 设备筛选列表辅助：从 DeviceRepository 同步刷新 DeviceFilterItems。
/// 供 HomeViewModel / HistoryQueryViewModel 共用，消除重复代码。
/// </summary>
public static class DeviceFilterHelper
{
    public static void Refresh(ObservableCollection<DeviceFilterItem> items, Data.DeviceRepository repo)
    {
        items.Clear();
        foreach (var d in repo.Devices)
            items.Add(new DeviceFilterItem(d.Id, d.Name));
    }

    /// <summary>
    /// 删除设备后回退选中：若当前选中设备不再存在，选第一个（或清空）。
    /// 返回应设置的 SelectedDeviceId（null 表示清空）。
    /// </summary>
    public static string? FallbackSelected(Data.DeviceRepository repo, string? currentId)
    {
        if (currentId != null && !repo.Devices.Any(d => d.Id == currentId))
            return repo.Devices.Count > 0 ? repo.Devices[0].Id : null;
        return currentId;
    }
}
