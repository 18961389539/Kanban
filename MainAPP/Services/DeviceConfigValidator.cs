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

public enum DeviceManagerTab
{
    Parameters = 0,
    Alarms = 1,
    Defects = 2,
    CounterAlarms = 3,
    Sources = 4,
    WorkOrders = 5,
}

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

        // 设备 Id 是运行时路由和持久化索引的主键，必须稳定且唯一。
        foreach (var group in deviceList
            .GroupBy(d => d.Id?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Where(g => string.IsNullOrEmpty(g.Key) || g.Count() > 1))
        {
            foreach (var device in group)
                errors.Add(new DeviceConfigError
                {
                    Device = device,
                    TargetTabIndex = 0,
                    Message = string.IsNullOrEmpty(group.Key)
                        ? string.Format(Strings.Validator_DeviceIdRequired, device.Name)
                        : string.Format(Strings.Validator_DeviceIdDuplicate, group.Key)
                });
        }

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

            AddAddressTypeErrors(errors, device, addressCodec);
            foreach (var source in device.Sources)
            {
                AddAddressError(errors, device, addressCodec, source.TriggerAddress, PlcAddressType.DWord, (int)DeviceManagerTab.Sources, string.Format(Strings.Validator_SourceTriggerAddressInvalid, source.Name));
                if (source.Values.Count == 0)
                {
                    errors.Add(new DeviceConfigError
                    {
                        Device = device,
                        TargetTabIndex = (int)DeviceManagerTab.Sources,
                        Message = string.Format(Strings.Validator_SourceNeedsValue, source.Name),
                    });
                    continue;
                }
                foreach (var value in source.Values)
                {
                    // 值项空地址：配置期直接拦截（修复 2026-08-17：触发源漏配采集地址会让 PLC 端一直等回执）
                    if (string.IsNullOrWhiteSpace(value.PlcAddress))
                    {
                        errors.Add(new DeviceConfigError
                        {
                            Device = device,
                            TargetTabIndex = (int)DeviceManagerTab.Sources,
                            Message = string.Format(Strings.Validator_SourceValueAddressMissing, source.Name, value.Name),
                        });
                        continue;
                    }
                    var expectedValueAddressType = value.DataType == DataSourceValueType.Bool ? PlcAddressType.MBit : PlcAddressType.DWord;
                AddAddressError(errors, device, addressCodec, value.PlcAddress, expectedValueAddressType, (int)DeviceManagerTab.Sources, string.Format(Strings.Validator_SourceValueAddressInvalid, source.Name, value.Name));

                    // 阈值规则（设计稿 §6）：上下限关系、滞回/延时合法
                    if (value.HasLimits && value.Hysteresis < 0)
                        errors.Add(new DeviceConfigError
                        {
                            Device = device,
                            TargetTabIndex = (int)DeviceManagerTab.Sources,
                            Message = string.Format(Strings.Validator_SourceHysteresisNegative, source.Name, value.Name),
                        });
                    if (value.HasLimits && value.ConfirmSeconds < 0)
                        errors.Add(new DeviceConfigError
                        {
                            Device = device,
                            TargetTabIndex = (int)DeviceManagerTab.Sources,
                            Message = string.Format(Strings.Validator_SourceConfirmSecondsNegative, source.Name, value.Name),
                        });
                    var triggerKey = addressCodec.CanonicalKey(source.TriggerAddress);
                    var valueKey = addressCodec.CanonicalKey(value.PlcAddress);
                    if (!string.IsNullOrWhiteSpace(triggerKey)
                        && string.Equals(triggerKey, valueKey, StringComparison.OrdinalIgnoreCase))
                        errors.Add(new DeviceConfigError
                        {
                            Device = device,
                            TargetTabIndex = (int)DeviceManagerTab.Sources,
                            Message = string.Format(Strings.Validator_SourceValueAddressSameAsTrigger, source.Name, value.Name),
                        });
                }

                // 同源内值项采集地址重复（修复 2026-08-17：大概率是配置错误，读同一地址的多个值项应合并）
                var dupAddress = source.Values
                    .Select(v => addressCodec.CanonicalKey(v.PlcAddress))
                    .Where(a => !string.IsNullOrWhiteSpace(a))
                    .GroupBy(a => a, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault(g => g.Count() > 1);
                if (dupAddress != null)
                    errors.Add(new DeviceConfigError
                    {
                        Device = device,
                        TargetTabIndex = (int)DeviceManagerTab.Sources,
                        Message = string.Format(Strings.Validator_SourceDuplicateAddress, source.Name, dupAddress.Count(), dupAddress.Key),
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

            // 计数报警阈值上限允许为 0：语义为「仅记录不触发」。
            ValidateChildConfiguration(errors, device);
        }

        // 跨设备地址冲突（两台及以上设备共用同一 PLC 地址，会导致产量/状态数据串台）
        errors.AddRange(CollectCrossDeviceConflicts(deviceList, addressCodec));

        // 同设备内主地址重复（OK/NG/状态/复位/配方互指同一地址，同样会串台）
        errors.AddRange(CollectSameDevicePrimaryAddressConflicts(deviceList, addressCodec));

        // 同设备内地址冲突（不破坏已覆盖的组合：主×主 → F504、报警×报警 → F207、
        // 同源值项/触发 → Validator_SourceDuplicateAddress；本方法补其余全部组合）
        errors.AddRange(CollectSameDeviceAddressReuse(deviceList, addressCodec));

        return errors;
    }

    // 同设备内地址占用记录：Kind 决定消息措辞与跳转 Tab，SourceOwner 区分数据源归属。
    private enum AddressRole { Primary, Alarm, Defect, CounterAlarm, SourceTrigger, SourceValue }

    private readonly record struct AddressOccupancy(AddressRole Role, int Tab, string Name, string SourceOwner, string Key);

    /// <summary>
    /// 单 PLC 部署下设备内任意两个采集项不得共用同一地址：这里补齐既存检查未覆盖的组合。
    /// 审查新增 2026-09-06（用户场景：所有设备位于同一 PLC，地址必须全局唯一）。
    /// 覆盖矩阵（同设备内）：
    ///   主×主 → 已有 F504；报警×报警 → 已有 F207；同源值项/触发×值项 → 已有；
    ///   本方法补：{缺陷×缺陷}、{计数报警×计数报警}、{缺陷×计数报警}、
    ///   {主×缺陷/计数报警/源触发/源值}、{缺陷/计数报警×源触发/源值}、跨源触发×值项 与 报警×布尔值项。
    /// 跨设备冲突仍由 <see cref="CollectCrossDeviceConflicts"/> 负责，两者互补无重叠。
    /// </summary>
    public static List<DeviceConfigError> CollectSameDeviceAddressReuse(
        IEnumerable<Device> devices,
        IPlcAddressCodec? addressCodec = null)
    {
        List<DeviceConfigError> errors = [];
        var codec = addressCodec ?? new MitsubishiAddressCodec();

        foreach (var device in devices)
        {
            foreach (var group in EnumerateOccupancy(device, codec)
                .GroupBy(o => o.Key, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1))
            {
                var members = group.ToList();
                var roles = members.Select(m => m.Role).ToHashSet();

                // 已被既有检查覆盖的组合：本方法跳过，避免重复报错。
                if (roles.Count == 1 && roles.Contains(AddressRole.Primary)) continue;   // 主×主 → F504
                if (roles.Count == 1 && roles.Contains(AddressRole.Alarm)) continue;     // 报警×报警 → F207
                if (members.All(m => m.SourceOwner.Length > 0
                                      && m.SourceOwner == members[0].SourceOwner)) continue;    // 同源内 → 已有

                // 主字段参与的重复 → 复用 F504（语义：设备主地址被多个字段重复使用）
                if (roles.Contains(AddressRole.Primary))
                {
                    errors.Add(new DeviceConfigError
                    {
                        Device = device,
                        TargetTabIndex = (int)DeviceManagerTab.Parameters,
                        Message = string.Format(Strings.F504, device.Name, group.Key),
                    });
                    continue;
                }

                // 纯子字段重复 → 列出各占用者名称（用户数据本身不翻译）
                var names = string.Join("、", members.Select(DescribeMember));
                errors.Add(new DeviceConfigError
                {
                    Device = device,
                    // 定位到第二个占用者所在页：用户看到错误时先改"后来加的那个"
                    TargetTabIndex = members[1].Tab,
                    Message = string.Format(Strings.Validator_AddressReuseInDevice, device.Name, group.Key, names),
                });
            }
        }

        return errors;
    }

    private static string DescribeMember(AddressOccupancy o) => o.Role switch
    {
        AddressRole.Defect => string.Format(Strings.Validator_RoleDefect, o.Name),
        AddressRole.CounterAlarm => string.Format(Strings.Validator_RoleCounterAlarm, o.Name),
        AddressRole.SourceTrigger => string.Format(Strings.Validator_RoleSourceTrigger, o.Name),
        AddressRole.SourceValue => string.Format(Strings.Validator_RoleSourceValue, o.Name),
        _ => o.Name,
    };

    /// <summary>枚举单台设备的全部非空 PLC 地址占用（去重后仍逐条返回——重复地址要靠重复条目暴露）。</summary>
    private static IEnumerable<AddressOccupancy> EnumerateOccupancy(Device device, IPlcAddressCodec codec)
    {
        (string? Address, AddressRole Role, string Name, int Tab)[] primaries =
        [
            (device.OkCountAddress, AddressRole.Primary, "OK", (int)DeviceManagerTab.Parameters),
            (device.NgCountAddress, AddressRole.Primary, "NG", (int)DeviceManagerTab.Parameters),
            (device.StatusCountAddress, AddressRole.Primary, Strings.Validator_PrimaryStatus, (int)DeviceManagerTab.Parameters),
            (device.ProductionResetAddress, AddressRole.Primary, Strings.Validator_PrimaryReset, (int)DeviceManagerTab.Parameters),
            (device.RecipeAddress, AddressRole.Primary, Strings.Validator_PrimaryRecipe, (int)DeviceManagerTab.Parameters),
        ];
        foreach (var (addr, role, name, tab) in primaries)
        {
            if (string.IsNullOrWhiteSpace(addr)) continue;
            var key = codec.CanonicalKey(addr.Trim());
            if (string.IsNullOrWhiteSpace(key)) continue;
            yield return new AddressOccupancy(role, tab, name, string.Empty, key);
        }

        foreach (var alarm in device.Alarms)
        {
            if (string.IsNullOrWhiteSpace(alarm.PlcAddress)) continue;
            var key = codec.CanonicalKey(alarm.PlcAddress.Trim());
            if (string.IsNullOrWhiteSpace(key)) continue;
            yield return new AddressOccupancy(AddressRole.Alarm, (int)DeviceManagerTab.Alarms, alarm.Name ?? string.Empty, string.Empty, key);
        }

        foreach (var defect in device.Defects)
        {
            if (string.IsNullOrWhiteSpace(defect.PlcAddress)) continue;
            var key = codec.CanonicalKey(defect.PlcAddress.Trim());
            if (string.IsNullOrWhiteSpace(key)) continue;
            yield return new AddressOccupancy(AddressRole.Defect, (int)DeviceManagerTab.Defects, defect.Name ?? string.Empty, string.Empty, key);
        }

        foreach (var counterAlarm in device.CounterAlarms)
        {
            if (string.IsNullOrWhiteSpace(counterAlarm.PlcAddress)) continue;
            var key = codec.CanonicalKey(counterAlarm.PlcAddress.Trim());
            if (string.IsNullOrWhiteSpace(key)) continue;
            yield return new AddressOccupancy(AddressRole.CounterAlarm, (int)DeviceManagerTab.CounterAlarms, counterAlarm.Name ?? string.Empty, string.Empty, key);
        }

        foreach (var source in device.Sources)
        {
            var owner = source.Id ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(source.TriggerAddress))
            {
                var key = codec.CanonicalKey(source.TriggerAddress.Trim());
                if (!string.IsNullOrWhiteSpace(key))
                    yield return new AddressOccupancy(AddressRole.SourceTrigger, (int)DeviceManagerTab.Sources, source.Name ?? string.Empty, owner, key);
            }
            foreach (var value in source.Values)
            {
                if (string.IsNullOrWhiteSpace(value.PlcAddress)) continue;
                var key = codec.CanonicalKey(value.PlcAddress.Trim());
                if (string.IsNullOrWhiteSpace(key)) continue;
                yield return new AddressOccupancy(AddressRole.SourceValue, (int)DeviceManagerTab.Sources, value.Name ?? string.Empty, owner, key);
            }
        }
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

        var byAddress = new Dictionary<string, (List<Device> Devices, Dictionary<string, int> TabIndices)>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in deviceList)
        {
            foreach (var (addr, tabIndex) in GetDeviceAddressesWithTabs(d))
            {
                var normalized = addressCodec.CanonicalKey(addr);
                if (string.IsNullOrWhiteSpace(normalized)) continue;
                if (!byAddress.TryGetValue(normalized, out var entry))
                {
                    entry = ([], new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
                    byAddress[normalized] = entry;
                }
                if (!entry.Devices.Contains(d)) entry.Devices.Add(d);
                entry.TabIndices.TryAdd(d.Id, tabIndex);
                byAddress[normalized] = entry;
            }
        }

        var conflicts = new List<DeviceAddressConflict>();
        foreach (var kvp in byAddress.Where(k => k.Value.Devices.Count > 1))
        {
            var firstDevice = kvp.Value.Devices[0];
            var firstTab = kvp.Value.TabIndices[firstDevice.Id];
            conflicts.Add(new DeviceAddressConflict(kvp.Key, kvp.Value.Devices, firstTab)
            {
                TargetTabIndices = kvp.Value.TabIndices,
            });
        }
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
            TargetTabIndex = conflict.GetTargetTabIndex(conflict.Devices[1]),
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
            if (!string.IsNullOrWhiteSpace(s.TriggerAddress)) yield return (s.TriggerAddress!, (int)DeviceManagerTab.Sources);
            foreach (var v in s.Values)
                if (!string.IsNullOrWhiteSpace(v.PlcAddress)) yield return (v.PlcAddress!, (int)DeviceManagerTab.Sources);
        }
    }

    private static void ValidateChildConfiguration(List<DeviceConfigError> errors, Device device)
    {
        ValidateChildSet(errors, device, device.Alarms.Select(x => (x.Id, x.DeviceId, x.Name)), 1, Strings.Validator_AlarmKind);
        ValidateChildSet(errors, device, device.Defects.Select(x => (x.Id, x.DeviceId, x.Name)), 2, Strings.Validator_DefectKind);
        ValidateChildSet(errors, device, device.CounterAlarms.Select(x => (x.Id, x.DeviceId, x.Name)), 3, Strings.Validator_CounterAlarmKind);

        var sourceNames = device.Sources.GroupBy(s => s.Name?.Trim() ?? "", StringComparer.OrdinalIgnoreCase);
        foreach (var group in sourceNames.Where(g => string.IsNullOrEmpty(g.Key) || g.Count() > 1))
            errors.Add(new DeviceConfigError { Device = device, TargetTabIndex = (int)DeviceManagerTab.Sources, Message = string.IsNullOrEmpty(group.Key) ? string.Format(Strings.Validator_EmptySourceName, device.Name) : string.Format(Strings.Validator_DuplicateSourceName, group.Key) });

        foreach (var source in device.Sources)
        {
            if (string.IsNullOrWhiteSpace(source.Id) || !string.Equals(source.DeviceId?.Trim(), device.Id?.Trim(), StringComparison.OrdinalIgnoreCase))
                errors.Add(new DeviceConfigError { Device = device, TargetTabIndex = (int)DeviceManagerTab.Sources, Message = string.Format(Strings.Validator_SourceIdentityInvalid, source.Name) });
            var valueNames = source.Values.GroupBy(v => v.Name?.Trim() ?? "", StringComparer.OrdinalIgnoreCase);
            foreach (var group in valueNames.Where(g => string.IsNullOrEmpty(g.Key) || g.Count() > 1))
                errors.Add(new DeviceConfigError { Device = device, TargetTabIndex = (int)DeviceManagerTab.Sources, Message = string.IsNullOrEmpty(group.Key) ? string.Format(Strings.Validator_EmptyValueName, source.Name) : string.Format(Strings.Validator_DuplicateValueName, source.Name, group.Key) });
            foreach (var value in source.Values)
            {
                if (string.IsNullOrWhiteSpace(value.Id))
                    errors.Add(new DeviceConfigError { Device = device, TargetTabIndex = (int)DeviceManagerTab.Sources, Message = string.Format(Strings.Validator_EmptyValueId, source.Name) });
                if (value.DataType == DataSourceValueType.String && (value.StringLength < 1 || value.StringLength > 1024))
                    errors.Add(new DeviceConfigError { Device = device, TargetTabIndex = (int)DeviceManagerTab.Sources, Message = string.Format(Strings.Validator_StringLengthInvalid, source.Name, value.Name) });
                if (value.DataType == DataSourceValueType.Float32 && (float.IsNaN(value.FloatLimitMin) || float.IsNaN(value.FloatLimitMax) || float.IsInfinity(value.FloatLimitMin) || float.IsInfinity(value.FloatLimitMax)))
                    errors.Add(new DeviceConfigError { Device = device, TargetTabIndex = (int)DeviceManagerTab.Sources, Message = string.Format(Strings.Validator_FloatLimitsInvalid, source.Name, value.Name) });
                var hasLimitFields = value.DataType == DataSourceValueType.Float32
                    ? value.FloatLimitMin != 0 || value.FloatLimitMax != 0
                    : value.LimitMin != 0 || value.LimitMax != 0;
                var limitInvalid = value.DataType == DataSourceValueType.Float32
                    ? value.FloatLimitMax <= value.FloatLimitMin
                    : value.LimitMax <= value.LimitMin;
                if (hasLimitFields && limitInvalid)
                    errors.Add(new DeviceConfigError { Device = device, TargetTabIndex = (int)DeviceManagerTab.Sources, Message = string.Format(Strings.Validator_LimitOrderInvalid, source.Name, value.Name) });
                if (value.HasLimits && value.HasExpectedValue)
                    errors.Add(new DeviceConfigError { Device = device, TargetTabIndex = (int)DeviceManagerTab.Sources, Message = string.Format(Strings.Validator_LimitExpectedConflict, source.Name, value.Name) });
                if (value.Hysteresis < 0 || value.ConfirmSeconds < 0)
                    errors.Add(new DeviceConfigError { Device = device, TargetTabIndex = (int)DeviceManagerTab.Sources, Message = string.Format(Strings.Validator_AlarmParametersNegative, source.Name, value.Name) });
                var enumGroups = value.EnumValues.GroupBy(e => e.Value).Any(g => g.Count() > 1);
                if (value.EnumValues.Any(e => string.IsNullOrWhiteSpace(e.DisplayName)) || enumGroups)
                    errors.Add(new DeviceConfigError { Device = device, TargetTabIndex = (int)DeviceManagerTab.Sources, Message = string.Format(Strings.Validator_EnumMappingInvalid, source.Name, value.Name) });
            }
            if (!string.IsNullOrWhiteSpace(source.TriggerAddress) && source.TriggerValue == source.AckValue)
                errors.Add(new DeviceConfigError { Device = device, TargetTabIndex = (int)DeviceManagerTab.Sources, Message = string.Format(Strings.Validator_TriggerAckConflict, source.Name) });
        }
    }

    private static void ValidateChildSet(
        List<DeviceConfigError> errors,
        Device device,
        IEnumerable<(string Id, string DeviceId, string Name)> items,
        int tabIndex,
        string kind)
    {
        var list = items.ToList();
        foreach (var item in list)
        {
            if (string.IsNullOrWhiteSpace(item.Id) || !string.Equals(item.DeviceId?.Trim(), device.Id?.Trim(), StringComparison.OrdinalIgnoreCase))
                errors.Add(new DeviceConfigError { Device = device, TargetTabIndex = tabIndex, Message = string.Format(Strings.Validator_ChildIdentityInvalid, device.Name, kind) });
            if (string.IsNullOrWhiteSpace(item.Name))
                errors.Add(new DeviceConfigError { Device = device, TargetTabIndex = tabIndex, Message = string.Format(Strings.Validator_EmptyChildName, device.Name, kind) });
        }
        foreach (var group in list.GroupBy(x => x.Id?.Trim() ?? "", StringComparer.OrdinalIgnoreCase).Where(g => string.IsNullOrEmpty(g.Key) || g.Count() > 1))
            errors.Add(new DeviceConfigError { Device = device, TargetTabIndex = tabIndex, Message = string.Format(Strings.Validator_DuplicateChildId, device.Name, kind) });
    }

    /// <summary>
    /// 单台设备的 PLC 地址格式/类型校验（设备主地址 + 报警 MBit + 缺陷/计数报警 DWord）。
    /// 供 <see cref="CollectValidationErrors"/> 与 <see cref="CollectAddressTypeErrors"/> 共用，
    /// 保证"保存前全量校验"与"导入前拦截"口径一致。
    /// </summary>
    private static void AddAddressTypeErrors(List<DeviceConfigError> errors, Device device, IPlcAddressCodec codec)
    {
        AddAddressError(errors, device, codec, device.OkCountAddress, PlcAddressType.DWord, 0, Strings.M232);
        AddAddressError(errors, device, codec, device.NgCountAddress, PlcAddressType.DWord, 0, Strings.M233);
        AddAddressError(errors, device, codec, device.StatusCountAddress, PlcAddressType.DWord, 0, Strings.M230);
        AddAddressError(errors, device, codec, device.ProductionResetAddress, PlcAddressType.DWord, 0, Strings.M234);
        AddAddressError(errors, device, codec, device.RecipeAddress, PlcAddressType.DWord, 0, Strings.M235);
        foreach (var alarm in device.Alarms)
            AddAddressError(errors, device, codec, alarm.PlcAddress, PlcAddressType.MBit, 1, string.Format(Strings.F128, alarm.Name));
        foreach (var defect in device.Defects)
            AddAddressError(errors, device, codec, defect.PlcAddress, PlcAddressType.DWord, 2, string.Format(Strings.F189, defect.Name));
        foreach (var counterAlarm in device.CounterAlarms)
            AddAddressError(errors, device, codec, counterAlarm.PlcAddress, PlcAddressType.DWord, 3, string.Format(Strings.F199, counterAlarm.Name));
    }

    /// <summary>
    /// 只收集"PLC 地址格式/类型"错误（不含名称唯一性、跨设备地址冲突、阈值关系等结构性校验）。
    /// 审查新增 2026-09-06：供配置**导入**路径在替换/加入设备前拦截"缺陷地址填成 M 位/乱码"
    /// 这类非法值——此前 JSON 导入完全不做地址校验，成了绕过保存校验的口子。
    /// 刻意**不**检测跨设备地址冲突：导入"地址待改的样板设备"再逐台修改是常见工作流，
    /// 立即拦截会让这条路走不通；冲突类结构性问题仍由保存时的 <see cref="CollectValidationErrors"/> 统一把关。
    /// </summary>
    public static List<DeviceConfigError> CollectAddressTypeErrors(
        IEnumerable<Device> devices,
        IPlcAddressCodec? addressCodec = null)
    {
        List<DeviceConfigError> errors = [];
        var deviceList = devices as IList<Device> ?? devices.ToList();
        addressCodec ??= new MitsubishiAddressCodec();
        foreach (var device in deviceList)
            AddAddressTypeErrors(errors, device, addressCodec);
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
        foreach (var s in d.Sources)
        {
            yield return s.TriggerAddress;
            foreach (var v in s.Values) yield return v.PlcAddress;
        }
    }
}

/// <summary>结构化跨设备地址冲突：冲突地址、涉及设备与各设备应定位的 Tab 索引。</summary>
public sealed record DeviceAddressConflict(
    string Address,
    IReadOnlyList<Device> Devices,
    int TargetTabIndex)
{
    /// <summary>按设备 Id 保存冲突地址在该设备中的实际配置 Tab。</summary>
    public IReadOnlyDictionary<string, int> TargetTabIndices { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public int GetTargetTabIndex(Device device)
        => TargetTabIndices.TryGetValue(device.Id, out var tabIndex) ? tabIndex : TargetTabIndex;
}
