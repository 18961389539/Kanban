namespace MainAPP.Models;

/// <summary>
/// 设备筛选下拉项（Id 为设备 Id；不再使用 null 表示"全部设备"）。
/// </summary>
public record DeviceFilterItem(string? Id, string Name);
