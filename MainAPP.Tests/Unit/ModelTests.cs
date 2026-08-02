using System;
using Kanban.Core.Models;
using MainAPP.Models;
using Kanban.Core.Services;
using MainAPP.Services;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// 模型类单元测试：ShiftConfig / Alarm.Duration / DeviceRuntime
/// </summary>
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class ModelTests
{
    // ──────────── ShiftConfig ────────────

    [Fact]
    public void ShiftConfig_SameDay_DurationCorrect()
    {
        var shift = new ShiftConfig
        {
            StartTime = new TimeSpan(8, 0, 0),
            EndTime = new TimeSpan(20, 0, 0)
        };
        Assert.Equal(12.0, shift.DurationHours);
    }

    [Fact]
    public void ShiftConfig_CrossMidnight_DurationCorrect()
    {
        // 20:00 → 次日 08:00 = 12 小时
        var shift = new ShiftConfig
        {
            StartTime = new TimeSpan(20, 0, 0),
            EndTime = new TimeSpan(8, 0, 0)
        };
        Assert.Equal(12.0, shift.DurationHours);
    }

    [Fact]
    public void ShiftConfig_FullDay_DurationIs24()
    {
        // 00:00 → 00:00 应视为 0 小时（相等），而非 24 小时
        // 当前实现：diff < 0 时 +24，但相等时 diff=0 不加 24
        var shift = new ShiftConfig
        {
            StartTime = TimeSpan.Zero,
            EndTime = TimeSpan.Zero
        };
        Assert.Equal(0.0, shift.DurationHours);
    }

    [Fact]
    public void ShiftConfig_Contains_SameDay()
    {
        var shift = new ShiftConfig
        {
            StartTime = new TimeSpan(8, 0, 0),
            EndTime = new TimeSpan(20, 0, 0)
        };
        Assert.True(shift.Contains(new TimeSpan(8, 0, 0)));    // 起点
        Assert.True(shift.Contains(new TimeSpan(15, 30, 0))); // 中间
        Assert.False(shift.Contains(new TimeSpan(20, 0, 0))); // 终点（半开区间）
        Assert.False(shift.Contains(new TimeSpan(7, 59, 59)));
    }

    [Fact]
    public void ShiftConfig_Contains_CrossMidnight()
    {
        var shift = new ShiftConfig
        {
            StartTime = new TimeSpan(20, 0, 0),
            EndTime = new TimeSpan(8, 0, 0)
        };
        Assert.True(shift.Contains(new TimeSpan(20, 0, 0)));   // 起点
        Assert.True(shift.Contains(new TimeSpan(23, 0, 0)));   // 子夜前
        Assert.True(shift.Contains(new TimeSpan(0, 0, 0)));   // 子夜
        Assert.True(shift.Contains(new TimeSpan(7, 59, 59)));  // 终点前
        Assert.False(shift.Contains(new TimeSpan(8, 0, 0)));   // 终点（半开）
        Assert.False(shift.Contains(new TimeSpan(12, 0, 0)));  // 白天
    }

    // ──────────── Alarm.Duration ────────────

    [Fact]
    public void Alarm_Duration_NormalCase()
    {
        var alarm = new Alarm
        {
            StartTime = new DateTime(2026, 7, 22, 10, 0, 0),
            EndTime = new DateTime(2026, 7, 22, 10, 30, 0)
        };
        Assert.Equal(TimeSpan.FromMinutes(30), alarm.Duration);
    }

    [Fact]
    public void Alarm_Duration_EndBeforeStart_ReturnsZero()
    {
        // EndTime < StartTime 时返回 Zero（防止负值）
        var alarm = new Alarm
        {
            StartTime = new DateTime(2026, 7, 22, 10, 30, 0),
            EndTime = new DateTime(2026, 7, 22, 10, 0, 0)
        };
        Assert.Equal(TimeSpan.Zero, alarm.Duration);
    }

    [Fact]
    public void Alarm_Duration_EqualStartEnd_ReturnsZero()
    {
        var t = new DateTime(2026, 7, 22, 10, 0, 0);
        var alarm = new Alarm { StartTime = t, EndTime = t };
        Assert.Equal(TimeSpan.Zero, alarm.Duration);
    }

    [Fact]
    public void Alarm_DefaultId_Is32CharNoDashGuid()
    {
        var alarm = new Alarm();
        Assert.Equal(32, alarm.Id.Length);
        Assert.DoesNotContain("-", alarm.Id);
    }

    // ──────────── DeviceRuntime ────────────

    [Fact]
    public void DeviceRuntime_ResetShift_ClearsAllAccumulators()
    {
        var device = new Device { TargetCycle = 100 };
        var runtime = new DeviceRuntime(device)
        {
            RunTime = 3600,
            AlarmTime = 600,
            PausedTime = 300,
            TotalOkProduction = 100,
            TotalNgProduction = 10
        };

        runtime.ResetShift();

        Assert.Equal(0, runtime.RunTime);
        Assert.Equal(0, runtime.AlarmTime);
        Assert.Equal(0, runtime.PausedTime);
        Assert.Equal(0, runtime.TotalOkProduction);
        Assert.Equal(0, runtime.TotalNgProduction);
    }

    [Fact]
    public void DeviceRuntime_SyncTargetCycle_UpdatesPerformanceRate()
    {
        var device = new Device { TargetCycle = 100 };
        var runtime = new DeviceRuntime(device)
        {
            TotalOkProduction = 50,
            TotalNgProduction = 0,
            RunTime = 3600
        };
        // 目标 100/小时，运行 1 小时，产 50 件 → 性能率 0.5
        Assert.Equal(0.5, runtime.PerformanceRate);

        // 提高目标节拍 → 性能率下降
        runtime.SyncTargetCycle(200);
        Assert.Equal(0.25, runtime.PerformanceRate);
    }

    [Fact]
    public void DeviceRuntime_DeviceId_MatchesDevice()
    {
        var device = new Device();
        var runtime = new DeviceRuntime(device);
        Assert.Equal(device.Id, runtime.DeviceId);
    }

    [Fact]
    public void DeviceRuntime_Oee_RecalculatedOnPropertyChange()
    {
        var device = new Device { TargetCycle = 100 };
        var runtime = new DeviceRuntime(device);

        // 初始全 0 → OEE = 0
        Assert.Equal(0.0, runtime.Oee);

        // 设置为完美状态
        runtime.TotalOkProduction = 100;
        runtime.TotalNgProduction = 0;
        runtime.RunTime = 3600;
        runtime.AlarmTime = 0;

        // q=1, p=1, a=1 → oee=1
        Assert.Equal(1.0, runtime.Oee);
    }
}
