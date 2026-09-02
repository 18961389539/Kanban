using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Kanban.Collector.Core.Data;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// BulkObservableCollection 单元测试：验证批量作用域的事件塌缩语义
///（N 次修改只抛 1 次 Reset、无变更不抛、嵌套只算最外层、异常不泄漏抑制状态）。
/// 该语义是工单页 O(W²·K) 修复的基石，回归时优先看这里。
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public class BulkObservableCollectionTests
{
    private sealed record Recorder
    {
        public List<NotifyCollectionChangedAction> Actions { get; } = [];
        public List<string> PropertyNames { get; } = [];

        public void Attach(BulkObservableCollection<string> c)
        {
            c.CollectionChanged += (_, e) => Actions.Add(e.Action);
            ((INotifyPropertyChanged)c).PropertyChanged += (_, e) => PropertyNames.Add(e.PropertyName!);
        }
    }

    [Fact]
    public void BulkScope_NMutations_RaiseSingleReset()
    {
        var c = new BulkObservableCollection<string> { "a", "b" };
        var rec = new Recorder();
        rec.Attach(c);

        using (c.BeginBulkUpdate())
        {
            c.Clear();
            for (var i = 0; i < 100; i++) c.Add("item" + i);
            c[0] = "replaced";
        }

        // W+2 次修改只抛 1 次 Reset，不出现逐条 Add/Remove/Replace
        Assert.Equal([NotifyCollectionChangedAction.Reset], rec.Actions);
        // Count / Item[] 各补一次（与 ObservableCollection.ClearItems 的通知顺序一致）
        // Clear 移除 2 项 + 100 次 Add + 1 次替换 → 共 100 项
        Assert.Equal(100, c.Count);
        Assert.Equal("replaced", c[0]);
        Assert.Equal("item1", c[1]);
    }

    [Fact]
    public void BulkScope_NoMutation_RaisesNothing()
    {
        var c = new BulkObservableCollection<string> { "a" };
        var rec = new Recorder();
        rec.Attach(c);

        using (c.BeginBulkUpdate())
        {
            // 只读不写
        }

        Assert.Empty(rec.Actions);
        Assert.Empty(rec.PropertyNames);
    }

    [Fact]
    public void BulkScope_Nested_OnlyOutermostExitsRaiseReset()
    {
        var c = new BulkObservableCollection<string>();
        var rec = new Recorder();
        rec.Attach(c);

        using (c.BeginBulkUpdate())
        {
            c.Add("a");
            using (c.BeginBulkUpdate())
            {
                c.Add("b");
            }
            // 内层退出不应抛事件
            Assert.Empty(rec.Actions);
            c.Add("c");
        }

        Assert.Equal([NotifyCollectionChangedAction.Reset], rec.Actions);
        Assert.Equal(3, c.Count);
    }

    [Fact]
    public void BulkScope_MutationOutsideScope_RaisesPerItemEventsAsUsual()
    {
        var c = new BulkObservableCollection<string>();
        var rec = new Recorder();
        rec.Attach(c);

        c.Add("a");       // Add
        c[0] = "b";       // Replace
        c.RemoveAt(0);    // Remove

        Assert.Equal(
        [
            NotifyCollectionChangedAction.Add,
            NotifyCollectionChangedAction.Replace,
            NotifyCollectionChangedAction.Remove,
        ], rec.Actions);
    }

    [Fact]
    public void BulkScope_ExceptionInsideScope_StillRaisesResetOnDispose()
    {
        var c = new BulkObservableCollection<string>();
        var rec = new Recorder();
        rec.Attach(c);

        try
        {
            using (c.BeginBulkUpdate())
            {
                c.Add("a");
                throw new InvalidOperationException("boom");
            }
        }
        catch (InvalidOperationException) { }

        // using 的 finally 保证 Dispose 执行：抑制状态不泄漏，后续修改恢复逐条通知
        Assert.Equal([NotifyCollectionChangedAction.Reset], rec.Actions);
        c.Add("b");
        Assert.Equal(
        [
            NotifyCollectionChangedAction.Reset,
            NotifyCollectionChangedAction.Add,
        ], rec.Actions);
    }

    [Fact]
    public void BulkScope_ProducesSameContent_AsSequentialMutations()
    {
        var items = new List<string> { "x", "y", "z" };

        var bulk = new BulkObservableCollection<string> { "old1", "old2" };
        using (bulk.BeginBulkUpdate())
        {
            bulk.Clear();
            foreach (var i in items) bulk.Add(i);
        }

        var seq = new ObservableCollection<string> { "old1", "old2" };
        seq.Clear();
        foreach (var i in items) seq.Add(i);

        Assert.Equal(seq, bulk);
    }
}
