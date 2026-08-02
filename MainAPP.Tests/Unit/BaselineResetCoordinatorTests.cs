using Kanban.Core.Services;
using MainAPP.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// BaselineResetCoordinator 单元测试：覆盖 PLC 清零后的基线清空窗口管理、
/// 重连后待清零设备推迟/触发、设备删除/班次切换清理等核心路径。
///
/// BaselineResetCoordinator 是 PlcDataAcquisitionService 拆分出的协作组件，
/// 拥有 _pendingBaselineClearAt 与 _pendingPlcResetOnReconnect 两个集合及专用锁。
/// 此测试文件独立验证其行为。
/// </summary>
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class BaselineResetCoordinatorTests
{
    private static readonly Microsoft.Extensions.Logging.ILogger<BaselineResetCoordinator> Logger =
        NullLogger<BaselineResetCoordinator>.Instance;

    // ──────────── ScheduleClear / IsInClearWindow ────────────

    [Fact]
    public void IsInClearWindow_NoScheduledClear_ReturnsFalse()
    {
        var coord = new BaselineResetCoordinator();
        Assert.False(coord.IsInClearWindow("dev-1"));
    }

    [Fact]
    public void ScheduleClear_EntersClearWindow()
    {
        var coord = new BaselineResetCoordinator();
        coord.ScheduleClear("dev-1");
        Assert.True(coord.IsInClearWindow("dev-1"));
    }

    [Fact]
    public void ScheduleClear_MultipleDevices_EachEntersWindow()
    {
        var coord = new BaselineResetCoordinator();
        coord.ScheduleClear("dev-1");
        coord.ScheduleClear("dev-2");
        Assert.True(coord.IsInClearWindow("dev-1"));
        Assert.True(coord.IsInClearWindow("dev-2"));
        Assert.False(coord.IsInClearWindow("dev-3"));
    }

    [Fact]
    public void ScheduleClear_OverwritesExistingEntry()
    {
        var coord = new BaselineResetCoordinator();
        coord.ScheduleClear("dev-1", delaySeconds: 10);
        coord.ScheduleClear("dev-1", delaySeconds: 0.5);
        Assert.True(coord.IsInClearWindow("dev-1"));
    }

    // ──────────── ExpireClears ────────────

    [Fact]
    public void ExpireClears_NoScheduledClear_ReturnsZero()
    {
        var coord = new BaselineResetCoordinator();
        Assert.Equal(0, coord.ExpireClears(DateTime.Now, Logger));
    }

    [Fact]
    public void ExpireClears_WithinWindow_ReturnsZeroAndKeepsEntry()
    {
        var coord = new BaselineResetCoordinator();
        var now = DateTime.Now;
        coord.ScheduleClear("dev-1", delaySeconds: 5.0);

        var expired = coord.ExpireClears(now, Logger);

        Assert.Equal(0, expired);
        Assert.True(coord.IsInClearWindow("dev-1"));
    }

    [Fact]
    public void ExpireClears_PastWindow_ReturnsCountAndClearsEntry()
    {
        var coord = new BaselineResetCoordinator();
        coord.ScheduleClear("dev-1", delaySeconds: -1.0); // 已过期
        coord.ScheduleClear("dev-2", delaySeconds: 5.0); // 未过期

        var expired = coord.ExpireClears(DateTime.Now, Logger);

        Assert.Equal(1, expired);
        Assert.False(coord.IsInClearWindow("dev-1"));
        Assert.True(coord.IsInClearWindow("dev-2"));
    }

    [Fact]
    public void ExpireClears_AllExpired_ReturnsCountAndClearsAll()
    {
        var coord = new BaselineResetCoordinator();
        coord.ScheduleClear("dev-1", delaySeconds: -1.0);
        coord.ScheduleClear("dev-2", delaySeconds: -1.0);
        coord.ScheduleClear("dev-3", delaySeconds: -1.0);

        var expired = coord.ExpireClears(DateTime.Now, Logger);

        Assert.Equal(3, expired);
        Assert.False(coord.IsInClearWindow("dev-1"));
        Assert.False(coord.IsInClearWindow("dev-2"));
        Assert.False(coord.IsInClearWindow("dev-3"));
    }

    [Fact]
    public void ExpireClears_AfterExpiry_NextScheduleClearStartsNewWindow()
    {
        var coord = new BaselineResetCoordinator();
        coord.ScheduleClear("dev-1", delaySeconds: -1.0);
        coord.ExpireClears(DateTime.Now, Logger);
        Assert.False(coord.IsInClearWindow("dev-1"));

        // 再次调度应能重新进入窗口
        coord.ScheduleClear("dev-1");
        Assert.True(coord.IsInClearWindow("dev-1"));
    }

    // ──────────── AddPendingReconnect / HasPendingReconnect / PendingReconnectCount ────────────

    [Fact]
    public void AddPendingReconnect_MarksDeviceForDeferredReset()
    {
        var coord = new BaselineResetCoordinator();
        Assert.False(coord.HasPendingReconnect);
        Assert.Equal(0, coord.PendingReconnectCount);

        coord.AddPendingReconnect("dev-1");
        Assert.True(coord.HasPendingReconnect);
        Assert.Equal(1, coord.PendingReconnectCount);
    }

    [Fact]
    public void AddPendingReconnect_DuplicateDevice_DoesNotIncrementCount()
    {
        var coord = new BaselineResetCoordinator();
        coord.AddPendingReconnect("dev-1");
        coord.AddPendingReconnect("dev-1"); // 重复

        Assert.Equal(1, coord.PendingReconnectCount);
    }

    [Fact]
    public void AddPendingReconnect_MultipleDevices_CountAccumulates()
    {
        var coord = new BaselineResetCoordinator();
        coord.AddPendingReconnect("dev-1");
        coord.AddPendingReconnect("dev-2");
        coord.AddPendingReconnect("dev-3");

        Assert.Equal(3, coord.PendingReconnectCount);
    }

    // ──────────── DrainPendingReconnect ────────────

    [Fact]
    public void DrainPendingReconnect_Empty_ReturnsEmptyList()
    {
        var coord = new BaselineResetCoordinator();
        var list = coord.DrainPendingReconnect();
        Assert.Empty(list);
    }

    [Fact]
    public void DrainPendingReconnect_ReturnsAllPendingDevicesAndClearsSet()
    {
        var coord = new BaselineResetCoordinator();
        coord.AddPendingReconnect("dev-1");
        coord.AddPendingReconnect("dev-2");

        var list = coord.DrainPendingReconnect();

        Assert.Equal(2, list.Count);
        Assert.Contains("dev-1", list);
        Assert.Contains("dev-2", list);
        Assert.False(coord.HasPendingReconnect);
    }

    [Fact]
    public void DrainPendingReconnect_CalledTwice_SecondCallReturnsEmpty()
    {
        var coord = new BaselineResetCoordinator();
        coord.AddPendingReconnect("dev-1");

        var first = coord.DrainPendingReconnect();
        var second = coord.DrainPendingReconnect();

        Assert.Single(first);
        Assert.Empty(second);
    }

    // ──────────── RemoveDevice ────────────

    [Fact]
    public void RemoveDevice_ClearsClearWindowEntry()
    {
        var coord = new BaselineResetCoordinator();
        coord.ScheduleClear("dev-1");
        coord.RemoveDevice("dev-1");
        Assert.False(coord.IsInClearWindow("dev-1"));
    }

    [Fact]
    public void RemoveDevice_ClearsPendingReconnectEntry()
    {
        var coord = new BaselineResetCoordinator();
        coord.AddPendingReconnect("dev-1");
        coord.RemoveDevice("dev-1");
        Assert.False(coord.HasPendingReconnect);
    }

    [Fact]
    public void RemoveDevice_UnknownDevice_NoThrow()
    {
        var coord = new BaselineResetCoordinator();
        coord.RemoveDevice("non-existent");
    }

    [Fact]
    public void RemoveDevice_OnlyAffectsSpecifiedDevice()
    {
        var coord = new BaselineResetCoordinator();
        coord.ScheduleClear("dev-1");
        coord.ScheduleClear("dev-2");
        coord.AddPendingReconnect("dev-1");
        coord.AddPendingReconnect("dev-2");

        coord.RemoveDevice("dev-1");

        Assert.False(coord.IsInClearWindow("dev-1"));
        Assert.True(coord.IsInClearWindow("dev-2"));
        Assert.Equal(1, coord.PendingReconnectCount);
    }

    // ──────────── ResetAll ────────────

    [Fact]
    public void ResetAll_ClearsAllClearWindows()
    {
        var coord = new BaselineResetCoordinator();
        coord.ScheduleClear("dev-1");
        coord.ScheduleClear("dev-2");

        coord.ResetAll();

        Assert.False(coord.IsInClearWindow("dev-1"));
        Assert.False(coord.IsInClearWindow("dev-2"));
    }

    [Fact]
    public void ResetAll_ClearsAllPendingReconnects()
    {
        var coord = new BaselineResetCoordinator();
        coord.AddPendingReconnect("dev-1");
        coord.AddPendingReconnect("dev-2");

        coord.ResetAll();

        Assert.False(coord.HasPendingReconnect);
        Assert.Equal(0, coord.PendingReconnectCount);
    }

    [Fact]
    public void ResetAll_OnEmptyCoordinator_NoThrow()
    {
        var coord = new BaselineResetCoordinator();
        coord.ResetAll();
    }

    // ──────────── 组合场景 ────────────

    [Fact]
    public void ScheduleClear_AfterResetAll_EntersNewWindow()
    {
        var coord = new BaselineResetCoordinator();
        coord.ScheduleClear("dev-1");
        coord.ResetAll();

        coord.ScheduleClear("dev-1");
        Assert.True(coord.IsInClearWindow("dev-1"));
    }

    [Fact]
    public void FullCycle_ScheduleClear_Expire_DrainReconnect_NoResidue()
    {
        var coord = new BaselineResetCoordinator();
        coord.ScheduleClear("dev-1", delaySeconds: -1.0); // 已过期
        coord.AddPendingReconnect("dev-2");
        coord.ScheduleClear("dev-3", delaySeconds: 5.0); // 未过期

        var expired = coord.ExpireClears(DateTime.Now, Logger);
        Assert.Equal(1, expired);

        var drained = coord.DrainPendingReconnect();
        Assert.Single(drained);
        Assert.Contains("dev-2", drained);

        // 残留检查
        Assert.False(coord.IsInClearWindow("dev-1"));
        Assert.False(coord.IsInClearWindow("dev-2"));
        Assert.True(coord.IsInClearWindow("dev-3"));
        Assert.False(coord.HasPendingReconnect);
    }
}
