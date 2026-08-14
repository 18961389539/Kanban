using Kanban.Core.Services;
using Kanban.Core.Models;
using Kanban.Core.Data;
using Kanban.Core.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using Kanban.Core.Models;
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

        return errors;
    }

    /// <summary>
    /// 检测跨设备 PLC 地址冲突：收集所有设备的 PLC 地址（OK/NG/状态/清零/配方 + 报警/缺陷/计数报警地址），
    /// 若同一地址出现在两台及以上不同设备，记录为冲突。每项携带其中一台冲突设备与目标 Tab 供定位。
    /// </summary>
    public static List<DeviceConfigError> CollectCrossDeviceConflicts(
        IEnumerable<Device> devices,
        IPlcAddressCodec? addressCodec = null)
    {
        var byAddress = new Dictionary<string, List<Device>>(StringComparer.OrdinalIgnoreCase);
        var deviceList = devices as IList<Device> ?? devices.ToList();
        addressCodec ??= new MitsubishiAddressCodec();
        foreach (var d in deviceList)
        {
            foreach (var addr in GetDeviceAddresses(d))
            {
                var normalized = addressCodec.CanonicalKey(addr);
                if (string.IsNullOrWhiteSpace(normalized)) continue;
                if (!byAddress.TryGetValue(normalized, out var list))
                {
                    list = [];
                    byAddress[normalized] = list;
                }
                if (!list.Contains(d)) list.Add(d);
            }
        }

        List<DeviceConfigError> errors = [];
        foreach (var kvp in byAddress.Where(k => k.Value.Count > 1))
        {
            var addr = kvp.Key;
            var list = kvp.Value;
            errors.Add(new DeviceConfigError
            {
                Device = list[1],
                TargetTabIndex = 0,
                Message = string.Format(Strings.F080, addr, list.Count) +
                          string.Join("、", list.Select(x => x.Name)),
            });
        }
        return errors;
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
    }
}
