using System.Collections.ObjectModel;
using Kanban.Collector.Core.Localization;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using Kanban.Contracts.Dtos;

namespace Kanban.Collector.Core.Mapping;

/// <summary>
/// Collector 设置的跨进程映射单一事实源。
/// 只负责字段转换和部分更新，不负责校验、落盘或运行时热切换。
/// </summary>
public static class CollectorSettingsMapper
{
    public static CollectorSettingsDto ToDto(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var plc = settings.PlcConfig;
        var profiles = settings.CreateConnectionProfilesSnapshot();
        lock (settings.ShiftsLock)
        {
            return new CollectorSettingsDto
            {
                Language = (int)AppSettings.LegacyLanguage(settings.EffectiveLanguageCode),
                LanguageCode = settings.EffectiveLanguageCode,
                PollingIntervalMs = settings.PollingIntervalMs,
                HistoryWriteIntervalScans = settings.HistoryWriteIntervalScans,
                PlcBatchReadMaxLength = settings.PlcBatchReadMaxLength,
                PlcBatchReadMaxGapSlots = settings.PlcBatchReadMaxGapSlots,
                PlcBrand = (int)plc.Brand,
                PlcProtocolKey = plc.ProtocolKey,
                PlcIpAddress = plc.IpAddress,
                PlcPort = plc.Port,
                PlcTimeoutMs = plc.TimeoutMs,
                Siemens = new SiemensSettingsDto
                {
                    Model = plc.Siemens.Model,
                    Rack = plc.Siemens.Rack,
                    Slot = plc.Siemens.Slot,
                    DataFormat = (int)plc.Siemens.DataFormat,
                    BatchInt32Limit = plc.Siemens.BatchInt32Limit,
                },
                ModbusTcp = new ModbusTcpSettingsDto
                {
                    UnitId = plc.ModbusTcp.UnitId,
                    AddressStartWithZero = plc.ModbusTcp.AddressStartWithZero,
                    RegisterFunction = plc.ModbusTcp.RegisterFunction,
                    BitFunction = plc.ModbusTcp.BitFunction,
                    DataFormat = (int)plc.ModbusTcp.DataFormat,
                    BatchInt32Limit = plc.ModbusTcp.BatchInt32Limit,
                },
                Omron = new OmronFinsSettingsDto
                {
                    ReadSplits = plc.Omron.ReadSplits,
                },
                ConnectionProfiles = profiles.Select(ToDto).ToList(),
                Shifts = settings.Shifts.Select(shift => new ShiftConfigDto
                {
                    Name = shift.Name,
                    StartTime = shift.StartTime,
                    EndTime = shift.EndTime,
                }).ToList(),
            };
        }
    }

    /// <summary>
    /// 将 DTO 中的非空字段应用到目标设置。空班次列表保持当前语义：不覆盖现有班次。
    /// </summary>
    public static void ApplyPatch(CollectorSettingsDto dto, AppSettings target)
    {
        ArgumentNullException.ThrowIfNull(dto);
        ArgumentNullException.ThrowIfNull(target);

        if (!string.IsNullOrWhiteSpace(dto.LanguageCode))
        {
            if (!LocalizationCatalog.IsSupported(dto.LanguageCode))
                throw new ArgumentOutOfRangeException(nameof(dto.LanguageCode), dto.LanguageCode, "不支持的界面语言");
            target.LanguageCode = LocalizationCatalog.Normalize(dto.LanguageCode);
        }
        else if (dto.Language.HasValue)
        {
            if (!Enum.IsDefined(typeof(AppLanguage), dto.Language.Value))
                throw new ArgumentOutOfRangeException(nameof(dto.Language), dto.Language.Value, "不支持的界面语言");
            target.Language = (AppLanguage)dto.Language.Value;
            target.LanguageCode = AppSettings.LegacyLanguageCode(target.Language);
        }

        if (dto.PollingIntervalMs.HasValue) target.PollingIntervalMs = dto.PollingIntervalMs.Value;
        if (dto.HistoryWriteIntervalScans.HasValue) target.HistoryWriteIntervalScans = dto.HistoryWriteIntervalScans.Value;
        if (dto.PlcBatchReadMaxLength.HasValue) target.PlcBatchReadMaxLength = dto.PlcBatchReadMaxLength.Value;
        if (dto.PlcBatchReadMaxGapSlots.HasValue) target.PlcBatchReadMaxGapSlots = dto.PlcBatchReadMaxGapSlots.Value;

        if (dto.PlcBrand.HasValue)
        {
            if (!Enum.IsDefined(typeof(PlcBrand), dto.PlcBrand.Value))
                throw new ArgumentOutOfRangeException(nameof(dto.PlcBrand), dto.PlcBrand.Value, "不支持的 PLC 品牌");
            target.PlcConfig.Brand = (PlcBrand)dto.PlcBrand.Value;
        }
        if (!string.IsNullOrWhiteSpace(dto.PlcProtocolKey))
            target.PlcConfig.ProtocolKey = dto.PlcProtocolKey;
        if (!string.IsNullOrWhiteSpace(dto.PlcIpAddress)) target.PlcConfig.IpAddress = dto.PlcIpAddress;
        if (dto.PlcPort.HasValue) target.PlcConfig.Port = dto.PlcPort.Value;
        if (dto.PlcTimeoutMs.HasValue) target.PlcConfig.TimeoutMs = dto.PlcTimeoutMs.Value;

        if (dto.Siemens is { } siemens)
        {
            if (!string.IsNullOrWhiteSpace(siemens.Model)) target.PlcConfig.Siemens.Model = siemens.Model;
            if (siemens.Rack.HasValue) target.PlcConfig.Siemens.Rack = siemens.Rack.Value;
            if (siemens.Slot.HasValue) target.PlcConfig.Siemens.Slot = siemens.Slot.Value;
            if (siemens.DataFormat.HasValue)
            {
                if (!Enum.IsDefined(typeof(PlcDataFormat), siemens.DataFormat.Value))
                    throw new ArgumentOutOfRangeException(nameof(dto.Siemens), siemens.DataFormat.Value, "不支持的 Siemens 数据格式");
                target.PlcConfig.Siemens.DataFormat = (PlcDataFormat)siemens.DataFormat.Value;
            }
            if (siemens.BatchInt32Limit.HasValue) target.PlcConfig.Siemens.BatchInt32Limit = siemens.BatchInt32Limit.Value;
        }

        if (dto.ModbusTcp is { } modbus)
        {
            if (modbus.UnitId.HasValue) target.PlcConfig.ModbusTcp.UnitId = modbus.UnitId.Value;
            if (modbus.AddressStartWithZero.HasValue) target.PlcConfig.ModbusTcp.AddressStartWithZero = modbus.AddressStartWithZero.Value;
            if (modbus.RegisterFunction.HasValue) target.PlcConfig.ModbusTcp.RegisterFunction = modbus.RegisterFunction.Value;
            if (modbus.BitFunction.HasValue) target.PlcConfig.ModbusTcp.BitFunction = modbus.BitFunction.Value;
            if (modbus.DataFormat.HasValue)
            {
                if (!Enum.IsDefined(typeof(PlcDataFormat), modbus.DataFormat.Value))
                    throw new ArgumentOutOfRangeException(nameof(dto.ModbusTcp), modbus.DataFormat.Value, "不支持的 Modbus 数据格式");
                target.PlcConfig.ModbusTcp.DataFormat = (PlcDataFormat)modbus.DataFormat.Value;
            }
            if (modbus.BatchInt32Limit.HasValue) target.PlcConfig.ModbusTcp.BatchInt32Limit = modbus.BatchInt32Limit.Value;
        }

        if (dto.Omron is { ReadSplits: { } readSplits })
            target.PlcConfig.Omron.ReadSplits = readSplits;

        if (dto.ConnectionProfiles is { Count: > 0 } profiles)
        {
            var profileIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var profileDto in profiles)
            {
                if (string.IsNullOrWhiteSpace(profileDto.Id))
                    throw new ArgumentException(ValidationMessages.ConnectionProfileIdRequired, nameof(dto.ConnectionProfiles));
                var profileId = profileDto.Id.Trim();
                if (!profileIds.Add(profileId))
                    throw new ArgumentException(string.Format(ValidationMessages.ConnectionProfileIdDuplicate, profileId), nameof(dto.ConnectionProfiles));

                var profile = target.FindConnectionProfile(profileId);
                if (profile is null)
                {
                    profile = new ConnectionProfile
                    {
                        Id = profileId,
                        Name = string.IsNullOrWhiteSpace(profileDto.Name)
                            ? profileId
                            : profileDto.Name,
                    };
                    target.ConnectionProfiles.Add(profile);
                }
                ApplyProfilePatch(profileDto, profile);
            }
        }
        target.SynchronizeDefaultConnectionProfile();

        if (dto.Shifts is { Count: > 0 })
        {
            target.Shifts = new ObservableCollection<ShiftConfig>(dto.Shifts.Select(shift => new ShiftConfig
            {
                Name = shift.Name,
                StartTime = shift.StartTime,
                EndTime = shift.EndTime,
            }));
        }
    }

    private static ConnectionProfileSettingsDto ToDto(ConnectionProfile profile)
    {
        var plc = profile.Config;
        return new ConnectionProfileSettingsDto
        {
            Id = profile.Id,
            Name = profile.Name,
            PlcBrand = (int)plc.Brand,
            ProtocolKey = plc.ProtocolKey,
            IpAddress = plc.IpAddress,
            Port = plc.Port,
            TimeoutMs = plc.TimeoutMs,
            Siemens = new SiemensSettingsDto
            {
                Model = plc.Siemens.Model,
                Rack = plc.Siemens.Rack,
                Slot = plc.Siemens.Slot,
                DataFormat = (int)plc.Siemens.DataFormat,
                BatchInt32Limit = plc.Siemens.BatchInt32Limit,
            },
            ModbusTcp = new ModbusTcpSettingsDto
            {
                UnitId = plc.ModbusTcp.UnitId,
                AddressStartWithZero = plc.ModbusTcp.AddressStartWithZero,
                RegisterFunction = plc.ModbusTcp.RegisterFunction,
                BitFunction = plc.ModbusTcp.BitFunction,
                DataFormat = (int)plc.ModbusTcp.DataFormat,
                BatchInt32Limit = plc.ModbusTcp.BatchInt32Limit,
            },
            Omron = new OmronFinsSettingsDto { ReadSplits = plc.Omron.ReadSplits },
        };
    }

    private static void ApplyProfilePatch(ConnectionProfileSettingsDto dto, ConnectionProfile profile)
    {
        var plc = profile.Config;
        if (!string.IsNullOrWhiteSpace(dto.Name)) profile.Name = dto.Name.Trim();
        if (dto.PlcBrand.HasValue)
        {
            if (!Enum.IsDefined(typeof(PlcBrand), dto.PlcBrand.Value))
                throw new ArgumentOutOfRangeException(nameof(dto.PlcBrand), dto.PlcBrand.Value, "不支持的 PLC 品牌");
            plc.Brand = (PlcBrand)dto.PlcBrand.Value;
        }
        if (!string.IsNullOrWhiteSpace(dto.ProtocolKey)) plc.ProtocolKey = dto.ProtocolKey;
        if (!string.IsNullOrWhiteSpace(dto.IpAddress)) plc.IpAddress = dto.IpAddress;
        if (dto.Port.HasValue) plc.Port = dto.Port.Value;
        if (dto.TimeoutMs.HasValue) plc.TimeoutMs = dto.TimeoutMs.Value;

        if (dto.Siemens is { } siemens)
        {
            if (!string.IsNullOrWhiteSpace(siemens.Model)) plc.Siemens.Model = siemens.Model;
            if (siemens.Rack.HasValue) plc.Siemens.Rack = siemens.Rack.Value;
            if (siemens.Slot.HasValue) plc.Siemens.Slot = siemens.Slot.Value;
            if (siemens.DataFormat.HasValue)
            {
                if (!Enum.IsDefined(typeof(PlcDataFormat), siemens.DataFormat.Value))
                    throw new ArgumentOutOfRangeException(nameof(dto.Siemens), siemens.DataFormat.Value, "不支持的 Siemens 数据格式");
                plc.Siemens.DataFormat = (PlcDataFormat)siemens.DataFormat.Value;
            }
            if (siemens.BatchInt32Limit.HasValue) plc.Siemens.BatchInt32Limit = siemens.BatchInt32Limit.Value;
        }

        if (dto.ModbusTcp is { } modbus)
        {
            if (modbus.UnitId.HasValue) plc.ModbusTcp.UnitId = modbus.UnitId.Value;
            if (modbus.AddressStartWithZero.HasValue) plc.ModbusTcp.AddressStartWithZero = modbus.AddressStartWithZero.Value;
            if (modbus.RegisterFunction.HasValue) plc.ModbusTcp.RegisterFunction = modbus.RegisterFunction.Value;
            if (modbus.BitFunction.HasValue) plc.ModbusTcp.BitFunction = modbus.BitFunction.Value;
            if (modbus.DataFormat.HasValue)
            {
                if (!Enum.IsDefined(typeof(PlcDataFormat), modbus.DataFormat.Value))
                    throw new ArgumentOutOfRangeException(nameof(dto.ModbusTcp), modbus.DataFormat.Value, "不支持的 Modbus 数据格式");
                plc.ModbusTcp.DataFormat = (PlcDataFormat)modbus.DataFormat.Value;
            }
            if (modbus.BatchInt32Limit.HasValue) plc.ModbusTcp.BatchInt32Limit = modbus.BatchInt32Limit.Value;
        }

        if (dto.Omron is { ReadSplits: { } readSplits })
            plc.Omron.ReadSplits = readSplits;
    }
}