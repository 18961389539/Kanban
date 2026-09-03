using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using MainAPP.ViewModels;
using Xunit;

namespace MainAPP.Tests.Unit;

[Collection("LocalizationSensitive")]
public class AlarmNameLocalizationTests
{
    [Fact]
    public void DisplayName_UsesNameEn_WhenCultureIsEnglish()
    {
        Localization.Apply("en-US");
        try
        {
            var info = new ActiveAlarmInfo(DateTime.Now, "d1", "注塑机1", "温控偏差-4", AlarmLevel.Medium, AlarmKind.Plc,
                "Temperature Control Deviation-4");
            Assert.Equal("Temperature Control Deviation-4", info.DisplayName);
        }
        finally
        {
            Localization.Apply("zh-CN");
        }
    }

    [Fact]
    public void DisplayName_UsesAppliedLanguage_EvenIfThreadCultureIsChinese()
    {
        Localization.Apply("en-US");
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
            var info = new ActiveAlarmInfo(DateTime.Now, "d1", "注塑机1", "温度过高", AlarmLevel.High, AlarmKind.Plc,
                "Over Temperature");
            Assert.Equal("Over Temperature", info.DisplayName);
        }
        finally
        {
            Localization.Apply("zh-CN");
        }
    }

    [Fact]
    public void HomeAlarmCollector_Refresh_UsesLatestNameEn_NotStaleInstance()
    {
        Localization.Apply("en-US");
        try
        {
        var device = new Device { Id = "d1", Name = "注塑机1" };
        var alarm = new Alarm
        {
            Name = "温控偏差-4",
            NameEn = null,
            PlcAddress = "M1",
            Level = AlarmLevel.Medium,
            StartTime = DateTime.Now.AddMinutes(-1),
        };
        device.Alarms.Add(alarm);

        var target = new ObservableCollection<ActiveAlarmInfo>();
        var collector = new HomeAlarmCollector();

        // 首次刷新：NameEn 尚未写入配置（旧实例会永久中文）
        collector.Refresh(target, [device], DateTime.Now, isMuted: true, maxAlarms: 5, selectedDeviceId: "d1");
        Assert.Equal("温控偏差-4", Assert.Single(target).DisplayName);

        alarm.NameEn = "Temperature Control Deviation-4";
        collector.Refresh(target, [device], DateTime.Now, isMuted: true, maxAlarms: 5, selectedDeviceId: "d1");

        Assert.Equal("Temperature Control Deviation-4", Assert.Single(target).DisplayName);
        }
        finally
        {
            Localization.Apply("zh-CN");
        }
    }

    [Fact]
    public void HomeAlarmCollector_Refresh_UsesCounterAlarmStartTime()
    {
        var triggerTime = DateTime.Now.AddMinutes(-15);
        var device = new Device { Id = "d1", Name = "设备1" };
        device.CounterAlarms.Add(new CounterAlarm
        {
            Name = "计数超限", Enabled = true, MaxValue = 10, CurrentValue = 20, StartTime = triggerTime,
        });

        var target = new ObservableCollection<ActiveAlarmInfo>();
        var collector = new HomeAlarmCollector();
        collector.Refresh(target, [device], DateTime.Now, isMuted: true, maxAlarms: 5, selectedDeviceId: "d1");

        Assert.Equal(triggerTime, Assert.Single(target).EventTime);
        Assert.Equal(AlarmKind.Count, target[0].Kind);
    }

    [Fact]
    public void HomeAlarmCollector_Refresh_IncludesPendingDataSourceAlarm()
    {
        var device = new Device { Id = "d1", Name = "设备1" };
        var source = new DataSource { Name = "温湿度" };
        var value = new DataSourceValue { Name = "温度" };
        source.Values.Add(value);
        device.Sources.Add(source);
        var deviceRepo = new DeviceRepository(new AppSettings());
        deviceRepo.Devices.Add(device);

        var pending = new List<ActiveAlarmStateRecord>
        {
            new()
            {
                DeviceId = "d1",
                DeviceName = "设备1",
                AlarmId = $"src:{value.Id}",
                AlarmName = "温湿度-温度",
                IsActive = true,
                TriggeredAt = DateTime.Now.AddMinutes(-4),
            },
        };

        var target = new ObservableCollection<ActiveAlarmInfo>();
        var collector = new HomeAlarmCollector();
        collector.Refresh(target, [device], DateTime.Now, isMuted: true, maxAlarms: 5,
            selectedDeviceId: "d1", activeSourceStates: pending, deviceRepository: deviceRepo);

        Assert.Equal(AlarmKind.DataSource, Assert.Single(target).Kind);
    }
}
