using Kanban.Collector.Core.Models;

namespace Kanban.Collector.Core.Services;

internal static class DWordAddressBatchCollector
{
    public static HashSet<string> Collect(Device device, IDeviceAdapter adapter)
    {
        var okAddress = device.OkCountAddress;
        var ngAddress = device.NgCountAddress;
        var statusAddress = device.StatusCountAddress;
        var defects = device.Defects.ToList();
        var counterAlarms = device.CounterAlarms.ToList();
        var sources = device.Sources.ToList();
        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var codec = adapter.AddressCodec;

        void Add(string? address)
        {
            var parsed = codec.Parse(address);
            if (parsed is { IsValid: true, Type: PlcAddressType.DWord })
                addresses.Add(parsed.Original);
        }

        Add(okAddress);
        Add(ngAddress);
        Add(statusAddress);

        foreach (var defect in defects)
            Add(defect.PlcAddress);

        foreach (var counterAlarm in counterAlarms)
        {
            if (counterAlarm.Enabled)
                Add(counterAlarm.PlcAddress);
        }

        foreach (var source in sources)
        {
            if (!source.Enabled)
                continue;

            foreach (var value in source.Values)
            {
                if (value.Enabled)
                    Add(value.PlcAddress);
            }
            Add(source.TriggerAddress);
        }

        return addresses;
    }
}
