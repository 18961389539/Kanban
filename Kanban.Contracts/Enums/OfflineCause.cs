namespace Kanban.Contracts.Enums;

/// <summary>
/// 离线原因。OEE 状态字仍为 <see cref="DeviceStatus.Offline"/> = 0；
/// 本枚举只解释「为什么是 0」，不参与可用率公式。
/// 非离线状态、旧库缺列或未知来源为 <see cref="None"/>。
/// </summary>
public enum OfflineCause
{
    /// <summary>非离线，或历史记录未标注原因。</summary>
    None = 0,

    /// <summary>PLC 状态字为 0（设备侧报离线）。</summary>
    PlcReported = 1,

    /// <summary>采集读不到状态字（会话断开 / 通讯失败）。</summary>
    CommsLost = 2,

    /// <summary>采集服务正常停止（干净关窗）。</summary>
    AcquisitionStopped = 3,

    /// <summary>启动时把杀进程 / 断电空窗补成离线。</summary>
    GapFilled = 4,
}

/// <summary>状态字 + 离线原因 → 本地化资源 Key（WPF / WASM 共用）。</summary>
public static class DeviceStatusLocKeys
{
    public static string For(DeviceStatus status, OfflineCause cause = OfflineCause.None)
        => For((int)status, cause);

    public static string For(int statusWord, OfflineCause cause = OfflineCause.None) => statusWord switch
    {
        (int)DeviceStatus.Running => "Status_Running",
        (int)DeviceStatus.Alarm => "Status_Alarm",
        (int)DeviceStatus.Paused => "Status_Paused",
        (int)DeviceStatus.Offline => cause switch
        {
            OfflineCause.PlcReported => "Status_Offline_PlcReported",
            OfflineCause.CommsLost => "Status_Offline_CommsLost",
            OfflineCause.AcquisitionStopped => "Status_Offline_AcquisitionStopped",
            OfflineCause.GapFilled => "Status_Offline_GapFilled",
            _ => "Status_Offline",
        },
        _ => "Status_Unknown",
    };
}
