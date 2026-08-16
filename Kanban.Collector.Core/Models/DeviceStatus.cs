namespace Kanban.Collector.Core.Models;

/// <summary>
/// 设备状态枚举。
/// StatusWord 定义：0=初始, 1=运行, 2=报警, 3=待机（等值判断，非位掩码）。
/// 改为 enum 以便 EF Core 用 HasConversion&lt;int&gt;() 强约束合法值，并让 switch 做范围校验。
/// 枚举可隐式转换为 int，现有 int 类型的 StatusWord / CurrentState / PreviousState 字段无需改动。
/// </summary>
public enum DeviceStatus
{
    Unknown = 0,
    Running = 1,
    Alarm = 2,
    Paused = 3,
}
