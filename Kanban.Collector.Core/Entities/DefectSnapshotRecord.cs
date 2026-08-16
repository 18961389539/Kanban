using Kanban.Collector.Core.Models;

namespace Kanban.Collector.Core.Entities;

/// <summary>
/// 缺陷计数历史快照。Count 为设备/班次内累计值，复盘时按缺陷和班次实例做差分。
/// </summary>
public class DefectSnapshotRecord
{
    public int Id { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string DefectId { get; set; } = string.Empty;
    public string DefectName { get; set; } = string.Empty;
    public DefectSeverity Severity { get; set; }
    public DefectCategory Category { get; set; }
    public string ShiftName { get; set; } = string.Empty;
    public int Count { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;
}
