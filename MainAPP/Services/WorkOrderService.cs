using System.Windows;
using MainAPP.Resources;
using Kanban.Collector.Core.Services;
using Kanban.Contracts.Dtos;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using MainAPP.Models;
using Microsoft.Extensions.Logging;

namespace MainAPP.Services;

/// <summary>
/// 工单产量聚合结果：用于列表项进度条与详情页产量统计。
/// OkCount/NgCount 为工单时间窗口内的实际产量（按班次差分累加）。
/// </summary>
public sealed class WorkOrderProductionSummary
{
    /// <summary>合格产量（班次内累计差分之和）。</summary>
    public int OkCount { get; init; }

    /// <summary>不良产量。</summary>
    public int NgCount { get; init; }

    /// <summary>总产量 = OkCount + NgCount。</summary>
    public int TotalCount => OkCount + NgCount;

    /// <summary>达成率 = OkCount / TargetQuantity（TargetQuantity<=0 时返回 0）。</summary>
    public double AchievementRate { get; init; }

    /// <summary>不良率 = NgCount / TotalCount（TotalCount<=0 时返回 0）。</summary>
    public double DefectRate { get; init; }
}

/// <summary>
/// 工单业务服务：封装工单的新增/编辑/删除/状态切换（Start/Complete/Abort）等业务规则，
/// 供 <see cref="WorkOrderManagerViewModel"/> 与 <see cref="DeviceManagerViewModel"/> 共享，
/// 消除两份几乎相同的实现。
///
/// 所有方法均在 UI 线程调用；内部已完成状态机校验、同设备 Running 检查、二次确认对话框与
/// 成功/失败通知。返回值约定：成功返回保存后的 WorkOrder（或 bool），用户取消或校验失败
/// 返回 null/false（此时已向用户提示原因，调用方无需再弹通知）。
/// </summary>
public interface IWorkOrderService
{
    /// <summary>获取可选设备列表（供工单编辑对话框的设备下拉）。</summary>
    IReadOnlyList<(string Id, string Name)> GetAvailableDevices();

    /// <summary>浅拷贝工单（作为编辑模板，避免直接修改原对象）。</summary>
    WorkOrder Clone(WorkOrder source);

    /// <summary>
    /// 新增工单。
    /// <param name="template">编辑模板，可预填 DeviceId/DeviceName 等（DeviceManagerVM 场景）；传 null 表示空白新增。</param>
    /// <returns>已保存的工单；用户取消时返回 null。</returns>
    WorkOrder? AddWorkOrder(WorkOrder? template = null);

    /// <summary>异步新增工单（Remote 模式用，避免 UI 线程阻塞等待 SignalR 落库）。</summary>
    Task<WorkOrder?> AddWorkOrderAsync(WorkOrder? template = null);

    /// <summary>复制指定工单并以新工单方式编辑保存。</summary>
    WorkOrder? CopyWorkOrder(WorkOrder source);

    /// <summary>异步复制工单（Remote 模式用）。</summary>
    Task<WorkOrder?> CopyWorkOrderAsync(WorkOrder source);

    /// <summary>
    /// 编辑指定工单（拷贝后打开对话框，避免直接修改原对象）。
    /// <returns>已保存的工单；用户取消时返回 null。</returns>
    WorkOrder? EditWorkOrder(WorkOrder source);

    /// <summary>异步编辑工单（Remote 模式用）。</summary>
    Task<WorkOrder?> EditWorkOrderAsync(WorkOrder source);

    /// <summary>
    /// 删除指定工单（带二次确认对话框）。
    /// <returns>true=已删除；false=用户取消。</returns>
    bool DeleteWorkOrder(WorkOrder target);

    /// <summary>异步删除工单（Remote 模式用）。</summary>
    Task<bool> DeleteWorkOrderAsync(WorkOrder target);

    /// <summary>
    /// 启动工单：校验 Status==Pending + 同设备无 Running 工单，通过后置 Running。
    /// <returns>已保存的工单；校验失败或同设备冲突时返回 null（已提示原因）。</returns>
    WorkOrder? StartWorkOrder(WorkOrder target);

    /// <summary>异步启动工单（Remote 模式用）。</summary>
    Task<WorkOrder?> StartWorkOrderAsync(WorkOrder target);

    /// <summary>
    /// 完成工单：校验 Status==Running，通过后置 Completed。
    /// <returns>已保存的工单；校验失败时返回 null（已提示原因）。</returns>
    WorkOrder? CompleteWorkOrder(WorkOrder target);

    /// <summary>异步完成工单（Remote 模式用）。</summary>
    Task<WorkOrder?> CompleteWorkOrderAsync(WorkOrder target);

    /// <summary>
    /// 中止工单：校验 Status∈{Running, Pending}，通过后弹二次确认，确认后置 Aborted。
    /// <returns>已保存的工单；校验失败或用户取消时返回 null。</returns>
    WorkOrder? AbortWorkOrder(WorkOrder target);

    /// <summary>异步中止工单（Remote 模式用）。</summary>
    Task<WorkOrder?> AbortWorkOrderAsync(WorkOrder target);

    /// <summary>
    /// 查询指定工单的产量聚合（合格/不良/达成率）。
    /// 优先按 WorkOrderId 查询关联的生产快照；若无关联（老数据），回退按 DeviceId + 工单时间窗口查询。
    /// OkProduction/NgProduction 是班次内累计值，需按班次分组差分后累加。
    /// </summary>
    WorkOrderProductionSummary GetProductionSummary(WorkOrder workOrder);

    /// <summary>批量查询工单产量，生产环境按一次工单 Id 查询减少数据库往返。</summary>
    IReadOnlyDictionary<int, WorkOrderProductionSummary> GetProductionSummaries(IReadOnlyList<WorkOrder> workOrders);

    /// <summary>
    /// 批量导入工单（CSV 解析后的候选列表）。逐条执行与单条新增一致的业务校验
    /// （OrderNo 唯一、设备存在、计划时间有效、同设备时间不冲突），校验失败的行跳过并记录错误，
    /// 不弹对话框（批量场景不适合逐个打断）。返回成功/失败汇总。
    /// </summary>
    /// <param name="candidates">候选工单（Id 应为 0，导入一律作为新工单；状态忽略统一为 Pending）。</param>
    /// <returns>导入结果：Imported 已落库清单、Errors 每行失败原因。</returns>
    Task<WorkOrderImportResult> ImportWorkOrdersAsync(IReadOnlyList<WorkOrder> candidates);
}

/// <summary>工单产量聚合的可选异步能力，供 Remote 主页取消在途 SignalR 查询。</summary>
public interface IAsyncWorkOrderProductionSummary
{
    Task<WorkOrderProductionSummary> GetProductionSummaryAsync(
        WorkOrder workOrder,
        CancellationToken cancellationToken = default);
}

/// <summary>批量导入工单结果：Imported 为已落库清单，Errors 为逐行失败原因（含行号/工单号便于定位）。</summary>
public sealed class WorkOrderImportResult
{
    /// <summary>已成功导入的工单（按输入顺序，仅校验通过部分）。</summary>
    public List<WorkOrder> Imported { get; } = [];

    /// <summary>失败原因列表（"第 N 行 [工单号]：原因"）。</summary>
    public List<string> Errors { get; } = [];

    public int FailedCount => Errors.Count;
    public bool HasErrors => Errors.Count > 0;
}

/// <summary>历史服务的可选批量能力，旧测试桩未实现时由工单服务回退逐条查询。</summary>
// 接口定义已随采集/存储核心迁移至 Kanban.Collector.Core（MainAPP.Services.IWorkOrderProductionBatchQuery），
// 此处经项目引用可见，不再重复定义。

/// <summary>
/// <see cref="IWorkOrderService"/> 的默认实现。
/// 依赖 <see cref="WorkOrderRepository"/>（落库）、<see cref="DeviceRepository"/>（设备列表）、
/// <see cref="IDialogService"/>（弹窗与通知）、<see cref="IHistoryService"/>（产量聚合查询）。
/// </summary>
public class WorkOrderService(
    WorkOrderRepository workOrderRepo,
    DeviceRepository deviceRepo,
    IDialogService dialog,
    IProductionHistoryReader historyService,
    IRuntimeMode? runtimeMode = null,
    ILogger<WorkOrderService>? logger = null) : IWorkOrderService, IAsyncWorkOrderProductionSummary
{
    private readonly WorkOrderRepository _workOrderRepo = workOrderRepo;
    private readonly DeviceRepository _deviceRepo = deviceRepo;
    private readonly IDialogService _dialog = dialog;
    private readonly IProductionHistoryReader _historyService = historyService;
    private readonly IRuntimeMode? _runtimeMode = runtimeMode;
    private readonly ILogger<WorkOrderService> _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkOrderService>.Instance;

    /// <summary>
    /// Remote 模式禁用同步包装器：IRemoteWorkOrderStore 走 SignalR 真正异步，UI 线程同步等待
    /// （GetAwaiter().GetResult()）会经典死锁。生产调用方一律使用 Async 变体；
    /// 本守卫仅拦截测试/误用路径（测试不传 runtimeMode 时为 null，不拦截）。
    /// </summary>
    private void EnsureSyncApiAllowed()
    {
        if (_runtimeMode?.IsRemote == true)
            throw new InvalidOperationException(
                "Sync API is not available in Remote mode (may deadlock); use the Async variant.");
    }

    public IReadOnlyDictionary<int, WorkOrderProductionSummary> GetProductionSummaries(IReadOnlyList<WorkOrder> workOrders)
    {
        var result = new Dictionary<int, WorkOrderProductionSummary>();
        var pending = workOrders.Where(w => !HasCompletedSnapshot(w)).ToList();
        Dictionary<int, List<ProductionLog>>? batch = null;
        if (_historyService is IWorkOrderProductionBatchQuery batchQuery && pending.Count > 0)
            batch = batchQuery.QueryProductionLogsByWorkOrderBatch(pending.Select(w => w.Id).Where(id => id > 0).ToList());

        // 回退窗口批量（审查修复 2026-08-13）：未按 WorkOrderId 命中的工单（老数据无关联 / 未落库新工单）
        // 走 DeviceId+时间窗口回退——Remote 模式此前逐条 GetProductionSummary 产生 N+1 次
        // UI 线程同步 SignalR 往返（每次最长 10s），现合并为按窗口的批量请求（每批 ≤32 个子查询）
        var fallbackWindows = new List<(int WorkOrderId, string DeviceId, DateTime From, DateTime To)>();
        foreach (var w in pending)
        {
            if (batch != null && batch.TryGetValue(w.Id, out var linkedLogs) && linkedLogs.Count > 0) continue;
            fallbackWindows.Add((w.Id, w.DeviceId, FallbackFrom(w), FallbackTo(w)));
        }
        Dictionary<int, List<ProductionLog>>? windowBatch = null;
        if (_historyService is IWorkOrderProductionBatchQuery windowQuery && fallbackWindows.Count > 0)
            windowBatch = windowQuery.QueryProductionLogsByDeviceWindowsBatch(fallbackWindows);

        foreach (var workOrder in workOrders)
        {
            if (HasCompletedSnapshot(workOrder))
            {
                result[workOrder.Id] = GetProductionSummary(workOrder);
                continue;
            }

            if (batch != null && batch.TryGetValue(workOrder.Id, out var logs) && logs.Count > 0)
            {
                result[workOrder.Id] = ToSummary(
                    WorkOrderProductionSummaryCalculator.CalculateFromLogs(workOrder, logs, _historyService));
                continue;
            }
            if (windowBatch != null && windowBatch.TryGetValue(workOrder.Id, out var wLogs))
            {
                result[workOrder.Id] = wLogs.Count > 0
                    ? ToSummary(WorkOrderProductionSummaryCalculator.CalculateFromLogs(workOrder, wLogs, _historyService))
                    : new WorkOrderProductionSummary();
                continue;
            }
            // 兜底：历史服务未实现批量能力（旧测试桩）时逐条查询（与原行为一致）
            result[workOrder.Id] = GetProductionSummary(workOrder);
        }
        return result;
    }

    private static bool HasCompletedSnapshot(WorkOrder workOrder)
        => workOrder.Status is WorkOrderStatus.Completed or WorkOrderStatus.Aborted
            && workOrder.CompletedOkCount.HasValue
            && workOrder.CompletedNgCount.HasValue;

    /// <summary>工单回退查询窗口起点（前后各扩 5 分钟，避免工单开始/结束边界处丢失快照）。</summary>
    private static DateTime FallbackFrom(WorkOrder workOrder) => workOrder.PlannedStart.AddMinutes(-5);

    /// <summary>工单回退查询窗口终点（未配置结束时间时取当前时刻）。</summary>
    private static DateTime FallbackTo(WorkOrder workOrder)
        => workOrder.PlannedEnd > workOrder.PlannedStart
            ? workOrder.PlannedEnd.AddMinutes(5)
            : DateTime.Now;

    /// <inheritdoc />
    public IReadOnlyList<(string Id, string Name)> GetAvailableDevices()
        => _deviceRepo.GetDevicesSnapshot().Select(d => (d.Id, d.Name)).ToList();

    /// <inheritdoc />
    public WorkOrder Clone(WorkOrder w) => new()
    {
        Id = w.Id,
        OrderNo = w.OrderNo,
        ProductCode = w.ProductCode,
        ProductName = w.ProductName,
        DeviceId = w.DeviceId,
        DeviceName = w.DeviceName,
        TargetQuantity = w.TargetQuantity,
        PlannedStart = w.PlannedStart,
        PlannedEnd = w.PlannedEnd,
        Status = w.Status,
        CompletedOkCount = w.CompletedOkCount,
        CompletedNgCount = w.CompletedNgCount,
        StartedAt = w.StartedAt,
        CompletedAt = w.CompletedAt,
        Remark = w.Remark,
        CreatedAt = w.CreatedAt,
        UpdatedAt = w.UpdatedAt,
    };

    /// <inheritdoc />
    public WorkOrder? AddWorkOrder(WorkOrder? template = null)
    {
        EnsureSyncApiAllowed();
        return AddWorkOrderCore(template).GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public async Task<WorkOrder?> AddWorkOrderAsync(WorkOrder? template = null)
        => await AddWorkOrderCore(template);

    private async Task<WorkOrder?> AddWorkOrderCore(WorkOrder? template)
    {
        var result = _dialog.ShowWorkOrderEditor(template, GetAvailableDevices());
        if (result == null) return null;
        if (!ValidateOrderIdentityAndSchedule(result)) return null;
        var saved = await _workOrderRepo.UpsertAsync(result);
        _dialog.NotifySuccess(string.Format(Strings.F109, saved.OrderNo));
        return saved;
    }

    /// <inheritdoc />
    public WorkOrder? CopyWorkOrder(WorkOrder source)
    {
        EnsureSyncApiAllowed();
        return CopyWorkOrderCore(source).GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public async Task<WorkOrder?> CopyWorkOrderAsync(WorkOrder source)
        => await CopyWorkOrderCore(source);

    private async Task<WorkOrder?> CopyWorkOrderCore(WorkOrder source)
    {
        var template = Clone(source);
        template.Id = 0;
        template.OrderNo = string.IsNullOrWhiteSpace(source.OrderNo) ? string.Empty : $"{source.OrderNo}-COPY";
        template.Status = WorkOrderStatus.Pending;
        template.CompletedOkCount = null;
        template.CompletedNgCount = null;
        template.Production = null;
        template.StartedAt = null;
        template.CompletedAt = null;
        template.CreatedAt = DateTime.Now;
        template.UpdatedAt = template.CreatedAt;

        var result = _dialog.ShowWorkOrderEditor(template, GetAvailableDevices());
        if (result == null) return null;
        if (!ValidateOrderIdentityAndSchedule(result)) return null;
        var saved = await _workOrderRepo.UpsertAsync(result);
        _dialog.NotifySuccess(string.Format(Strings.F099, saved.OrderNo));
        return saved;
    }

    /// <inheritdoc />
    public WorkOrder? EditWorkOrder(WorkOrder source)
    {
        EnsureSyncApiAllowed();
        return EditWorkOrderCore(source).GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public async Task<WorkOrder?> EditWorkOrderAsync(WorkOrder source)
        => await EditWorkOrderCore(source);

    private async Task<WorkOrder?> EditWorkOrderCore(WorkOrder source)
    {
        var template = Clone(source);
        var result = _dialog.ShowWorkOrderEditor(template, GetAvailableDevices());
        if (result == null) return null;
        if (!ValidateOrderIdentityAndSchedule(result)) return null;
        var saved = await _workOrderRepo.UpsertAsync(result);
        _dialog.NotifySuccess(string.Format(Strings.F110, saved.OrderNo));
        return saved;
    }

    /// <inheritdoc />
    public bool DeleteWorkOrder(WorkOrder target)
    {
        EnsureSyncApiAllowed();
        return DeleteWorkOrderCore(target).GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public async Task<bool> DeleteWorkOrderAsync(WorkOrder target)
        => await DeleteWorkOrderCore(target);

    private async Task<bool> DeleteWorkOrderCore(WorkOrder target)
    {
        var r = _dialog.Show(
            string.Format(Strings.F174, target.OrderNo, target.ProductName),
            Strings.M_ConfirmDelete, MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (r != MessageBoxResult.Yes) return false;
        await _workOrderRepo.DeleteAsync(target.Id);
        _dialog.NotifySuccess(Strings.M006);
        return true;
    }

    private bool ValidateOrderIdentityAndSchedule(WorkOrder candidate)
    {
        var duplicate = _workOrderRepo.GetSnapshot().FirstOrDefault(w =>
            w.Id != candidate.Id
            && string.Equals(w.OrderNo.Trim(), candidate.OrderNo.Trim(), StringComparison.OrdinalIgnoreCase));
        if (duplicate != null)
        {
            _dialog.NotifyWarning(string.Format(Strings.F097, candidate.OrderNo, duplicate.Id));
            return false;
        }

        if (candidate.Status is not (WorkOrderStatus.Pending or WorkOrderStatus.Running))
            return true;

        var conflict = _workOrderRepo.GetSnapshot()
            .Where(w => w.Id != candidate.Id
                && w.DeviceId == candidate.DeviceId
                && w.Status is WorkOrderStatus.Pending or WorkOrderStatus.Running)
            .FirstOrDefault(w => candidate.PlannedStart < w.PlannedEnd && w.PlannedStart < candidate.PlannedEnd);
        if (conflict != null)
        {
            _dialog.NotifyWarning(string.Format(Strings.F200, candidate.DeviceName, conflict.OrderNo, conflict.PlannedStart, conflict.PlannedEnd));
            return false;
        }
        return true;
    }

    /// <inheritdoc />
    public WorkOrder? StartWorkOrder(WorkOrder target)
    {
        EnsureSyncApiAllowed();
        return StartWorkOrderCore(target).GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public async Task<WorkOrder?> StartWorkOrderAsync(WorkOrder target)
        => await StartWorkOrderCore(target);

    private async Task<WorkOrder?> StartWorkOrderCore(WorkOrder target)
    {
        // 同设备 Running 检查保留在 Service 层（跨工单约束，实体无法自检）
        var running = _workOrderRepo.GetRunningByDevice(target.DeviceId);
        if (running != null && running.Id != target.Id)
        {
            _dialog.NotifyWarning(string.Format(Strings.F208, target.DeviceName, running.OrderNo));
            return null;
        }
        var updated = Clone(target);
        // 状态机校验下沉到实体方法，违反约束时实体抛 InvalidOperationException
        try
        {
            updated.Start();
        }
        catch (InvalidOperationException ex)
        {
            _dialog.NotifyWarning(ex.Message);
            return null;
        }
        var saved = await _workOrderRepo.UpsertAsync(updated);
        _dialog.NotifySuccess(string.Format(Strings.F096, saved.OrderNo));
        return saved;
    }

    /// <inheritdoc />
    public WorkOrder? CompleteWorkOrder(WorkOrder target)
    {
        EnsureSyncApiAllowed();
        return CompleteWorkOrderCore(target).GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public async Task<WorkOrder?> CompleteWorkOrderAsync(WorkOrder target)
        => await CompleteWorkOrderCore(target);

    private async Task<WorkOrder?> CompleteWorkOrderCore(WorkOrder target)
    {
        var updated = Clone(target);
        // 状态机校验下沉到实体方法，违反约束时实体抛 InvalidOperationException
        try
        {
            updated.Complete();
        }
        catch (InvalidOperationException ex)
        {
            _dialog.NotifyWarning(ex.Message);
            return null;
        }
        // 完成时写入产量快照：避免历史工单每次展示都重复扫描 ProductionLogs，
        // 同时保留数据即使日志被清理（ProductionLogs 与 WorkOrders 保留期不同）
        var summary = GetProductionSummary(updated);
        updated.CompletedOkCount = summary.OkCount;
        updated.CompletedNgCount = summary.NgCount;
        var saved = await _workOrderRepo.UpsertAsync(updated);
        _dialog.NotifySuccess(string.Format(Strings.F095, saved.OrderNo));
        return saved;
    }

    /// <inheritdoc />
    public WorkOrder? AbortWorkOrder(WorkOrder target)
    {
        EnsureSyncApiAllowed();
        return AbortWorkOrderCore(target).GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public async Task<WorkOrder?> AbortWorkOrderAsync(WorkOrder target)
        => await AbortWorkOrderCore(target);

    private async Task<WorkOrder?> AbortWorkOrderCore(WorkOrder target)
    {
        var updated = Clone(target);
        // 状态机校验下沉到实体方法：先在副本上验证状态约束，失败则直接提示，不弹确认框
        try
        {
            updated.Abort();
        }
        catch (InvalidOperationException ex)
        {
            _dialog.NotifyWarning(ex.Message);
            return null;
        }
        var r = _dialog.Show(
            string.Format(Strings.F173, target.OrderNo),
            Strings.M_ConfirmAbort, MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (r != MessageBoxResult.Yes) return null;
        // 中止也写入产量快照（Running 中止时已有部分产量，需保留）
        // 注意：此处用 target.Status 判定原状态，updated 已被 Abort() 改为 Aborted
        if (target.Status == WorkOrderStatus.Running)
        {
            var summary = GetProductionSummary(updated);
            updated.CompletedOkCount = summary.OkCount;
            updated.CompletedNgCount = summary.NgCount;
        }
        var saved = await _workOrderRepo.UpsertAsync(updated);
        _dialog.NotifySuccess(string.Format(Strings.F094, saved.OrderNo));
        return saved;
    }

    /// <inheritdoc />
    public WorkOrderProductionSummary GetProductionSummary(WorkOrder workOrder)
    {
        try
        {
            return ToSummary(WorkOrderProductionSummaryCalculator.Calculate(workOrder, _historyService));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "查询工单 {OrderNo} 产量聚合失败", workOrder.OrderNo);
            return new WorkOrderProductionSummary();
        }
    }

    /// <summary>异步聚合工单产量，Remote 历史请求可在工单切换或页面销毁时取消。</summary>
    public async Task<WorkOrderProductionSummary> GetProductionSummaryAsync(
        WorkOrder workOrder,
        CancellationToken cancellationToken = default)
    {
        if (HasCompletedSnapshot(workOrder))
            return ToSummary(WorkOrderProductionSummaryCalculator.Calculate(workOrder, _historyService));

        try
        {
            var asyncHistory = _historyService as IAsyncProductionHistoryReader;
            var logs = asyncHistory != null
                ? await asyncHistory.QueryProductionLogsByWorkOrderAsync(workOrder.Id, cancellationToken).ConfigureAwait(false)
                : await Task.Run(() => _historyService.QueryProductionLogsByWorkOrder(workOrder.Id), cancellationToken).ConfigureAwait(false);

            if (logs.Count == 0)
            {
                logs = asyncHistory != null
                    ? await asyncHistory.QueryProductionLogsAsync(
                        FallbackFrom(workOrder), FallbackTo(workOrder), workOrder.DeviceId,
                        cancellationToken: cancellationToken).ConfigureAwait(false)
                    : await Task.Run(
                        () => _historyService.QueryProductionLogs(
                            FallbackFrom(workOrder), FallbackTo(workOrder), workOrder.DeviceId),
                        cancellationToken).ConfigureAwait(false);
            }

            return logs.Count == 0
                ? new WorkOrderProductionSummary()
                : ToSummary(WorkOrderProductionSummaryCalculator.CalculateFromLogs(workOrder, logs, _historyService));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "异步查询工单产量聚合失败，返回空摘要 WorkOrder={WorkOrderId}", workOrder.Id);
            return new WorkOrderProductionSummary();
        }
    }

    private static WorkOrderProductionSummary ToSummary(WorkOrderProductionSummaryDto dto)
        => new()
        {
            OkCount = dto.OkCount,
            NgCount = dto.NgCount,
            AchievementRate = dto.AchievementRate,
            DefectRate = dto.DefectRate,
        };

    /// <inheritdoc />
    public async Task<WorkOrderImportResult> ImportWorkOrdersAsync(IReadOnlyList<WorkOrder> candidates)
    {
        var result = new WorkOrderImportResult();
        if (candidates == null || candidates.Count == 0)
            return result;

        // 设备名 → DeviceId 映射（导入 CSV 用设备名标识设备，与导出对齐）
        var devices = _deviceRepo.GetDevicesSnapshot();
        var deviceByName = devices
            .GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First());

        // 现有工单 OrderNo 集合（批量导入需跨候选查重）
        var existingOrderNos = new HashSet<string>(
            _workOrderRepo.GetSnapshot().Select(w => w.OrderNo.Trim()),
            StringComparer.OrdinalIgnoreCase);

        // 批量作用域：N 行导入只抛 1 次 Reset，而不是 N 次集合事件。
        // 原先每行 Upsert 都会触发一次 CollectionChanged → ViewModel 全量重算派生计数（O(W·K)）
        // + 重建甘特图，整体退化成 O(W²·K)，数百行导入会卡死 UI 数十秒。
        using (_workOrderRepo.BeginBulkUpdate())
        {
            for (var i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                var lineNo = i + 2; // 表头占第 1 行
                try
                {
                    var failure = ValidateImportCandidate(candidate, deviceByName, existingOrderNos);
                    if (failure != null)
                    {
                        result.Errors.Add(string.Format(Strings.K806, lineNo, candidate.OrderNo, failure));
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "导入工单第 {Line} 行校验异常 OrderNo={OrderNo}", lineNo, candidate.OrderNo);
                    result.Errors.Add(string.Format(Strings.K806, lineNo, candidate.OrderNo, ex.Message));
                    continue;
                }

                try
                {
                    var saved = await _workOrderRepo.UpsertAsync(candidate);
                    existingOrderNos.Add(saved.OrderNo.Trim());
                    result.Imported.Add(saved);
                    _logger.LogInformation("工单导入 Id={Id} OrderNo={OrderNo} Device={Device}", saved.Id, saved.OrderNo, saved.DeviceName);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "导入工单第 {Line} 行落库失败 OrderNo={OrderNo}", lineNo, candidate.OrderNo);
                    result.Errors.Add(string.Format(Strings.K806, lineNo, candidate.OrderNo, ex.Message));
                }
            }
        }
        return result;
    }

    /// <summary>单条导入候选校验：返回失败原因字符串，null 表示通过。批量场景不弹对话框。</summary>
    private static string? ValidateImportCandidate(
        WorkOrder candidate,
        IReadOnlyDictionary<string, Device> deviceByName,
        ISet<string> existingOrderNos)
    {
        if (string.IsNullOrWhiteSpace(candidate.OrderNo))
            return Strings.K599;
        if (string.IsNullOrWhiteSpace(candidate.ProductCode))
            return Strings.K600;
        if (string.IsNullOrWhiteSpace(candidate.ProductName))
            return Strings.K601;
        if (candidate.TargetQuantity <= 0)
            return Strings.K603;

        var orderNoKey = candidate.OrderNo.Trim();
        if (existingOrderNos.Contains(orderNoKey))
            return Strings.K794;

        // 设备名匹配（CSV 仅设备名；找不到或重名都拒绝，避免落库悬空 DeviceId）
        if (!deviceByName.TryGetValue(candidate.DeviceName ?? string.Empty, out var device))
            return string.Format(Strings.K795, candidate.DeviceName);
        candidate.DeviceId = device.Id;
        candidate.DeviceName = device.Name;

        if (candidate.PlannedStart == default || candidate.PlannedEnd == default
            || candidate.PlannedEnd <= candidate.PlannedStart)
            return Strings.K604;

        // 状态统一为 Pending（导入即待排产）；清空运行时产量与旧快照
        candidate.Status = WorkOrderStatus.Pending;
        candidate.Id = 0;
        candidate.CompletedOkCount = null;
        candidate.CompletedNgCount = null;
        candidate.StartedAt = null;
        candidate.CompletedAt = null;
        candidate.Production = null;
        candidate.CreatedAt = DateTime.Now;
        candidate.UpdatedAt = candidate.CreatedAt;
        return null;
    }
}
