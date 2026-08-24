using Kanban.Collector.Core.Models;

namespace MainAPP.Services;

/// <summary>
/// 设备配置审计快照服务：把当前设备配置（全量设备 + 全量配置字段）压成可 JSON 序列化的审计快照。
/// 供 DeviceManagerViewModel 在保存前后生成 before/after 快照并写入审计库。
/// 审查修复 2026-08-15：原快照仅含前 20 台设备的 5 个字段，设备数与字段均不完整，审计会漏记。
/// </summary>
public static class DeviceAuditService
{
    public static DeviceAuditSnapshot CreateSnapshot(IEnumerable<Device> devices)
    {
        var items = devices.Select(d => new DeviceAuditItem(
            d.Id,
            d.Name ?? string.Empty,
            d.MachineType ?? string.Empty,
            d.TargetCycle,
            d.OkCountAddress ?? string.Empty,
            d.NgCountAddress ?? string.Empty,
            d.StatusCountAddress ?? string.Empty,
            d.ProductionResetAddress ?? string.Empty,
            d.RecipeAddress ?? string.Empty,
            d.RecipeName ?? string.Empty,
            d.RecipeValue,
            d.Alarms.Select(a => new AlarmAuditItem(
                a.Id ?? string.Empty,
                a.Name ?? string.Empty,
                a.PlcAddress ?? string.Empty,
                a.Level,
                a.Description ?? string.Empty)).ToArray(),
            d.Defects.Select(x => new DefectAuditItem(
                x.Name ?? string.Empty,
                x.PlcAddress ?? string.Empty,
                x.Severity,
                x.Category)).ToArray(),
            d.CounterAlarms.Select(c => new CounterAlarmAuditItem(
                c.Name ?? string.Empty,
                c.PlcAddress ?? string.Empty,
                c.MaxValue,
                c.Enabled,
                c.Unit ?? string.Empty,
                c.Description ?? string.Empty)).ToArray(),
            d.Sources.Select(s => new DataSourceAuditItem(
                s.Id ?? string.Empty,
                s.Name ?? string.Empty,
                s.Type ?? string.Empty,
                s.Enabled,
                s.Description ?? string.Empty,
                s.TriggerAddress ?? string.Empty,
                s.TriggerValue,
                s.AckValue,
                s.Values.Select(v => new DataSourceValueAuditItem(
                    v.Id ?? string.Empty,
                    v.Name ?? string.Empty,
                    v.PlcAddress ?? string.Empty,
                    v.Unit ?? string.Empty,
                    v.Enabled,
                    v.DataType,
                    v.StringLength,
                    v.LimitMin,
                    v.LimitMax,
                    v.FloatLimitMin,
                    v.FloatLimitMax,
                    v.Hysteresis,
                    v.ConfirmSeconds,
                    v.ExpectedValue,
                    v.FloatExpectedValue,
                    v.BoolExpectedValue,
                    v.StringExpectedValue,
                    v.EnumValues.Select(e => new DataSourceEnumAuditItem(e.Value, e.DisplayName ?? string.Empty)).ToArray())).ToArray())).ToArray()
        )).ToArray();

        return new DeviceAuditSnapshot(items.Length, items);
    }
}

/// <summary>设备配置审计快照（全量设备 + 全量配置字段）。</summary>
public sealed record DeviceAuditSnapshot(int Count, IReadOnlyList<DeviceAuditItem> Devices);

/// <summary>单台设备的配置审计条目。</summary>
public sealed record DeviceAuditItem(
    string Id,
    string Name,
    string MachineType,
    int TargetCycle,
    string OkAddress,
    string NgAddress,
    string StatusAddress,
    string ResetAddress,
    string RecipeAddress,
    string RecipeName,
    int RecipeValue,
    IReadOnlyList<AlarmAuditItem> Alarms,
    IReadOnlyList<DefectAuditItem> Defects,
    IReadOnlyList<CounterAlarmAuditItem> CounterAlarms,
    IReadOnlyList<DataSourceAuditItem> Sources);

/// <summary>报警配置审计条目。</summary>
public sealed record AlarmAuditItem(string Id, string Name, string PlcAddress, AlarmLevel Level, string Description);

/// <summary>缺陷配置审计条目。</summary>
public sealed record DefectAuditItem(string Name, string PlcAddress, DefectSeverity Severity, DefectCategory Category);

/// <summary>计数报警配置审计条目。</summary>
public sealed record CounterAlarmAuditItem(
    string Name, string PlcAddress, int MaxValue, bool Enabled, string Unit, string Description);

public sealed record DataSourceAuditItem(
    string Id,
    string Name,
    string Type,
    bool Enabled,
    string Description,
    string TriggerAddress,
    int TriggerValue,
    int AckValue,
    IReadOnlyList<DataSourceValueAuditItem> Values);

public sealed record DataSourceValueAuditItem(
    string Id,
    string Name,
    string PlcAddress,
    string Unit,
    bool Enabled,
    DataSourceValueType DataType,
    int StringLength,
    int LimitMin,
    int LimitMax,
    float FloatLimitMin,
    float FloatLimitMax,
    int Hysteresis,
    int ConfirmSeconds,
    int? ExpectedValue,
    float? FloatExpectedValue,
    bool? BoolExpectedValue,
    string? StringExpectedValue,
    IReadOnlyList<DataSourceEnumAuditItem> EnumValues);

public sealed record DataSourceEnumAuditItem(int Value, string DisplayName);
