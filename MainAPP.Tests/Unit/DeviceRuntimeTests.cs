using Kanban.Collector.Core.Models;
using MainAPP.Models;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// 设备运行时状态（DeviceRuntime）单元测试。
/// 覆盖 OEE 计算属性、ResetShift、SyncTargetCycle、StatusWord 读写。
/// 纯内存、无 PLC/DB 依赖，与 OeeCalculator 口径一致（可用率不含 PausedTime）。
/// </summary>
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class DeviceRuntimeTests
{
    private static Device MakeDevice(int targetCycle = 100) => new() { TargetCycle = targetCycle };

    [Fact]
    public void OeeProperties_ComputeFromSessionCounters()
    {
        var rt = new DeviceRuntime(MakeDevice(targetCycle: 100));
        rt.TotalOkProduction = 90;
        rt.TotalNgProduction = 10;
        rt.RunTime = 3600;
        rt.AlarmTime = 0;

        // 合格率 = 90 / (90+10) = 0.9
        Assert.Equal(0.9, rt.QualityRate);
        // 性能率 = (90+10) / (100 * (3600/3600)) = 100 / 100 = 1.0
        Assert.Equal(1.0, rt.PerformanceRate);
        // 可用率 = 3600 / (3600 + 0) = 1.0（不含 PausedTime）
        Assert.Equal(1.0, rt.AvailabilityRate);
        // OEE = 0.9 * 1.0 * 1.0 = 0.9
        Assert.Equal(0.9, rt.Oee);
    }

    [Fact]
    public void AvailabilityRate_ExcludesPausedTime()
    {
        var rt = new DeviceRuntime(MakeDevice());
        rt.RunTime = 1800;
        rt.AlarmTime = 600;
        rt.PausedTime = 9999; // 暂停时间不应进入分母

        // 1800 / (1800 + 600) = 0.75
        Assert.Equal(0.75, rt.AvailabilityRate);
    }

    [Fact]
    public void QualityRate_ZeroTotal_Returns0_NotNaN()
    {
        var rt = new DeviceRuntime(MakeDevice());
        rt.TotalOkProduction = 0;
        rt.TotalNgProduction = 0;
        Assert.Equal(0.0, rt.QualityRate);
    }

    [Fact]
    public void ResetShift_ClearsAllSessionData()
    {
        var rt = new DeviceRuntime(MakeDevice(targetCycle: 100));
        rt.TotalOkProduction = 50;
        rt.TotalNgProduction = 5;
        rt.RunTime = 1000;
        rt.AlarmTime = 200;
        rt.PausedTime = 300;
        rt.OkProduction = 999;
        rt.NgProduction = 888;
        rt.StatusWord = 2;

        rt.ResetShift();

        Assert.Equal(0, rt.TotalOkProduction);
        Assert.Equal(0, rt.TotalNgProduction);
        Assert.Equal(0, rt.RunTime);
        Assert.Equal(0, rt.AlarmTime);
        Assert.Equal(0, rt.PausedTime);
        // 原始 PLC 值不在此方法清理范围（用于实时显示）
        Assert.Equal(2, rt.StatusWord);
        // OEE 应全部归零
        Assert.Equal(0.0, rt.Oee);
    }

    [Fact]
    public void SyncTargetCycle_UpdatesPerformanceRate()
    {
        var rt = new DeviceRuntime(MakeDevice(targetCycle: 100));
        rt.TotalOkProduction = 200;
        rt.TotalNgProduction = 0;
        rt.RunTime = 3600;

        // 目标 100 个/小时 → 性能率 = 200 / 100 = 2.0 → clamp 到 1
        Assert.Equal(1.0, rt.PerformanceRate);

        rt.SyncTargetCycle(200);
        Assert.Equal(200, rt.TargetCycle);
        // 目标 200 个/小时 → 性能率 = 200 / 200 = 1.0
        Assert.Equal(1.0, rt.PerformanceRate);

        rt.SyncTargetCycle(400);
        // 目标 400 个/小时 → 性能率 = 200 / 400 = 0.5
        Assert.Equal(0.5, rt.PerformanceRate);
    }

    [Fact]
    public void StatusWord_ReadWrite()
    {
        var rt = new DeviceRuntime(MakeDevice());
        rt.StatusWord = 3; // 暂停
        Assert.Equal(3, rt.StatusWord);
        rt.StatusWord = 1; // 运行
        Assert.Equal(1, rt.StatusWord);
    }
}
