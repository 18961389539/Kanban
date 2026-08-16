using Kanban.Collector.Core.Models;

namespace Kanban.Collector.Core.Services;

internal static class DWordAddressBatchCollector
{
    public static HashSet<string> Collect(Device device, IDeviceAdapter adapter)
    {
        var addresses = new[]
        {
            device.OkCountAddress,
            device.NgCountAddress,
            device.StatusCountAddress,
        }
        .Concat(device.Defects.ToList().Select(defect => defect.PlcAddress))
        .Concat(device.CounterAlarms.ToList()
            .Where(counterAlarm => counterAlarm.Enabled)
            .Select(counterAlarm => counterAlarm.PlcAddress))
        .Select(adapter.AddressCodec.Parse)
        .Where(parsed => parsed is { IsValid: true, Type: PlcAddressType.DWord })
        .Select(parsed => parsed.Original)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return addresses;
    }
}
