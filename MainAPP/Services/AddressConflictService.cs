using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;

namespace MainAPP.Services;

/// <summary>
/// 跨设备 PLC 地址冲突汇总服务：基于结构化的冲突检测结果（而非解析本地化错误文案）
/// 汇总每台设备涉及的冲突地址，供设备管理列表展示冲突摘要。
/// 审查修复 2026-08-15：原实现从冲突 Message 字符串反解地址，依赖本地化文案格式且易失效。
/// </summary>
public static class AddressConflictService
{
    public static AddressConflictReport Compute(IEnumerable<Device> devices, IPlcAddressCodec? addressCodec = null)
    {
        var conflicts = DeviceConfigValidator.CollectCrossDeviceConflictsStructured(devices, addressCodec);

        var summaries = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var conflict in conflicts)
        {
            foreach (var device in conflict.Devices)
            {
                if (!summaries.TryGetValue(device.Id, out var addresses))
                {
                    addresses = [];
                    summaries[device.Id] = addresses;
                }
                if (!addresses.Contains(conflict.Address, StringComparer.OrdinalIgnoreCase))
                    addresses.Add(conflict.Address);
            }
        }

        return new AddressConflictReport(
            conflicts.Count,
            summaries.ToDictionary(
                pair => pair.Key,
                pair => string.Join(", ", pair.Value),
                StringComparer.OrdinalIgnoreCase));
    }
}

/// <summary>地址冲突报告：冲突地址总数 + 每台设备涉及的冲突地址摘要。</summary>
public sealed record AddressConflictReport(
    int ConflictCount,
    IReadOnlyDictionary<string, string> Summaries);
