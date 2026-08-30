using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using Kanban.Collector.Core.Models;
using MainAPP.Services;
using MainAPP.ViewModels;
using Xunit;

namespace MainAPP.Tests.Unit;

public class AlarmNameLocalizationTests
{
    [Fact]
    public void DisplayName_UsesNameEn_WhenCultureIsEnglish()
    {
        Localization.Apply("en-US");
        var info = new ActiveAlarmInfo(DateTime.Now, "d1", "注塑机1", "温控偏差-4", AlarmLevel.Medium, AlarmKind.Plc,
            "Temperature Control Deviation-4");
        Assert.Equal("Temperature Control Deviation-4", info.DisplayName);
    }

    [Fact]
    public void DisplayName_UsesAppliedLanguage_EvenIfThreadCultureIsChinese()
    {
        Localization.Apply("en-US");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
        var info = new ActiveAlarmInfo(DateTime.Now, "d1", "注塑机1", "温度过高", AlarmLevel.High, AlarmKind.Plc,
            "Over Temperature");
        Assert.Equal("Over Temperature", info.DisplayName);
    }

    [Fact]
    public void HomeAlarmCollector_Refresh_UsesLatestNameEn_NotStaleInstance()
    {
        Localization.Apply("en-US");
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
}
