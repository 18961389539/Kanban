using MainAPP.Models;

namespace MainAPP.Services;

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
        .Concat(device.CountAlarms.ToList()
            .Where(countAlarm => countAlarm.Enabled)
            .Select(countAlarm => countAlarm.PlcAddress))
        .Select(adapter.AddressCodec.Parse)
        .Where(parsed => parsed is { IsValid: true, Type: PlcAddressType.DWord })
        .Select(parsed => parsed.Original)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return addresses;
    }
}
