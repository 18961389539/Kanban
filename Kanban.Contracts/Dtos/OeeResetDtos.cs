namespace Kanban.Contracts.Dtos;

/// <summary>
/// 「全部设备 OEE 清零」执行结果。
/// Triggered = 成功写入 PLC 清零触发位的设备数，Total = 参与清零的设备总数；
/// Triggered &lt; Total 通常表示部分设备未配置/未连接清零地址（软件侧清零仍对全部设备生效）。
/// </summary>
public sealed record OeeResetAllResultDto(
    int Triggered,
    int Total);
