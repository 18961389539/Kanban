using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Kanban.Collector.Core.Models;
using MainAPP.Helpers;
using MainAPP.ViewModels;
using Xunit;

namespace MainAPP.Tests.Unit;


[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public sealed class ObservableCollectionSyncHelperTests
{
    private sealed class Item
    {
        public Item(string id) => Id = id;

        public string Id { get; }
    }

    private static object AlarmKey(ActiveAlarmInfo a)
        => (a.DeviceId, a.AlarmName, a.Level, a.Kind, a.EventTime);

    [Fact]
    public void Sync_RemovesMissingItemsAndAppendsNewItems()
    {
        var retained = new Item("retained");
        var removed = new Item("removed");
        var added = new Item("added");
        var target = new ObservableCollection<Item> { retained, removed };

        ObservableCollectionSyncHelper.Sync(target, new List<Item> { retained, added });

        Assert.Equal(new[] { retained, added }, target);
    }

    [Fact]
    public void Sync_PreservesReferencesForUnchangedPositions()
    {
        var first = new Item("first");
        var second = new Item("second");
        var target = new ObservableCollection<Item> { first, second };

        ObservableCollectionSyncHelper.Sync(target, new List<Item> { first, second });

        Assert.Same(first, target[0]);
        Assert.Same(second, target[1]);
    }

    [Fact]
    public void Sync_ReplacesItemsWhenOrderChanges()
    {
        var first = new Item("first");
        var second = new Item("second");
        var target = new ObservableCollection<Item> { first, second };

        ObservableCollectionSyncHelper.Sync(target, new List<Item> { second, first });

        Assert.Same(second, target[0]);
        Assert.Same(first, target[1]);
    }

    [Fact]
    public void Sync_EmptySourceClearsTarget()
    {
        var target = new ObservableCollection<Item> { new("one"), new("two") };

        ObservableCollectionSyncHelper.Sync(target, new List<Item>());

        Assert.Empty(target);
    }

    // ──────────── 报警中心 O(N²) + 差分失效修复（2026-09-01）────────────

    [Fact]
    public void Sync_UnchangedReferences_LargeScale_ProducesNoEvents()
    {
        const int n = 500;
        var items = Enumerable.Range(0, n).Select(i => new Item("item" + i)).ToList();
        var target = new ObservableCollection<Item>(items);

        var events = new List<NotifyCollectionChangedAction>();
        target.CollectionChanged += (_, e) => events.Add(e.Action);

        // 引用一致的大规模同步：HashSet 化后 O(N)，且零集合事件
        //（原 Contains 线性扫版本同为零事件，此用例锁定该契约不因优化回归）
        ObservableCollectionSyncHelper.Sync(target, items.ToList());

        Assert.Empty(events);
    }

    [Fact]
    public void ReuseExisting_ThenSync_IdenticalAlarm_ProducesZeroCollectionEvents()
    {
        // 模拟报警中心每 3s 刷新：静止报警每次 new 全新对象，但身份键（含 EventTime）相同
        var alarm = new ActiveAlarmInfo(DateTime.Today.AddHours(1), "D1", "设备1", "报警A", AlarmLevel.High, AlarmKind.Plc);
        var target = new ObservableCollection<ActiveAlarmInfo> { alarm };

        var events = new List<NotifyCollectionChangedAction>();
        target.CollectionChanged += (_, e) => events.Add(e.Action);

        var desired = new List<ActiveAlarmInfo>
        {
            new(DateTime.Today.AddHours(1), "D1", "设备1", "报警A", AlarmLevel.High, AlarmKind.Plc),
        };

        ObservableCollectionSyncHelper.ReuseExisting(target, desired, AlarmKey);
        ObservableCollectionSyncHelper.Sync(target, desired);

        // 静止报警：零集合事件（原先是 Remove+Add 全量重建，每 3s 400 次通知 + 200 个模板重建）
        Assert.Empty(events);
        // 实例被复用：DurationText 等 [ObservableProperty] 可原地刷新驱动 UI
        Assert.Same(alarm, target[0]);
    }

    [Fact]
    public void ReuseExisting_EventTimeChanged_AlarmIsReplacedNotReused()
    {
        // 报警重新触发：EventTime 变化 → 键不同 → 不复用，UI 取到新触发时刻
        var old = new ActiveAlarmInfo(DateTime.Today.AddHours(1), "D1", "设备1", "报警A", AlarmLevel.High, AlarmKind.Plc);
        var target = new ObservableCollection<ActiveAlarmInfo> { old };

        var retriggered = new ActiveAlarmInfo(DateTime.Today.AddHours(2), "D1", "设备1", "报警A", AlarmLevel.High, AlarmKind.Plc);
        var desired = new List<ActiveAlarmInfo> { retriggered };

        ObservableCollectionSyncHelper.ReuseExisting(target, desired, AlarmKey);
        ObservableCollectionSyncHelper.Sync(target, desired);

        Assert.Same(retriggered, target[0]);
        Assert.Equal(DateTime.Today.AddHours(2), target[0].EventTime);
    }

    [Fact]
    public void ReuseExisting_NewAndRemovedAlarms_AppliedBySync()
    {
        var kept = new ActiveAlarmInfo(DateTime.Today.AddHours(1), "D1", "设备1", "报警A", AlarmLevel.High, AlarmKind.Plc);
        var gone = new ActiveAlarmInfo(DateTime.Today.AddHours(2), "D1", "设备1", "报警B", AlarmLevel.Medium, AlarmKind.Count);
        var target = new ObservableCollection<ActiveAlarmInfo> { kept, gone };

        var fresh = new ActiveAlarmInfo(DateTime.Today.AddHours(3), "D2", "设备2", "报警C", AlarmLevel.Low, AlarmKind.DataSource);
        var desired = new List<ActiveAlarmInfo>
        {
            // 报警A：同键全新对象（应复用 kept 实例）
            new(DateTime.Today.AddHours(1), "D1", "设备1", "报警A", AlarmLevel.High, AlarmKind.Plc),
            fresh,
        };

        ObservableCollectionSyncHelper.ReuseExisting(target, desired, AlarmKey);
        ObservableCollectionSyncHelper.Sync(target, desired);

        Assert.Equal(2, target.Count);
        Assert.Same(kept, target[0]);   // 复用
        Assert.Same(fresh, target[1]);  // 新增
        Assert.DoesNotContain(gone, target); // 移除
    }
}
