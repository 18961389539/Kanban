using System.ComponentModel;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// DeviceSelectionService 单元测试。
/// 验证选中设备 Id 的默认值、属性变更通知、防回环行为及接口实现。
/// </summary>
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class DeviceSelectionServiceTests
{
    // ──────────── 默认值 ────────────

    [Fact]
    public void SelectedDeviceId_Default_IsNull()
    {
        var svc = new DeviceSelectionService();
        Assert.Null(svc.SelectedDeviceId);
    }

    // ──────────── PropertyChanged 触发 ────────────

    [Fact]
    public void Set_NewValue_RaisesPropertyChanged()
    {
        var svc = new DeviceSelectionService();
        var fired = false;
        svc.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IDeviceSelectionService.SelectedDeviceId))
                fired = true;
        };

        svc.SelectedDeviceId = "device-001";

        Assert.True(fired);
        Assert.Equal("device-001", svc.SelectedDeviceId);
    }

    [Fact]
    public void Set_SameValue_DoesNotRaisePropertyChanged()
    {
        // 防回环：VM 收到变更后写回 service，值相同不应重复触发
        var svc = new DeviceSelectionService { SelectedDeviceId = "device-001" };
        var count = 0;
        svc.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IDeviceSelectionService.SelectedDeviceId))
                count++;
        };

        svc.SelectedDeviceId = "device-001"; // 同值再写

        Assert.Equal(0, count);
    }

    // ──────────── 非 null 值设置 ────────────

    [Fact]
    public void Set_NonNullValue_StoredCorrectly()
    {
        var svc = new DeviceSelectionService();
        svc.SelectedDeviceId = "ABC-123";
        Assert.Equal("ABC-123", svc.SelectedDeviceId);
    }

    // ──────────── 非 null 重置为 null ────────────

    [Fact]
    public void Set_FromNonNullToNull_RaisesPropertyChanged_AndClearsValue()
    {
        var svc = new DeviceSelectionService { SelectedDeviceId = "device-001" };
        var fired = false;
        svc.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IDeviceSelectionService.SelectedDeviceId))
                fired = true;
        };

        svc.SelectedDeviceId = null;

        Assert.True(fired);
        Assert.Null(svc.SelectedDeviceId);
    }

    // ──────────── 接口实现 ────────────

    [Fact]
    public void Implements_IDeviceSelectionService()
    {
        IDeviceSelectionService svc = new DeviceSelectionService();
        Assert.NotNull(svc);
    }

    [Fact]
    public void Implements_INotifyPropertyChanged()
    {
        INotifyPropertyChanged svc = new DeviceSelectionService();
        Assert.NotNull(svc);
    }
}
