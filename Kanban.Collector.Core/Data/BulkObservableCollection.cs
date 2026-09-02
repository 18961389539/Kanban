using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Kanban.Collector.Core.Data;

/// <summary>
/// 支持批量更新的 <see cref="ObservableCollection{T}"/>：批量作用域内抑制逐条
/// CollectionChanged / PropertyChanged 通知，最外层作用域退出时统一抛一次 Reset。
/// </summary>
/// <remarks>
/// <para>解决的问题：LoadAll / 批量导入 / 远程全量同步会连续产生 N 次集合事件，
/// 每个订阅者（WPF 的 CollectionView、ViewModel 的派生计数重算）都要完整处理一次，
/// 而单次处理的代价是 O(N)，整体退化成 O(N²)。批量作用域把 N 次事件塌缩成 1 次 Reset。</para>
///
/// <para>语义约定：作用域内只抑制通知，集合本身的读写行为不变（顺序、去重、索引均保持不变），
/// 因此对订阅方而言等价于「一次性把集合换成新内容」。</para>
///
/// <para>锁约定：本类不自行加锁。调用方须与逐条修改保持一致，在持有集合同步锁
/// （SyncRoot / _collectionLock）的前提下使用，否则 WPF 绑定引擎（已通过
/// EnableCollectionSynchronization 注册同步）与后台线程之间仍会竞争。
/// 审查修复 2026-09-02（P1-4）：<see cref="WorkOrderRepository.BeginBulkUpdate"/> 的
/// 包装层会自动持有 SyncRoot，调用方（WorkOrderService.ImportWorkOrders、
/// RemoteRuntimeSink 批量应用）不再需要手动加锁；仓储内部路径（LoadAll/ApplySnapshot）
/// 外层已持锁，经 Monitor 重入不受影响。</para>
///
/// <para>嵌套安全：支持嵌套作用域，仅最外层退出时抛 Reset；作用域内若无任何实际变更则不抛 Reset。</para>
/// </remarks>
public class BulkObservableCollection<T> : ObservableCollection<T>
{
    private int _bulkDepth;
    /// <summary>
    /// 批量作用域内是否发生过修改。必须 volatile：InsertItem 等写侧可能在后台线程执行，
    /// ExitBulk 在作用域退出线程读取——普通字段无内存屏障，JIT/CPU 可重排导致漏抛 Reset
    /// （UI 静默不刷新，审查修复 2026-09-02 P1-4）。
    /// </summary>
    private volatile bool _mutated;

    /// <summary>进入批量更新作用域。建议配合 <c>using</c> 使用：最外层退出时统一抛一次 Reset。</summary>
    public IDisposable BeginBulkUpdate()
    {
        // 仅进入最外层作用域时清脏标记：作用域外的历史修改（如构造/初始化时的 Add）
        // 不应泄漏成一次多余的 Reset。嵌套作用域不清，避免丢掉外层已发生的变更。
        if (Interlocked.Increment(ref _bulkDepth) == 1)
            _mutated = false;
        return new BulkScope(this);
    }

    private bool InBulk => Volatile.Read(ref _bulkDepth) > 0;

    /// <inheritdoc />
    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (InBulk) return;
        base.OnCollectionChanged(e);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (InBulk) return;
        base.OnPropertyChanged(e);
    }

    /// <inheritdoc />
    protected override void InsertItem(int index, T item)
    {
        _mutated = true;
        base.InsertItem(index, item);
    }

    /// <inheritdoc />
    protected override void RemoveItem(int index)
    {
        _mutated = true;
        base.RemoveItem(index);
    }

    /// <inheritdoc />
    protected override void SetItem(int index, T item)
    {
        _mutated = true;
        base.SetItem(index, item);
    }

    /// <inheritdoc />
    protected override void MoveItem(int oldIndex, int newIndex)
    {
        _mutated = true;
        base.MoveItem(oldIndex, newIndex);
    }

    /// <inheritdoc />
    protected override void ClearItems()
    {
        _mutated = true;
        base.ClearItems();
    }

    /// <summary>最外层作用域退出：仅在确有变更时补抛一次 Reset（含 Count / Item[] 属性通知）。</summary>
    private void ExitBulk()
    {
        if (Interlocked.Decrement(ref _bulkDepth) > 0) return;
        if (!_mutated) return;
        _mutated = false;

        // 通知顺序与 ObservableCollection.ClearItems 保持一致：Count → Item[] → Reset
        base.OnPropertyChanged(new PropertyChangedEventArgs("Count"));
        base.OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        base.OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    private sealed class BulkScope(BulkObservableCollection<T> owner) : IDisposable
    {
        private BulkObservableCollection<T>? _owner = owner;

        public void Dispose()
            => Interlocked.Exchange(ref _owner, null)?.ExitBulk();
    }
}
