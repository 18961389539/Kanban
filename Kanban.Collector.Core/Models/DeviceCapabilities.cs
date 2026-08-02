using System.Text.Json.Serialization;

namespace Kanban.Core.Models;

/// <summary>
/// 设备可用能力。能力由当前设备配置派生，不单独持久化，避免配置状态不一致。
/// PLC 品牌和通信驱动由共享 PlcConfig.Brand 决定，不由设备独立决定。
/// </summary>
public sealed record DeviceCapabilities(
    bool SupportsAlarmRead,
    bool SupportsWriteCommand,
    bool SupportsProductionCounter,
    bool SupportsStatusHistory)
{
    public static DeviceCapabilities For(Device device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return new DeviceCapabilities(
            device.Alarms.Any(alarm => !string.IsNullOrWhiteSpace(alarm.PlcAddress)),
            !string.IsNullOrWhiteSpace(device.ProductionResetAddress) ||
            !string.IsNullOrWhiteSpace(device.RecipeAddress),
            !string.IsNullOrWhiteSpace(device.OkCountAddress) ||
            !string.IsNullOrWhiteSpace(device.NgCountAddress),
            !string.IsNullOrWhiteSpace(device.StatusCountAddress));
    }
}

public partial class Device
{
    [JsonIgnore]
    public DeviceCapabilities Capabilities => DeviceCapabilities.For(this);
}
