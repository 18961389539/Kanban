namespace Kanban.Contracts.Enums;

/// <summary>
/// 设备状态枚举（与 MainAPP.Models.DeviceStatus 数值一致，避免跨进程枚举错位）。
/// StatusWord 定义：0=离线, 1=运行, 2=报警, 3=待机（等值判断，非位掩码）。
/// </summary>
public enum DeviceStatus
{
    Offline = 0,
    Running = 1,
    Alarm = 2,
    Paused = 3,
}
