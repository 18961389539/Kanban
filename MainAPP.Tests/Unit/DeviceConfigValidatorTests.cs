using System.Linq;
using Kanban.Core.Models;
using MainAPP.Models;
using Kanban.Core.Services;
using MainAPP.Services;
using Xunit;

namespace MainAPP.Tests.Unit;


[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class DeviceConfigValidatorTests
{
    private static Device ValidDevice(string name, string addrPrefix)
        => new Device
        {
            Name = name,
            TargetCycle = 100,
            OkCountAddress = addrPrefix + "0",
            NgCountAddress = addrPrefix + "2",
            StatusCountAddress = addrPrefix + "4",
            ProductionResetAddress = addrPrefix + "6",
        };

    [Fact]
    public void CollectValidationErrors_ValidDevice_NoErrors()
    {
        var device = ValidDevice("设备1", "D100");
        var errors = DeviceConfigValidator.CollectValidationErrors(new[] { device });
        Assert.Empty(errors);
    }

    [Fact]
    public void CollectValidationErrors_MissingAddress_ReportsError()
    {
        var device = ValidDevice("设备1", "D100");
        device.NgCountAddress = "";
        var errors = DeviceConfigValidator.CollectValidationErrors(new[] { device });
        var e = Assert.Single(errors);
        Assert.Equal(0, e.TargetTabIndex);
        Assert.Contains("未配置", e.Message);
    }

    [Fact]
    public void CollectValidationErrors_TargetCycleNotPositive_ReportsError()
    {
        var device = ValidDevice("设备1", "D100");
        device.TargetCycle = 0;
        var errors = DeviceConfigValidator.CollectValidationErrors(new[] { device });
        var e = Assert.Single(errors);
        Assert.Contains("目标周期必须 > 0", e.Message);
    }

    [Fact]
    public void CollectValidationErrors_DuplicateDeviceName_ReportsError()
    {
        var a = ValidDevice("同名牌", "D100");
        var b = ValidDevice("同名牌", "D200");
        var errors = DeviceConfigValidator.CollectValidationErrors(new[] { a, b });
        Assert.Contains(errors, e => e.Message.Contains("设备名重复") && e.TargetTabIndex == 0);
    }

    [Fact]
    public void CollectValidationErrors_DuplicateAlarmName_ReportsError()
    {
        var device = ValidDevice("设备1", "D100");
        device.Alarms.Add(new Alarm { DeviceId = device.Id, Name = "报警X", PlcAddress = "M1" });
        device.Alarms.Add(new Alarm { DeviceId = device.Id, Name = "报警X", PlcAddress = "M2" });
        var errors = DeviceConfigValidator.CollectValidationErrors(new[] { device });
        Assert.Contains(errors, e => e.Message.Contains("报警名重复") && e.TargetTabIndex == 1);
    }

    [Fact]
    public void CollectValidationErrors_DuplicateAlarmPlcAddress_ReportsError()
    {
        var device = ValidDevice("设备1", "D100");
        device.Alarms.Add(new Alarm { DeviceId = device.Id, Name = "A1", PlcAddress = "M9" });
        device.Alarms.Add(new Alarm { DeviceId = device.Id, Name = "A2", PlcAddress = "M9" });
        var errors = DeviceConfigValidator.CollectValidationErrors(new[] { device });
        Assert.Contains(errors, e => e.Message.Contains("重复报警 PLC 地址") && e.TargetTabIndex == 1);
    }

    [Fact]
    public void CollectValidationErrors_DuplicateDefectName_ReportsError()
    {
        var device = ValidDevice("设备1", "D100");
        device.Defects.Add(new Defect { DeviceId = device.Id, Name = "缺陷X", PlcAddress = "D1" });
        device.Defects.Add(new Defect { DeviceId = device.Id, Name = "缺陷X", PlcAddress = "D2" });
        var errors = DeviceConfigValidator.CollectValidationErrors(new[] { device });
        Assert.Contains(errors, e => e.Message.Contains("缺陷名重复") && e.TargetTabIndex == 2);
    }

    [Fact]
    public void CollectValidationErrors_CountAlarmMaxValueNotPositive_ReportsError()
    {
        var device = ValidDevice("设备1", "D100");
        device.CountAlarms.Add(new CountAlarm { DeviceId = device.Id, Name = "计数报警", PlcAddress = "D9", MaxValue = 0 });
        var errors = DeviceConfigValidator.CollectValidationErrors(new[] { device });
        Assert.Contains(errors, e => e.Message.Contains("阈值上限必须 > 0") && e.TargetTabIndex == 3);
    }

    [Fact]
    public void CollectCrossDeviceConflicts_SharedAddress_ReportsConflict()
    {
        var a = ValidDevice("A", "D100");
        var b = ValidDevice("B", "D200");
        b.OkCountAddress = a.OkCountAddress; // 跨设备共用地址
        var errors = DeviceConfigValidator.CollectCrossDeviceConflicts(new[] { a, b });
        var e = Assert.Single(errors);
        Assert.Equal(0, e.TargetTabIndex);
        Assert.Contains("地址冲突", e.Message);
    }

    [Fact]
    public void CollectCrossDeviceConflicts_DistinctAddresses_NoErrors()
    {
        var a = ValidDevice("A", "D100");
        var b = ValidDevice("B", "D200");
        var errors = DeviceConfigValidator.CollectCrossDeviceConflicts(new[] { a, b });
        Assert.Empty(errors);
    }

    [Fact]
    public void CollectValidationErrors_SiemensAddresses_UsesSiemensCodec()
    {
        var device = new Device
        {
            Name = "S7设备",
            TargetCycle = 100,
            OkCountAddress = "DB1.DBD100",
            NgCountAddress = "DB1.DBD104",
            StatusCountAddress = "MD100",
            ProductionResetAddress = "MD104",
        };
        var errors = DeviceConfigValidator.CollectValidationErrors(
            new[] { device }, new PlcAddressCodecResolver(new AppSettings()).Resolve(PlcBrand.Siemens));

        Assert.DoesNotContain(errors, error => error.Message.Contains("地址无效"));
    }

    [Fact]
    public void CollectValidationErrors_MitsubishiCodec_RejectsSiemensAddress()
    {
        var device = ValidDevice("三菱设备", "D100");
        device.OkCountAddress = "DB1.DBD100";
        var errors = DeviceConfigValidator.CollectValidationErrors(new[] { device });

        Assert.Contains(errors, error => error.Message.Contains("OK 数量地址无效"));
    }
}
