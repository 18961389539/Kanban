using AutoMapper;
using System.Collections.ObjectModel;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Services;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace Kanban.Collector.Core.Data;

/// <summary>
/// 工单仓储抽象接口：供 ViewModel / Service 依赖，解耦具体实现。
/// </summary>
public interface IWorkOrderRepository
{
    BulkObservableCollection<WorkOrder> WorkOrders { get; }

    /// <summary>开启批量更新作用域：作用域内的集合修改不逐条抛事件，最外层退出时统一抛一次 Reset。
    /// 供 LoadAll / 批量导入 / 远程全量同步等「连续 N 次变更」场景使用，避免订阅方退化成 O(N²) 重算。
    /// 调用方须自行持有 SyncRoot 锁（与逐条修改的锁约定一致）。</summary>
    IDisposable BeginBulkUpdate();

    void LoadAll();
    List<WorkOrder> GetSnapshot();
    Task<WorkOrder> UpsertAsync(WorkOrder workOrder);
    Task DeleteAsync(int id);
    WorkOrder Upsert(WorkOrder workOrder);
    void Delete(int id);
    int CleanupOldWorkOrders(int retentionDays = 365);
    WorkOrder? GetRunningByDevice(string deviceId);
    WorkOrder? GetLatestPendingByDevice(string deviceId);
}

/// <summary>
/// 工单仓储：管理 <see cref="WorkOrder"/> 的内存集合与持久化。
/// 参照 <see cref="DeviceRepository"/> 模式：DI 单例 + <see cref="ObservableCollection{WorkOrder}"/> +
/// <see cref="SyncRoot"/> 锁支持后台线程读写（WPF 绑定同步由 MainAPP.WpfCollectionBindingRegistrar 注册）。
///
/// 持久化使用 EF Core + SQLite（work_orders.db），每次写操作短上下文模式（using ctx），
/// 避免长生命周期 DbContext 的变更追踪开销与并发问题。
/// 内存集合仅在 LoadAll/Reload 时全量刷新，CRUD 操作直接落库 + 同步内存。
///
/// 注意：不在此处暴露 ICollectionView（属 UI 层关注点），由 ViewModel 通过
/// CollectionViewSource.GetDefaultView(WorkOrders) 自行创建过滤视图。
/// </summary>
public class WorkOrderRepository : IWorkOrderRepository
{
    private readonly DatabaseProvider _dbProvider;
    private readonly IMapper _mapper;
    private readonly IRemoteWorkOrderStore? _remoteStore;
    private readonly object _collectionLock = new();

    /// <summary>集合同步锁（只读暴露）：供 WPF 绑定引擎注册跨线程同步（MainAPP 启动时调用
    /// BindingOperations.EnableCollectionSynchronization(WorkOrders, SyncRoot)）。</summary>
    public object SyncRoot => _collectionLock;

    /// <summary>工单内存集合（绑定到 UI）。所有读写经 _collectionLock 串行化。</summary>
    public BulkObservableCollection<WorkOrder> WorkOrders { get; } = new();

    /// <inheritdoc />
    public IDisposable BeginBulkUpdate() => WorkOrders.BeginBulkUpdate();

    /// <summary>工单变更版本号（LoadAll/Upsert/Delete 成功后递增）。供 MetaPublisher 脏标记判断
    /// 是否重组装快照——无变更时跳过全量拷贝，消除每 5s 的无谓分配与锁争用。</summary>
    private long _changeVersionBacking;

    public long ChangeVersion => Volatile.Read(ref _changeVersionBacking);

    public WorkOrderRepository(DatabaseProvider dbProvider, IMapper mapper)
        : this(dbProvider, mapper, null)
    {
    }

    public WorkOrderRepository(
        DatabaseProvider dbProvider,
        IMapper mapper,
        IRemoteWorkOrderStore? remoteStore = null)
    {
        _dbProvider = dbProvider;
        _mapper = mapper;
        _remoteStore = remoteStore;
        // 注：WPF 绑定同步锁不在此注册（UI 进程关注点，Core 不依赖 WPF），
        // 由 MainAPP.WpfCollectionBindingRegistrar 经 SyncRoot 注册。
    }

    /// <summary>
    /// 启动期加载全部工单到内存集合（按 CreatedAt 倒序）。
    /// 调用时机：App.OnStartup 在 EnsureCreatedAll 之后。
    /// </summary>
    public void LoadAll()
    {
        List<WorkOrder> snapshot;
        try
        {
            using var ctx = _dbProvider.CreateWorkOrderContext();
            snapshot = ctx.WorkOrders
                .AsNoTracking()
                .OrderByDescending(w => w.CreatedAt)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "work_orders.db 加载失败，回退空工单列表");
            snapshot = new();
        }

        lock (_collectionLock)
        {
            // 批量作用域：W 条工单只抛 1 次 Reset，而不是 W 次 Add + 1 次 Clear。
            // 否则每个订阅者（CollectionView 重建 + ViewModel 派生计数重算）都要跑 O(W) 一次，整体 O(W²)。
            using (WorkOrders.BeginBulkUpdate())
            {
                WorkOrders.Clear();
                foreach (var w in snapshot)
                {
                    WorkOrders.Add(w);
                }
            }
        }

        Interlocked.Increment(ref _changeVersionBacking);
        Log.Information("WorkOrderRepository.LoadAll 完成：加载 {Count} 条工单", snapshot.Count);
    }

    /// <summary>获取工单快照副本（后台线程枚举用，避免持有锁过久）。</summary>
    public List<WorkOrder> GetSnapshot()
    {
        lock (_collectionLock)
        {
            return WorkOrders.ToList();
        }
    }

    /// <summary>
    /// 异步新增或更新工单。Remote 模式委托 Collector 落库；Local 模式等价于 <see cref="Upsert"/>。
    /// </summary>
    public async Task<WorkOrder> UpsertAsync(WorkOrder workOrder)
    {
        // Remote 模式：Collector 是唯一写者，工单经 SignalR 落库，返回带 Id 的结果
        if (_remoteStore?.IsEnabled == true)
        {
            var saved = await _remoteStore.UpsertAsync(workOrder);
            SyncMemoryCollection(saved);
            return saved;
        }

        return Upsert(workOrder);
    }

    /// <summary>
    /// 异步删除工单。Remote 模式委托 Collector 落库；Local 模式等价于 <see cref="Delete"/>。
    /// </summary>
    public async Task DeleteAsync(int id)
    {
        // Remote 模式：Collector 是唯一写者，工单经 SignalR 落库删除
        if (_remoteStore?.IsEnabled == true)
        {
            if (await _remoteStore.DeleteAsync(id))
            {
                RemoveFromMemory(id);
            }
            return;
        }

        Delete(id);
    }

    /// <summary>
    /// 同步新增或更新工单（Local 模式专用；Remote 模式请使用 <see cref="UpsertAsync"/>）。
    /// 写库成功后同步内存集合（已存在则替换，不存在则追加到首位）。
    /// 返回落库后的实体（含自增 Id）。
    /// </summary>
    public WorkOrder Upsert(WorkOrder workOrder)
    {
        EnsureLocalPersistence();
        workOrder.UpdatedAt = DateTime.Now;
        var isNew = workOrder.Id == 0;
        var oldStatus = default(WorkOrderStatus?);
        using var ctx = _dbProvider.CreateWorkOrderContext();
        if (workOrder.Id == 0)
        {
            if (workOrder.CreatedAt == default)
            {
                workOrder.CreatedAt = DateTime.Now;
            }
            ctx.WorkOrders.Add(workOrder);
        }
        else
            {
                var existing = ctx.WorkOrders.Find(workOrder.Id);
                if (existing == null)
                {
                    // 数据库无此 Id（可能已被外部删除），改为插入
                    ctx.WorkOrders.Add(workOrder);
                    isNew = true;
                }
                else
                {
                    oldStatus = existing.Status;
                    // AutoMapper 批量拷贝所有匹配属性（忽略 Id/CreatedAt），避免手动逐字段赋值漏写新字段
                    _mapper.Map(workOrder, existing);
                }
            }
        ctx.SaveChanges();

        SyncMemoryCollection(workOrder);
        BumpChangeVersion();

        // 业务事件 INF 日志：新增 / 状态变化 / 字段更新
        if (isNew)
        {
            Log.Information("工单新增 Id={Id} OrderNo={OrderNo} Device={Device} TargetQty={Qty} Status={Status}",
                workOrder.Id, workOrder.OrderNo, workOrder.DeviceName, workOrder.TargetQuantity, workOrder.Status);
        }
        else if (oldStatus.HasValue && oldStatus.Value != workOrder.Status)
        {
            Log.Information("工单状态变更 Id={Id} OrderNo={OrderNo} {Old} -> {New}",
                workOrder.Id, workOrder.OrderNo, oldStatus.Value, workOrder.Status);
        }
        else
        {
            Log.Information("工单字段更新 Id={Id} OrderNo={OrderNo} Status={Status}",
                workOrder.Id, workOrder.OrderNo, workOrder.Status);
        }

        return workOrder;
    }

    private void BumpChangeVersion() => Interlocked.Increment(ref _changeVersionBacking);

    /// <summary>同步内存集合：已存在则替换（整项替换触发 UI 通知），不存在则插入到首位（按 CreatedAt 倒序约定）。</summary>
    private void SyncMemoryCollection(WorkOrder workOrder)
    {
        lock (_collectionLock)
        {
            var idx = -1;
            for (var i = 0; i < WorkOrders.Count; i++)
            {
                if (WorkOrders[i].Id == workOrder.Id)
                {
                    idx = i;
                    break;
                }
            }
            if (idx >= 0)
            {
                // 替换而非就地修改属性：ObservableCollection 不会对元素属性变更触发通知，
                // 替换整项可让绑定 UI 重新读取
                WorkOrders[idx] = workOrder;
            }
            else
            {
                // 新增：插入到首位（按 CreatedAt 倒序约定）
                WorkOrders.Insert(0, workOrder);
            }
        }
    }

    /// <summary>
    /// 同步删除指定工单（Local 模式专用；Remote 模式请使用 <see cref="DeleteAsync"/>）。
    /// 同时从数据库与内存集合移除。
    /// </summary>
    public void Delete(int id)
    {
        EnsureLocalPersistence();
        WorkOrder? removed = null;
        using var ctx = _dbProvider.CreateWorkOrderContext();
        var existing = ctx.WorkOrders.Find(id);
        if (existing == null)
        {
            return;
        }
        removed = existing;
        ctx.WorkOrders.Remove(existing);
        ctx.SaveChanges();

        RemoveFromMemory(id);
        BumpChangeVersion();

        Log.Information("工单删除 Id={Id} OrderNo={OrderNo} Device={Device} Status={Status}",
            removed.Id, removed.OrderNo, removed.DeviceName, removed.Status);
    }

    private void EnsureLocalPersistence()
    {
        if (_remoteStore?.IsEnabled == true)
            throw new InvalidOperationException("Remote 模式不支持同步写入工单，请使用异步 API。");
    }

    /// <summary>从内存集合移除指定 Id 工单。</summary>
    private void RemoveFromMemory(int id)
    {
        lock (_collectionLock)
        {
            for (var i = 0; i < WorkOrders.Count; i++)
            {
                if (WorkOrders[i].Id == id)
                {
                    WorkOrders.RemoveAt(i);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// 清理超过指定时间的已完成/已中止工单。
    /// 默认保留一年（365天），启动时调用一次。
    /// 仅清理 Completed/Aborted 状态的工单，Pending/Running 状态的工单不受影响（避免误删进行中业务数据）。
    /// 同时同步内存集合，保持内存与数据库一致。
    /// 返回删除的记录数。
    /// </summary>
    public int CleanupOldWorkOrders(int retentionDays = 365)
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-retentionDays);
            using var ctx = _dbProvider.CreateWorkOrderContext();
            // 仅清理已结束状态（Completed/Aborted）且 UpdatedAt 早于 cutoff 的工单
            var old = ctx.WorkOrders
                .Where(w => (w.Status == WorkOrderStatus.Completed || w.Status == WorkOrderStatus.Aborted)
                            && w.UpdatedAt < cutoff)
                .ToList();
            if (old.Count == 0)
            {
                return 0;
            }

            var removedIds = old.Select(w => w.Id).ToHashSet();
            ctx.WorkOrders.RemoveRange(old);
            ctx.SaveChanges();

            // 同步内存集合（批量作用域：N 次 RemoveAt 只抛 1 次 Reset）
            lock (_collectionLock)
            {
                using (WorkOrders.BeginBulkUpdate())
                {
                    for (var i = WorkOrders.Count - 1; i >= 0; i--)
                    {
                        if (removedIds.Contains(WorkOrders[i].Id))
                        {
                            WorkOrders.RemoveAt(i);
                        }
                    }
                }
            }

            Log.Information("已清理 {Count} 条过期工单（已结束且早于 {Cutoff:yyyy-MM-dd}）", old.Count, cutoff);
            return old.Count;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "清理过期工单失败");
            return 0;
        }
    }

    /// <summary>
    /// 获取指定设备的当前 Running 工单。同设备最多 1 个 Running（由调用方保证）。
    /// 主页工单条与设备详情页用此查询当前工单。
    /// </summary>
    public WorkOrder? GetRunningByDevice(string deviceId)
    {
        lock (_collectionLock)
        {
            return WorkOrders.FirstOrDefault(w => w.DeviceId == deviceId && w.Status == WorkOrderStatus.Running);
        }
    }

    /// <summary>
    /// 获取指定设备的最新 Pending 工单（按 PlannedStart 升序）。
    /// 主页工单条无 Running 时回退显示最近待开始工单。
    /// </summary>
    public WorkOrder? GetLatestPendingByDevice(string deviceId)
    {
        lock (_collectionLock)
        {
            return WorkOrders
                .Where(w => w.DeviceId == deviceId && w.Status == WorkOrderStatus.Pending)
                .OrderBy(w => w.PlannedStart)
                .FirstOrDefault();
        }
    }
}
