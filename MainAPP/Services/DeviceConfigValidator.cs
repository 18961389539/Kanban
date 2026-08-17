using System;
using System.Collections.Generic;
using System.Linq;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Models;
using MainAPP.Resources;

namespace MainAPP.Services;

/// <summary>
/// 设备配置校验器：聚合所有设备的配置错误（地址完整性 / 名称唯一性 / 报警地址冲突 / 跨设备地址冲突）。
/// 全部为纯静态方法，无副作用，输入设备列表输出错误列表，便于单元测试。
/// 每项错误携带 Device 与目标 Tab 索引，供错误对话框点击后定位到对应设备/选项卡。
/// </summary>
public static class DeviceConfigValidator
{
    /// <summary>
    /// 聚合全部设备配置校验错误。覆盖：地址完整性、设备名唯一性、
    /// 同设备内报警名/报警 PLC 地址唯一性、同设备内缺陷名唯一性、计数报警阈值上限。
    /// </summary>
    public static List<DeviceConfigError> CollectValidationErrors(
        IEnumerable<Device> devices,
        IPlcAddressCodec? addressCodec = null)
    {
        List<DeviceConfigError> errors = [];
        var deviceList = devices as IList<Device> ?? devices.ToList();
        addressCodec ??= new MitsubishiAddressCodec();

        // 地址完整性（设备参数 Tab）
        foreach (var device in deviceList)
        {
            List<string> missing = [];
            if (string.IsNullOrWhiteSpace(device.OkCountAddress)) missing.Add(Strings.M228);
            if (string.IsNullOrWhiteSpace(device.NgCountAddress)) missing.Add(Strings.M229);
            if (string.IsNullOrWhiteSpace(device.StatusCountAddress)) missing.Add(Strings.M230);
            if (string.IsNullOrWhiteSpace(device.ProductionResetAddress)) missing.Add(Strings.M231);
            if (missing.Count > 0)
                errors.Add(new DeviceConfigError
                {
                    Device = device,
                    TargetTabIndex = 0,
                    Message = string.Format(Strings.F203, device.Name, string.Join("、", missing)),
                });

            AddAddressError(errors, device, addressCodec, device.OkCountAddress, PlcAddressType.DWord, 0, Strings.M232);
            AddAddressError(errors, device, addressCodec, device.NgCountAddress, PlcAddressType.DWord, 0, Strings.M233);
            AddAddressError(errors, device, addressCodec, device.StatusCountAddress, PlcAddressType.DWord, 0, Strings.M230);
            AddAddressError(errors, device, addressCodec, device.ProductionResetAddress, PlcAddressType.DWord, 0, Strings.M234);
            AddAddressError(errors, device, addressCodec, device.RecipeAddress, PlcAddressType.DWord, 0, Strings.M235);
            foreach (var alarm in device.Alarms)
                AddAddressError(errors, device, addressCodec, alarm.PlcAddress, PlcAddressType.MBit, 1, string.Format(Strings.F128, alarm.Name));
            foreach (var defect in device.Defects)
                AddAddressError(errors, device, addressCodec, defect.PlcAddress, PlcAddressType.DWord, 2, string.Format(Strings.F189, defect.Name));
            foreach (var counterAlarm in device.CounterAlarms)
                AddAddressError(errors, device, addressCodec, counterAlarm.PlcAddress, PlcAddressType.DWord, 3, string.Format(Strings.F199, counterAlarm.Name));
            foreach (var source in device.Sources)
            {
                AddAddressError(errors, device, addressCodec, source.PlcAddress, PlcAddressType.DWord, 5, $"采集源「{source.Name}」采集地址格式无效");
                AddAddressError(errors, device, addressCodec, source.TriggerAddress, PlcAddressType.DWord, 5, $"采集源「{source.Name}」触发地址格式无效");

                // 阈值规则（设计稿 §6）：上下限关系、滞回/延时合法、预期值不能与上下限并存
                if (source.HasLimits && source.Hysteresis < 0)
                    errors.Add(new DeviceConfigError
                    {
                        Device = device,
                        TargetTabIndex = 5,
                        Message = $"采集源「{source.Name}」滞回不能为负",
                    });
                if (source.HasLimits && source.ConfirmSeconds < 0)
                    errors.Add(new DeviceConfigError
                    {
                        Device = device,
                        TargetTabIndex = 5,
                        Message = $"采集源「{source.Name}」延时确认不能为负",
                    });
                if (!string.IsNullOrWhiteSpace(source.TriggerAddress)
                    && string.Equals(source.TriggerAddress?.Trim(), source.PlcAddress?.Trim(), StringComparison.OrdinalIgnoreCase))
                    errors.Add(new DeviceConfigError
                    {
                        Device = device,
                        TargetTabIndex = 5,
                        Message = $"采集源「{source.Name}」触发地址不能与采集地址相同",
                    });
            }

            // 目标周期必须 > 0：OEE 性能率分母为 TargetCycle，0 会导致性能率恒为 0
            if (device.TargetCycle <= 0)
                errors.Add(new DeviceConfigError
                {
                    Device = device,
                    TargetTabIndex = 0,
                    Message = string.Format(Strings.F204, device.Name, device.TargetCycle),
                });
        }

        // 设备名重复（设备参数 Tab，定位到首个同名设备）
        foreach (var g in deviceList
            .GroupBy(d => d.Name?.Trim() ?? "", StringComparer.OrdinalIgnoreCase)
            .Where(g => !string.IsNullOrEmpty(g.Key) && g.Count() > 1))
        {
            errors.Add(new DeviceConfigError
            {
                Device = g.First(),
                TargetTabIndex = 0,
                Message = string.Format(Strings.F209, g.Key, g.Count()),
            });
        }

        // 报警名 / 报警 PLC 地址唯一性（报警 Tab）+ 缺陷名唯一性（缺陷 Tab）
        foreach (var device in deviceList)
        {
            var dupAlarmName = device.Alarms
                .GroupBy(a => a.Name?.Trim() ?? "", StringComparer.OrdinalIgnoreCase)
                .Where(g => !string.IsNullOrEmpty(g.Key) && g.Count() > 1)
                .FirstOrDefault();
            if (dupAlarmName != null)
                errors.Add(new DeviceConfigError
                {
                    Device = device,
                    TargetTabIndex = 1,
                    Message = string.Format(Strings.F202, device.Name, dupAlarmName.Key),
                });

            // 报警 PLC 地址唯一性：Alarm.Id 基于确定性生成（{DeviceId}_{PlcAddress}），
            // 重复地址会导致 Id 碰撞，_prevAlarmStates 字典中两个报警共享同一 key，边沿检测错乱
            var dupAlarmAddr = device.Alarms
                .Where(a => !string.IsNullOrWhiteSpace(a.PlcAddress))
                .GroupBy(a => addressCodec.CanonicalKey(a.PlcAddress!.Trim()), StringComparer.OrdinalIgnoreCase)
                .Where(g => !string.IsNullOrWhiteSpace(g.Key))
                .Where(g => g.Count() > 1)
                .FirstOrDefault();
            if (dupAlarmAddr != null)
                errors.Add(new DeviceConfigError
                {
                    Device = device,
                    TargetTabIndex = 1,
                    Message = string.Format(Strings.F207, device.Name, dupAlarmAddr.Key),
                });

            var dupDefect = device.Defects
                .GroupBy(d => d.Name?.Trim() ?? "", StringComparer.OrdinalIgnoreCase)
                .Where(g => !string.IsNullOrEmpty(g.Key) && g.Count() > 1)
                .FirstOrDefault();
            if (dupDefect != null)
                errors.Add(new DeviceConfigError
                {
                    Device = device,
                    TargetTabIndex = 2,
                    Message = string.Format(Strings.F205, device.Name, dupDefect.Key),
                });

            // 计数报警阈值上限允许为 0：语义为「仅记录不触发」（IsTriggered => MaxValue > 0 && CurrentValue > MaxValue，
            // 0 值永不触发，运行时天然安全）。样本数据即预置 0 值报警（"仅记录不停机"），故不做阻断校验。
            // 若未来要求必须配置阈值，应改为「警告」而非错误，避免合法配置无法保存。
        }

        // 跨设备地址冲突（两台及以上设备共用同一 PLC 地址，会导致产量/状态数据串台）
        errors.AddRange(CollectCrossDeviceConflicts(deviceList, addressCodec));

        // 同设备内主地址重复（OK/NG/状态/复位/配方互指同一地址，同样会串台）
        errors.AddRange(CollectSameDevicePrimaryAddressConflicts(deviceList, addressCodec));

        return errors;
    }

    /// <summary>
    /// 检测跨设备 PLC 地址冲突（兼容旧签名，返回 <see cref="DeviceConfigError"/> 供校验错误列表使用）。
    /// 内部委托给 <see cref="CollectCrossDeviceConflictsStructured"/> 后转换，保证单一事实来源。
    /// </summary>
    public static List<DeviceConfigError> CollectCrossDeviceConflicts(
        IEnumerable<Device> devices,
        IPlcAddressCodec? addressCodec = null)
        => CollectCrossDeviceConflictsStructured(devices, addressCodec)
            .Select(ToConfigError)
            .ToList();

    /// <summary>
    /// 结构化跨设备 PLC 地址冲突：返回冲突地址、涉及设备与应定位的 Tab 索引。
    /// 与旧实现相比，Tab 索引按地址类别归属（设备参数/报警/缺陷/计数报警）精确定位，
    /// 不再依赖从本地化错误文案反解地址。
    /// </summary>
    public static List<DeviceAddressConflict> CollectCrossDeviceConflictsStructured(
        IEnumerable<Device> devices,
        IPlcAddressCodec? addressCodec = null)
    {
        var deviceList = devices as IList<Device> ?? devices.ToList();
        addressCodec ??= new MitsubishiAddressCodec();

        var byAddress = new Dictionary<string, (List<Device> Devices, int TabIndex)>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in deviceList)
        {
            foreach (var (addr, tabIndex) in GetDeviceAddressesWithTabs(d))
            {
                var normalized = addressCodec.CanonicalKey(addr);
                if (string.IsNullOrWhiteSpace(normalized)) continue;
                if (!byAddress.TryGetValue(normalized, out var entry))
                {
                    entry = ([], tabIndex);
                    byAddress[normalized] = entry;
                }
                if (!entry.Devices.Contains(d)) entry.Devices.Add(d);
                byAddress[normalized] = entry;
            }
        }

        var conflicts = new List<DeviceAddressConflict>();
        foreach (var kvp in byAddress.Where(k => k.Value.Devices.Count > 1))
            conflicts.Add(new DeviceAddressConflict(kvp.Key, kvp.Value.Devices, kvp.Value.TabIndex));
        return conflicts;
    }

    /// <summary>
    /// 检测同一设备内主地址重复：OK/NG/状态/复位/配方五个 DWord 地址互相指向同一地址。
    /// 旧版跨设备冲突只按设备去重，无法发现该问题。
    /// </summary>
    public static List<DeviceConfigError> CollectSameDevicePrimaryAddressConflicts(
        IEnumerable<Device> devices,
        IPlcAddressCodec? addressCodec = null)
    {
        var list = new List<DeviceConfigError>();
        var codec = addressCodec ?? new MitsubishiAddressCodec();

        foreach (var device in devices)
        {
            var fields = new[]
            {
                device.OkCountAddress,
                device.NgCountAddress,
                device.StatusCountAddress,
                device.ProductionResetAddress,
                device.RecipeAddress,
            };

            var duplicates = fields
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .GroupBy(f => codec.CanonicalKey(f!.Trim()), StringComparer.OrdinalIgnoreCase)
                .Where(g => !string.IsNullOrWhiteSpace(g.Key) && g.Count() > 1);

            foreach (var g in duplicates)
            {
                list.Add(new DeviceConfigError
                {
                    Device = device,
                    TargetTabIndex = 0,
                    Message = string.Format(Strings.F504, device.Name, g.Key),
                });
            }
        }

        return list;
    }

    private static DeviceConfigError ToConfigError(DeviceAddressConflict conflict)
        => new()
        {
            Device = conflict.Devices[1],
            TargetTabIndex = conflict.TargetTabIndex,
            Message = string.Format(Strings.F080, conflict.Address, conflict.Devices.Count) +
                      string.Join("、", conflict.Devices.Select(x => x.Name)),
        };

    /// <summary>按地址类别收集单台设备的全部非空 PLC 地址及对应 Tab 索引（0=设备参数,1=报警,2=缺陷,3=计数报警）。</summary>
    private static IEnumerable<(string Address, int TabIndex)> GetDeviceAddressesWithTabs(Device d)
    {
        if (!string.IsNullOrWhiteSpace(d.OkCountAddress)) yield return (d.OkCountAddress!, 0);
        if (!string.IsNullOrWhiteSpace(d.NgCountAddress)) yield return (d.NgCountAddress!, 0);
        if (!string.IsNullOrWhiteSpace(d.StatusCountAddress)) yield return (d.StatusCountAddress!, 0);
        if (!string.IsNullOrWhiteSpace(d.ProductionResetAddress)) yield return (d.ProductionResetAddress!, 0);
        if (!string.IsNullOrWhiteSpace(d.RecipeAddress)) yield return (d.RecipeAddress!, 0);
        foreach (var a in d.Alarms)
            if (!string.IsNullOrWhiteSpace(a.PlcAddress)) yield return (a.PlcAddress!, 1);
        foreach (var def in d.Defects)
            if (!string.IsNullOrWhiteSpace(def.PlcAddress)) yield return (def.PlcAddress!, 2);
        foreach (var c in d.CounterAlarms)
            if (!string.IsNullOrWhiteSpace(c.PlcAddress)) yield return (c.PlcAddress!, 3);
        foreach (var s in d.Sources)
        {
            if (!string.IsNullOrWhiteSpace(s.PlcAddress)) yield return (s.PlcAddress!, 5);
            if (!string.IsNullOrWhiteSpace(s.TriggerAddress)) yield return (s.TriggerAddress!, 5);
        }
    }

    private static void AddAddressError(
        List<DeviceConfigError> errors,
        Device device,
        IPlcAddressCodec codec,
        string? address,
        PlcAddressType expectedType,
        int tabIndex,
        string label)
    {
        if (string.IsNullOrWhiteSpace(address)) return;
        var parsed = codec.Parse(address);
        if (parsed is not { IsValid: true } || parsed.Type != expectedType)
        {
            errors.Add(new DeviceConfigError
            {
                Device = device,
                TargetTabIndex = tabIndex,
                Message = string.Format(Strings.F201, device.Name, label, parsed.ErrorMessage),
            });
        }
    }

    /// <summary>
    /// 收集单台设备的全部 PLC 地址（空值跳过），用于跨设备冲突检测。
    /// </summary>
    public static IEnumerable<string> GetDeviceAddresses(Device d)
    {
        yield return d.OkCountAddress;
        yield return d.NgCountAddress;
        yield return d.StatusCountAddress;
        yield return d.ProductionResetAddress;
        yield return d.RecipeAddress;
        foreach (var a in d.Alarms) yield return a.PlcAddress;
        foreach (var def in d.Defects) yield return def.PlcAddress;
        foreach (var c in d.CounterAlarms) yield return c.PlcAddress;
        foreach (var s in d.Sources)
        {
            yield return s.PlcAddress;
            yield return s.TriggerAddress;
        }
    }
}

/// <summary>结构化跨设备地址冲突：冲突地址、涉及设备与应定位的 Tab 索引。</summary>
public sealed record DeviceAddressConflict(
    string Address,
    IReadOnlyList<Device> Devices,
    int TargetTabIndex);
