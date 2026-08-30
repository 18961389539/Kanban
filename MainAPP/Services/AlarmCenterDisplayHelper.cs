using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;

namespace MainAPP.Services;

/// <summary>
/// 报警中心历史事件/排行的显示名解析：从设备配置反查多语言名称，
/// 数据源报警（AlarmId 以 src: 前缀）按值项配置解析。
/// </summary>
public static class AlarmCenterDisplayHelper
{
    public static string ResolveEventDisplayName(IDeviceRepository deviceRepository, AlarmEventRecord record)
        => ResolveEventDisplayName(deviceRepository, record.DeviceId, record.AlarmId, record.AlarmName);

    public static string ResolveEventDisplayName(
        IDeviceRepository deviceRepository,
        string deviceId,
        string alarmId,
        string alarmName)
    {
        if (alarmId.StartsWith("src:", StringComparison.OrdinalIgnoreCase))
        {
            var valueId = alarmId["src:".Length..];
            var resolved = TryResolveDataSourceValueDisplay(deviceRepository, deviceId, valueId);
            if (resolved != null) return resolved;
        }

        var device = deviceRepository.GetDeviceById(deviceId);
        if (device == null) return alarmName;

        var alarm = device.Alarms.FirstOrDefault(a =>
            string.Equals(a.Id, alarmId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(a.Name, alarmName, StringComparison.OrdinalIgnoreCase));
        if (alarm != null) return AlarmNameLocalizer.Resolve(alarm);

        var counter = device.CounterAlarms.FirstOrDefault(a =>
            string.Equals(a.Id, alarmId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(a.Name, alarmName, StringComparison.OrdinalIgnoreCase));
        if (counter != null) return AlarmNameLocalizer.Resolve(counter);

        return alarmName;
    }

    public static (string? NameEn, string? NameJa, string? NamePt) ResolveEventLocalizedFields(
        IDeviceRepository deviceRepository,
        string deviceId,
        string alarmId)
    {
        if (!alarmId.StartsWith("src:", StringComparison.OrdinalIgnoreCase))
            return (null, null, null);

        var valueId = alarmId["src:".Length..];
        var device = deviceRepository.GetDeviceById(deviceId);
        if (device == null) return (null, null, null);

        foreach (var source in device.Sources)
        {
            var value = source.Values.FirstOrDefault(v =>
                string.Equals(v.Id, valueId, StringComparison.OrdinalIgnoreCase));
            if (value == null) continue;

            var sourceEn = source.NameEn;
            var sourceJa = source.NameJa;
            var sourcePt = source.NamePt;
            return (
                ComposeLocalized(source.Name, sourceEn, value.Name, value.NameEn),
                ComposeLocalized(source.Name, sourceJa, value.Name, value.NameJa),
                ComposeLocalized(source.Name, sourcePt, value.Name, value.NamePt));
        }

        return (null, null, null);
    }

    private static string? TryResolveDataSourceValueDisplay(
        IDeviceRepository deviceRepository,
        string deviceId,
        string valueId)
    {
        var device = deviceRepository.GetDeviceById(deviceId);
        if (device == null) return null;

        foreach (var source in device.Sources)
        {
            var value = source.Values.FirstOrDefault(v =>
                string.Equals(v.Id, valueId, StringComparison.OrdinalIgnoreCase));
            if (value == null) continue;
            return $"{AlarmNameLocalizer.Resolve(source)}-{AlarmNameLocalizer.Resolve(value)}";
        }

        return null;
    }

    private static string? ComposeLocalized(string left, string? leftLocalized, string right, string? rightLocalized)
    {
        var l = string.IsNullOrWhiteSpace(leftLocalized) ? left : leftLocalized;
        var r = string.IsNullOrWhiteSpace(rightLocalized) ? right : rightLocalized;
        return $"{l}-{r}";
    }
}
