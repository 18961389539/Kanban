using System.Linq;
using Kanban.Collector.Core.Models;
using MainAPP.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using Xunit;

namespace MainAPP.Tests.Unit;


[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
[Collection("LocalizationSensitive")]
public class DeviceConfigValidatorTests
{
    /// <summary>
    /// 本类断言依赖中文文案（Strings.F20x）。
    /// 并行测试（LocalizationTests）会调用 Localization.Apply(En/Ja) 污染静态 captured culture，
    /// 导致本类拿到日文/英文消息而断言失败。每个用例实例化时锁定 zh-CN。
    /// </summary>
    public DeviceConfigValidatorTests()
    {
        Localization.Apply(AppLanguage.Zh);
    }

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

// ──────────── 数据源值项校验（修复 2026-08-17：空地址/重复地址） ────────────

    [Fact]
    public void CollectValidationErrors_SourceValueMissingAddress_ReportsError()
    {
        var device = ValidDevice("设备1", "D100");
        var source = new DataSource { DeviceId = device.Id, Name = "温湿度" };
        source.Values.Add(new DataSourceValue { Name = "温度" }); // 无采集地址
        device.Sources.Add(source);

        var errors = DeviceConfigValidator.CollectValidationErrors(new[] { device });

        var error = Assert.Single(errors);
        Assert.Contains("未配置采集地址", error.Message);
        Assert.Equal((int)DeviceManagerTab.Sources, error.TargetTabIndex);
    }

    [Fact]
    public void CollectValidationErrors_DuplicateSourceValueAddress_ReportsError()
    {
        var device = ValidDevice("设备1", "D100");
        var source = new DataSource { Name = "温湿度" };
        source.Values.Add(new DataSourceValue { Name = "温度", PlcAddress = "D300" });
        source.Values.Add(new DataSourceValue { Name = "湿度", PlcAddress = "D300" }); // 重复地址
        device.Sources.Add(source);

        var errors = DeviceConfigValidator.CollectValidationErrors(new[] { device });

        Assert.Contains(errors, e => e.Message.Contains("相同采集地址"));
    }

    [Fact]
    public void CollectValidationErrors_SiemensSourceTriggerAndValueAlias_ReportsConflict()
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
        var source = new DataSource
        {
            DeviceId = device.Id,
            Name = "温度源",
            TriggerAddress = "DB1.DBD0",
        };
        source.Values.Add(new DataSourceValue { Name = "温度", PlcAddress = "DB1.0" });
        device.Sources.Add(source);

        var errors = DeviceConfigValidator.CollectValidationErrors(
            new[] { device }, new PlcAddressCodecResolver(new AppSettings()).Resolve(PlcBrand.Siemens));

        Assert.Contains(errors, e => e.Message.Contains("不能与触发地址相同"));
    }

    [Fact]
    public void CollectValidationErrors_SiemensSourceValueAliases_ReportsDuplicate()
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
        var source = new DataSource { DeviceId = device.Id, Name = "温度源" };
        source.Values.Add(new DataSourceValue { Name = "温度", PlcAddress = "DB1.DBD0" });
        source.Values.Add(new DataSourceValue { Name = "温度别名", PlcAddress = "DB1.0" });
        device.Sources.Add(source);

        var errors = DeviceConfigValidator.CollectValidationErrors(
            new[] { device }, new PlcAddressCodecResolver(new AppSettings()).Resolve(PlcBrand.Siemens));

        Assert.Contains(errors, e => e.Message.Contains("相同采集地址"));
    }

    [Fact]
    public void CollectValidationErrors_ValidSource_NoSourceErrors()
    {
        var device = ValidDevice("设备1", "D100");
        var source = new DataSource { DeviceId = device.Id, Name = "温湿度", TriggerAddress = "D510" };
        source.Values.Add(new DataSourceValue { Name = "温度", PlcAddress = "D300" });
        device.Sources.Add(source);

        var errors = DeviceConfigValidator.CollectValidationErrors(new[] { device });

        Assert.DoesNotContain(errors, e => e.TargetTabIndex == (int)DeviceManagerTab.Sources);
    }

    [Fact]
    public void CollectValidationErrors_FloatExpectedValueWithLimits_ReportsConflict()
    {
        var device = ValidDevice("设备1", "D100");
        var source = new DataSource { DeviceId = device.Id, Name = "温度" };
        source.Values.Add(new DataSourceValue
        {
            Name = "温度值",
            PlcAddress = "D300",
            DataType = DataSourceValueType.Float32,
            FloatLimitMin = 0,
            FloatLimitMax = 100,
            FloatExpectedValue = 50,
        });
        device.Sources.Add(source);

        var errors = DeviceConfigValidator.CollectValidationErrors(new[] { device });

        var error = Assert.Single(errors);
        Assert.Contains("不能同时配置上下限和预期值", error.Message);
        Assert.Equal((int)DeviceManagerTab.Sources, error.TargetTabIndex);
    }

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
        Assert.Contains("目标产能必须 > 0", e.Message);
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
    public void CollectValidationErrors_CounterAlarmMaxValueZero_AllowedAsNoTrigger()
    {
        // 0 值阈值 = 仅记录不触发（IsTriggered 已按 MaxValue > 0 防护），保存不应被阻断
        var device = ValidDevice("设备1", "D100");
        device.CounterAlarms.Add(new CounterAlarm { DeviceId = device.Id, Name = "计数报警", PlcAddress = "D9", MaxValue = 0 });
        var errors = DeviceConfigValidator.CollectValidationErrors(new[] { device });
        Assert.DoesNotContain(errors, e => e.Message.Contains("阈值上限必须 > 0"));
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
