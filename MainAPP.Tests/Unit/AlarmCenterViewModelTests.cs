using System;
using System.Linq;
using Kanban.Core.Data;
using Kanban.Core.Entities;
using Kanban.Core.Models;
using MainAPP.Models;
using Kanban.Core.Services;
using MainAPP.Services;
using MainAPP.ViewModels;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// AlarmCenterViewModel 单元测试：覆盖实时活跃报警收集、级别筛选、KPI 聚合。
///
/// 测试策略：
/// - 使用真实 DeviceRepository + 内存设备配置（含 Alarms/CountAlarms）
/// - 使用 InMemoryHistoryService 注入报警事件
/// - 使用 FakeDialogService
/// - 不启动 DispatcherTimer（构造函数仅创建定时器但不 Start），避免 STA 线程依赖
///
/// 覆盖范围：
/// - 构造函数初始化：空设备不抛异常
/// - RefreshActiveAlarms：收集 PLC 边沿报警 + 计数报警
/// - 级别筛选：ShowHigh/Medium/Low 切换后列表更新
/// - KPI 聚合：ActiveCount / AffectedDeviceCount / LongestDurationText
/// - 时间范围切换：SelectedTimeRange 变化触发 RefreshStats
/// </summary>
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class AlarmCenterViewModelTests : IDisposable
{
    private readonly AppSettings _appSettings;
    private readonly DeviceRepository _deviceRepo;
    private readonly InMemoryHistoryService _historyService;
    private readonly FakeDialogService _dialog;

    public AlarmCenterViewModelTests()
    {
        _appSettings = new AppSettings();
        _deviceRepo = new DeviceRepository(_appSettings);
        _historyService = new InMemoryHistoryService();
        _dialog = new FakeDialogService();
    }

    public void Dispose()
    {
        // AlarmCenterViewModel.Dispose 停止定时器；VM 在每个测试中 using 释放
    }

    private AlarmCenterViewModel CreateVm()
        => new(_historyService, _deviceRepo, _dialog);

    private static Device CreateDeviceWithAlarm(
        string id = "d1",
        string name = "设备1",
        string alarmName = "温度过高",
        AlarmLevel level = AlarmLevel.High,
        bool active = true)
    {
        var device = new Device { Id = id, Name = name };
        var alarm = new Alarm
        {
            Name = alarmName,
            Level = level,
            PlcAddress = "M100",
        };
        if (active)
        {
            alarm.StartTime = DateTime.Now.AddMinutes(-5);
            alarm.EndTime = default;  // 未恢复 = 活跃
        }
        device.Alarms.Add(alarm);
        return device;
    }

    // ──────────── 构造函数 ────────────

    [Fact]
    public void Constructor_WithNoDevices_DoesNotThrow()
    {
        using var vm = CreateVm();
        Assert.Empty(vm.ActiveAlarms);
        Assert.Equal(0, vm.ActiveCount);
    }

    [Fact]
    public void Constructor_InitializesDefaultTimeRange()
    {
        using var vm = CreateVm();
        Assert.Equal(AlarmCenterTimeRange.Hours4, vm.SelectedTimeRange);
        Assert.True(vm.IsHours4);
        Assert.False(vm.IsHour1);
        Assert.False(vm.IsHours24);
    }

    // ──────────── RefreshActiveAlarms：PLC 边沿报警 ────────────

    [Fact]
    public void RefreshAll_CollectsActivePlcAlarms()
    {
        var device = CreateDeviceWithAlarm("d1", "设备1", "温度过高", AlarmLevel.High, active: true);
        _deviceRepo.Devices.Add(device);

        using var vm = CreateVm();
        vm.RefreshAllCommand.Execute(null);

        Assert.Single(vm.ActiveAlarms);
        Assert.Equal("温度过高", vm.ActiveAlarms[0].AlarmName);
        Assert.Equal("设备1", vm.ActiveAlarms[0].DeviceName);
        Assert.Equal(AlarmLevel.High, vm.ActiveAlarms[0].Level);
    }

    [Fact]
    public void RefreshAll_FiltersRecoveredAlarms()
    {
        var device = new Device { Id = "d1", Name = "设备1" };
        var activeAlarm = new Alarm
        {
            Name = "压力异常", Level = AlarmLevel.High, PlcAddress = "M100",
            StartTime = DateTime.Now.AddMinutes(-3), EndTime = default,
        };
        var recoveredAlarm = new Alarm
        {
            Name = "温度过高", Level = AlarmLevel.Medium, PlcAddress = "M101",
            StartTime = DateTime.Now.AddMinutes(-10),
            EndTime = DateTime.Now.AddMinutes(-1),  // 已恢复
        };
        device.Alarms.Add(activeAlarm);
        device.Alarms.Add(recoveredAlarm);
        _deviceRepo.Devices.Add(device);

        using var vm = CreateVm();
        vm.RefreshAllCommand.Execute(null);

        Assert.Single(vm.ActiveAlarms);
        Assert.Equal("压力异常", vm.ActiveAlarms[0].AlarmName);
    }

    [Fact]
    public void RefreshAll_CollectsTriggeredCountAlarms()
    {
        var device = new Device { Id = "d1", Name = "设备1" };
        device.CountAlarms.Add(new CountAlarm
        {
            Name = "不合格计数超限", Enabled = true, MaxValue = 50, CurrentValue = 100,
        });
        _deviceRepo.Devices.Add(device);

        using var vm = CreateVm();
        vm.RefreshAllCommand.Execute(null);

        Assert.Single(vm.ActiveAlarms);
        Assert.Equal("不合格计数超限", vm.ActiveAlarms[0].AlarmName);
        Assert.Equal(AlarmKind.Count, vm.ActiveAlarms[0].Kind);

        var firstAlarm = vm.ActiveAlarms[0];
        var firstEventTime = firstAlarm.EventTime;
        vm.RefreshAllCommand.Execute(null);

        Assert.Same(firstAlarm, vm.ActiveAlarms[0]);
        Assert.Equal(firstEventTime, vm.ActiveAlarms[0].EventTime);
    }

    [Fact]
    public void RefreshAll_FiltersDisabledCountAlarms()
    {
        var device = new Device { Id = "d1", Name = "设备1" };
        device.CountAlarms.Add(new CountAlarm
        {
            Name = "已禁用计数", Enabled = false, MaxValue = 50, CurrentValue = 100,
        });
        _deviceRepo.Devices.Add(device);

        using var vm = CreateVm();
        vm.RefreshAllCommand.Execute(null);

        Assert.Empty(vm.ActiveAlarms);
    }

    // ──────────── 级别筛选 ────────────

    [Fact]
    public void ShowHighAlarms_False_HidesHighLevelAlarms()
    {
        var device = new Device { Id = "d1", Name = "设备1" };
        device.Alarms.Add(new Alarm
        {
            Name = "高级报警", Level = AlarmLevel.High, PlcAddress = "M100",
            StartTime = DateTime.Now.AddMinutes(-5),
        });
        device.Alarms.Add(new Alarm
        {
            Name = "低级报警", Level = AlarmLevel.Low, PlcAddress = "M101",
            StartTime = DateTime.Now.AddMinutes(-3),
        });
        _deviceRepo.Devices.Add(device);

        using var vm = CreateVm();
        vm.ShowHighAlarms = false;  // 隐藏高级

        // ShowHighAlarms 变化触发 RefreshActiveAlarms
        Assert.Single(vm.ActiveAlarms);
        Assert.Equal("低级报警", vm.ActiveAlarms[0].AlarmName);
        Assert.Equal(2, vm.ActiveCount);
    }

    [Fact]
    public void ShowAllLevelsFalse_EmptyActiveAlarms()
    {
        var device = CreateDeviceWithAlarm("d1", "设备1", active: true);
        _deviceRepo.Devices.Add(device);

        using var vm = CreateVm();
        vm.ShowHighAlarms = false;
        vm.ShowMediumAlarms = false;
        vm.ShowLowAlarms = false;

        Assert.Empty(vm.ActiveAlarms);
        Assert.Equal(1, vm.ActiveCount);
        Assert.Equal("当前筛选无匹配报警", vm.ActiveEmptyStateMessage);
    }

    // ──────────── KPI 聚合 ────────────

    [Fact]
    public void RefreshAll_UpdatesActiveCount()
    {
        var device1 = CreateDeviceWithAlarm("d1", "设备1", "报警A", active: true);
        var device2 = CreateDeviceWithAlarm("d2", "设备2", "报警B", active: true);
        _deviceRepo.Devices.Add(device1);
        _deviceRepo.Devices.Add(device2);

        using var vm = CreateVm();
        vm.RefreshAllCommand.Execute(null);

        Assert.Equal(2, vm.ActiveCount);
    }

    [Fact]
    public void RefreshAll_UpdatesAffectedDeviceCount()
    {
        // 同一设备 2 条报警 → 影响设备数 = 1
        var device = new Device { Id = "d1", Name = "设备1" };
        device.Alarms.Add(new Alarm
        {
            Name = "报警A", Level = AlarmLevel.High, PlcAddress = "M100",
            StartTime = DateTime.Now.AddMinutes(-5),
        });
        device.Alarms.Add(new Alarm
        {
            Name = "报警B", Level = AlarmLevel.Medium, PlcAddress = "M101",
            StartTime = DateTime.Now.AddMinutes(-3),
        });
        _deviceRepo.Devices.Add(device);

        using var vm = CreateVm();
        vm.RefreshAllCommand.Execute(null);

        Assert.Equal(2, vm.ActiveCount);
        Assert.Equal(1, vm.AffectedDeviceCount);
    }

    [Fact]
    public void RefreshAll_UpdatesLongestDurationText()
    {
        var device = new Device { Id = "d1", Name = "设备1" };
        device.Alarms.Add(new Alarm
        {
            Name = "最早报警", Level = AlarmLevel.High, PlcAddress = "M100",
            StartTime = DateTime.Now.AddMinutes(-30),  // 30 分钟前
        });
        device.Alarms.Add(new Alarm
        {
            Name = "较新报警", Level = AlarmLevel.Medium, PlcAddress = "M101",
            StartTime = DateTime.Now.AddMinutes(-2),   // 2 分钟前
        });
        _deviceRepo.Devices.Add(device);

        using var vm = CreateVm();
        vm.RefreshAllCommand.Execute(null);

        // 最长持续 = 最早触发的那条（30 分钟前）
        Assert.NotEqual("—", vm.LongestDurationText);
        Assert.Contains("30", vm.LongestDurationText);
    }

    [Fact]
    public void RefreshAll_NoActiveAlarms_LongestDurationIsDash()
    {
        using var vm = CreateVm();
        vm.RefreshAllCommand.Execute(null);
        Assert.Equal("—", vm.LongestDurationText);
    }

    // ──────────── 时间范围切换 ────────────

    [Fact]
    public void IsHour1_SetTrue_UpdatesSelectedTimeRange()
    {
        using var vm = CreateVm();
        vm.IsHour1 = true;
        Assert.Equal(AlarmCenterTimeRange.Hour1, vm.SelectedTimeRange);
        Assert.True(vm.IsHour1);
        Assert.False(vm.IsHours4);
    }

    [Fact]
    public void IsHours24_SetTrue_UpdatesSelectedTimeRange()
    {
        using var vm = CreateVm();
        vm.IsHours24 = true;
        Assert.Equal(AlarmCenterTimeRange.Hours24, vm.SelectedTimeRange);
        Assert.True(vm.IsHours24);
    }

    // ──────────── 排序 ────────────

    [Fact]
    public void RefreshActiveAlarms_SortsByLevelDescThenTimeAsc()
    {
        var device = new Device { Id = "d1", Name = "设备1" };
        // 低级但更早
        device.Alarms.Add(new Alarm
        {
            Name = "低级早", Level = AlarmLevel.Low, PlcAddress = "M100",
            StartTime = DateTime.Now.AddMinutes(-30),
        });
        // 高级但较晚
        device.Alarms.Add(new Alarm
        {
            Name = "高级晚", Level = AlarmLevel.High, PlcAddress = "M101",
            StartTime = DateTime.Now.AddMinutes(-1),
        });
        // 中级中等
        device.Alarms.Add(new Alarm
        {
            Name = "中级中", Level = AlarmLevel.Medium, PlcAddress = "M102",
            StartTime = DateTime.Now.AddMinutes(-10),
        });
        _deviceRepo.Devices.Add(device);

        using var vm = CreateVm();
        vm.RefreshAllCommand.Execute(null);

        // 排序：级别降序（High → Medium → Low），同级别时间升序
        Assert.Equal(3, vm.ActiveAlarms.Count);
        Assert.Equal("高级晚", vm.ActiveAlarms[0].AlarmName);    // High
        Assert.Equal("中级中", vm.ActiveAlarms[1].AlarmName);    // Medium
        Assert.Equal("低级早", vm.ActiveAlarms[2].AlarmName);    // Low
    }

    // ──────────── 上限保护 ────────────

    [Fact]
    public void RefreshActiveAlarms_RespectsMaxActiveAlarmsLimit()
    {
        // 生成超过 200 条活跃报警，验证截断
        for (var i = 0; i < 250; i++)
        {
            var device = new Device { Id = $"d{i}", Name = $"设备{i}" };
            device.Alarms.Add(new Alarm
            {
                Name = $"报警{i}", Level = AlarmLevel.High, PlcAddress = $"M{i}",
                StartTime = DateTime.Now.AddMinutes(-i),
            });
            _deviceRepo.Devices.Add(device);
        }

        using var vm = CreateVm();
        vm.RefreshAllCommand.Execute(null);

        // MaxActiveAlarms = 200
        Assert.Equal(250, vm.ActiveCount);
        Assert.Equal(200, vm.ActiveAlarms.Count);
    }

    [Fact]
    public void RefreshActiveAlarms_TruncatesAfterGlobalPrioritySort()
    {
        var firstDevice = new Device { Id = "d-first", Name = "前置设备" };
        for (var i = 0; i < 200; i++)
        {
            firstDevice.Alarms.Add(new Alarm
            {
                Name = $"低级报警{i}",
                Level = AlarmLevel.Low,
                PlcAddress = $"M{i}",
                StartTime = DateTime.Now.AddMinutes(-i),
            });
        }

        var laterDevice = CreateDeviceWithAlarm("d-later", "后置设备", "后置高级报警", AlarmLevel.High, active: true);
        _deviceRepo.Devices.Add(firstDevice);
        _deviceRepo.Devices.Add(laterDevice);

        using var vm = CreateVm();
        vm.RefreshAllCommand.Execute(null);

        Assert.Equal(201, vm.ActiveCount);
        Assert.Equal(200, vm.ActiveAlarms.Count);
        Assert.Equal("后置高级报警", vm.ActiveAlarms[0].AlarmName);
    }
}
