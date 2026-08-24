using System.Collections.ObjectModel;
using Kanban.Collector.Core.Mapping;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using Kanban.Contracts.Dtos;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>Collector 设置跨进程映射契约：字段、嵌套 PLC 参数和部分更新语义。</summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class CollectorSettingsMapperTests
{
    [Fact]
    public void ToDto_CopiesNestedPlcOptionsAndShifts()
    {
        var settings = CreateSettings();

        var dto = CollectorSettingsMapper.ToDto(settings);

        Assert.Equal(settings.PollingIntervalMs, dto.PollingIntervalMs);
        Assert.Equal(settings.HistoryWriteIntervalScans, dto.HistoryWriteIntervalScans);
        Assert.Equal(settings.PlcBatchReadMaxLength, dto.PlcBatchReadMaxLength);
        Assert.Equal(settings.PlcBatchReadMaxGapSlots, dto.PlcBatchReadMaxGapSlots);
        Assert.Equal((int)settings.PlcConfig.Brand, dto.PlcBrand);
        Assert.Equal(settings.PlcConfig.IpAddress, dto.PlcIpAddress);
        Assert.Equal(settings.PlcConfig.Port, dto.PlcPort);
        Assert.Equal(settings.PlcConfig.TimeoutMs, dto.PlcTimeoutMs);
        Assert.Equal(settings.PlcConfig.ProtocolKey, dto.PlcProtocolKey);

        Assert.NotNull(dto.Siemens);
        Assert.Equal(settings.PlcConfig.Siemens.Model, dto.Siemens!.Model);
        Assert.Equal(settings.PlcConfig.Siemens.Rack, dto.Siemens.Rack);
        Assert.Equal(settings.PlcConfig.Siemens.Slot, dto.Siemens.Slot);
        Assert.Equal((int)settings.PlcConfig.Siemens.DataFormat, dto.Siemens.DataFormat);
        Assert.Equal(settings.PlcConfig.Siemens.BatchInt32Limit, dto.Siemens.BatchInt32Limit);

        Assert.NotNull(dto.ModbusTcp);
        Assert.Equal(settings.PlcConfig.ModbusTcp.UnitId, dto.ModbusTcp!.UnitId);
        Assert.Equal(settings.PlcConfig.ModbusTcp.AddressStartWithZero, dto.ModbusTcp.AddressStartWithZero);
        Assert.Equal(settings.PlcConfig.ModbusTcp.RegisterFunction, dto.ModbusTcp.RegisterFunction);
        Assert.Equal(settings.PlcConfig.ModbusTcp.BitFunction, dto.ModbusTcp.BitFunction);
        Assert.Equal((int)settings.PlcConfig.ModbusTcp.DataFormat, dto.ModbusTcp.DataFormat);
        Assert.Equal(settings.PlcConfig.ModbusTcp.BatchInt32Limit, dto.ModbusTcp.BatchInt32Limit);

        Assert.NotNull(dto.Omron);
        Assert.Equal(settings.PlcConfig.Omron.ReadSplits, dto.Omron!.ReadSplits);
        Assert.Equal(2, dto.Shifts!.Count);
        Assert.Equal("早班", dto.Shifts[0].Name);
        Assert.Equal(settings.Shifts[1].StartTime, dto.Shifts[1].StartTime);
    }

    [Fact]
    public void ApplyPatch_RoundTripPreservesCollectorSettings()
    {
        var source = CreateSettings();
        var target = new AppSettings();

        CollectorSettingsMapper.ApplyPatch(CollectorSettingsMapper.ToDto(source), target);

        Assert.Equal(source.PollingIntervalMs, target.PollingIntervalMs);
        Assert.Equal(source.HistoryWriteIntervalScans, target.HistoryWriteIntervalScans);
        Assert.Equal(source.PlcBatchReadMaxLength, target.PlcBatchReadMaxLength);
        Assert.Equal(source.PlcBatchReadMaxGapSlots, target.PlcBatchReadMaxGapSlots);
        Assert.Equal(source.PlcConfig.Brand, target.PlcConfig.Brand);
        Assert.Equal(source.PlcConfig.IpAddress, target.PlcConfig.IpAddress);
        Assert.Equal(source.PlcConfig.Port, target.PlcConfig.Port);
        Assert.Equal(source.PlcConfig.TimeoutMs, target.PlcConfig.TimeoutMs);
        Assert.Equal(source.PlcConfig.ProtocolKey, target.PlcConfig.ProtocolKey);
        Assert.Equal(source.PlcConfig.Siemens.Model, target.PlcConfig.Siemens.Model);
        Assert.Equal(source.PlcConfig.Siemens.DataFormat, target.PlcConfig.Siemens.DataFormat);
        Assert.Equal(source.PlcConfig.ModbusTcp.RegisterFunction, target.PlcConfig.ModbusTcp.RegisterFunction);
        Assert.Equal(source.PlcConfig.Omron.ReadSplits, target.PlcConfig.Omron.ReadSplits);
        Assert.Equal(source.Shifts.Select(shift => shift.Name), target.Shifts.Select(shift => shift.Name));
        Assert.Equal(source.Shifts.Select(shift => shift.StartTime), target.Shifts.Select(shift => shift.StartTime));
    }

    [Fact]
    public void ApplyPatch_UpdatesOnlyProvidedFields()
    {
        var target = CreateSettings();
        var originalHistoryInterval = target.HistoryWriteIntervalScans;
        var originalSiemensRack = target.PlcConfig.Siemens.Rack;
        var originalModbusUnitId = target.PlcConfig.ModbusTcp.UnitId;

        CollectorSettingsMapper.ApplyPatch(new CollectorSettingsDto
        {
            PollingIntervalMs = 900,
            PlcIpAddress = "10.0.0.8",
            Siemens = new SiemensSettingsDto
            {
                Model = "S7-1500",
                DataFormat = (int)PlcDataFormat.DCBA,
            },
            ModbusTcp = new ModbusTcpSettingsDto
            {
                RegisterFunction = 4,
            },
        }, target);

        Assert.Equal(900, target.PollingIntervalMs);
        Assert.Equal(originalHistoryInterval, target.HistoryWriteIntervalScans);
        Assert.Equal("10.0.0.8", target.PlcConfig.IpAddress);
        Assert.Equal("S7-1500", target.PlcConfig.Siemens.Model);
        Assert.Equal(PlcDataFormat.DCBA, target.PlcConfig.Siemens.DataFormat);
        Assert.Equal(originalSiemensRack, target.PlcConfig.Siemens.Rack);
        Assert.Equal(4, target.PlcConfig.ModbusTcp.RegisterFunction);
        Assert.Equal(originalModbusUnitId, target.PlcConfig.ModbusTcp.UnitId);
    }

    [Fact]
    public void ApplyPatch_NullFieldsAndEmptyShiftsDoNotOverwrite()
    {
        var target = CreateSettings();
        var originalIp = target.PlcConfig.IpAddress;
        var originalShiftNames = target.Shifts.Select(shift => shift.Name).ToArray();

        CollectorSettingsMapper.ApplyPatch(new CollectorSettingsDto
        {
            PlcIpAddress = "   ",
            Siemens = new SiemensSettingsDto(),
            Shifts = [],
        }, target);

        Assert.Equal(originalIp, target.PlcConfig.IpAddress);
        Assert.Equal(originalShiftNames, target.Shifts.Select(shift => shift.Name).ToArray());
    }

    [Fact]
    public void ToDtoAndApplyPatch_RoundTripsNamedConnectionProfiles()
    {
        var source = CreateSettings();
        source.ConnectionProfiles.Add(new ConnectionProfile
        {
            Id = "line-2",
            Name = "Line 2",
            Config = new PlcConfig
            {
                ProtocolKey = "simulated",
                Brand = PlcBrand.Keyence,
                IpAddress = "10.0.0.20",
                Port = 5001,
                TimeoutMs = 3200,
            },
        });

        var dto = CollectorSettingsMapper.ToDto(source);
        var target = new AppSettings();
        CollectorSettingsMapper.ApplyPatch(dto, target);

        var profileDto = Assert.Single(dto.ConnectionProfiles!, profile => profile.Id == "line-2");
        Assert.Equal("Line 2", profileDto.Name);
        Assert.Equal("simulated", profileDto.ProtocolKey);
        Assert.Equal((int)PlcBrand.Keyence, profileDto.PlcBrand);

        var profile = Assert.Single(target.ConnectionProfiles, item => item.Id == "line-2");
        Assert.Equal("Line 2", profile.Name);
        Assert.Equal("simulated", profile.Config.ProtocolKey);
        Assert.Equal(PlcBrand.Keyence, profile.Config.Brand);
        Assert.Equal("10.0.0.20", profile.Config.IpAddress);
        Assert.Equal(5001, profile.Config.Port);
        Assert.Same(target.PlcConfig, target.DefaultConnectionProfile.Config);
    }

    [Fact]
    public void ApplyPatch_RejectsInvalidEnums()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CollectorSettingsMapper.ApplyPatch(
            new CollectorSettingsDto { PlcBrand = 999 }, new AppSettings()));

        Assert.Throws<ArgumentOutOfRangeException>(() => CollectorSettingsMapper.ApplyPatch(
            new CollectorSettingsDto
            {
                Siemens = new SiemensSettingsDto { DataFormat = 999 },
            }, new AppSettings()));

        Assert.Throws<ArgumentOutOfRangeException>(() => CollectorSettingsMapper.ApplyPatch(
            new CollectorSettingsDto
            {
                ModbusTcp = new ModbusTcpSettingsDto { DataFormat = 999 },
            }, new AppSettings()));
    }

    [Fact]
    public void ApplyPatch_RejectsDuplicateConnectionProfileIds()
    {
        var dto = new CollectorSettingsDto
        {
            ConnectionProfiles =
            [
                new ConnectionProfileSettingsDto { Id = "line-2" },
                new ConnectionProfileSettingsDto { Id = "LINE-2" },
            ],
        };

        var exception = Assert.Throws<ArgumentException>(() =>
            CollectorSettingsMapper.ApplyPatch(dto, new AppSettings()));

        Assert.Contains("连接档案 Id 重复", exception.Message);
    }

    private static AppSettings CreateSettings()
    {
        var settings = new AppSettings
        {
            PollingIntervalMs = 350,
            HistoryWriteIntervalScans = 17,
            PlcBatchReadMaxLength = 48,
            PlcBatchReadMaxGapSlots = 3,
            PlcConfig = new PlcConfig
            {
                ProtocolKey = "simulated",
                Brand = PlcBrand.ModbusTcp,
                IpAddress = "192.168.10.20",
                Port = 1502,
                TimeoutMs = 2300,
                Siemens = new SiemensPlcOptions
                {
                    Model = "S7-1215",
                    Rack = 2,
                    Slot = 3,
                    DataFormat = PlcDataFormat.CDAB,
                    BatchInt32Limit = 41,
                },
                ModbusTcp = new ModbusTcpPlcOptions
                {
                    UnitId = 7,
                    AddressStartWithZero = false,
                    RegisterFunction = 4,
                    BitFunction = 2,
                    DataFormat = PlcDataFormat.BADC,
                    BatchInt32Limit = 37,
                },
                Omron = new OmronFinsPlcOptions { ReadSplits = 120 },
            },
            Shifts = new ObservableCollection<ShiftConfig>
            {
                new() { Name = "早班", StartTime = new TimeSpan(6, 30, 0), EndTime = new TimeSpan(14, 30, 0) },
                new() { Name = "晚班", StartTime = new TimeSpan(14, 30, 0), EndTime = new TimeSpan(22, 30, 0) },
            },
        };
        return settings;
    }
}
