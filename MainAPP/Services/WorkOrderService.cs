using System.Windows;
using MainAPP.Data;
using MainAPP.Entities;
using MainAPP.Models;

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

    /// <summary>复制指定工单并以新工单方式编辑保存。</summary>
    WorkOrder? CopyWorkOrder(WorkOrder source);

    /// <summary>
    /// 编辑指定工单（拷贝后打开对话框，避免直接修改原对象）。
    /// <returns>已保存的工单；用户取消时返回 null。</returns>
    WorkOrder? EditWorkOrder(WorkOrder source);

    /// <summary>
    /// 删除指定工单（带二次确认对话框）。
    /// <returns>true=已删除；false=用户取消。</returns>
    bool DeleteWorkOrder(WorkOrder target);

    /// <summary>
    /// 启动工单：校验 Status==Pending + 同设备无 Running 工单，通过后置 Running。
    /// <returns>已保存的工单；校验失败或同设备冲突时返回 null（已提示原因）。</returns>
    WorkOrder? StartWorkOrder(WorkOrder target);

    /// <summary>
    /// 完成工单：校验 Status==Running，通过后置 Completed。
    /// <returns>已保存的工单；校验失败时返回 null（已提示原因）。</returns>
    WorkOrder? CompleteWorkOrder(WorkOrder target);

    /// <summary>
    /// 中止工单：校验 Status∈{Running, Pending}，通过后弹二次确认，确认后置 Aborted。
    /// <returns>已保存的工单；校验失败或用户取消时返回 null。</returns>
    WorkOrder? AbortWorkOrder(WorkOrder target);

    /// <summary>
    /// 查询指定工单的产量聚合（合格/不良/达成率）。
    /// 优先按 WorkOrderId 查询关联的生产快照；若无关联（老数据），回退按 DeviceId + 工单时间窗口查询。
    /// OkProduction/NgProduction 是班次内累计值，需按班次分组差分后累加。
    /// </summary>
    WorkOrderProductionSummary GetProductionSummary(WorkOrder workOrder);

    /// <summary>批量查询工单产量，生产环境按一次工单 Id 查询减少数据库往返。</summary>
    IReadOnlyDictionary<int, WorkOrderProductionSummary> GetProductionSummaries(IReadOnlyList<WorkOrder> workOrders);
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
    IProductionHistoryReader historyService) : IWorkOrderService
{
    private readonly WorkOrderRepository _workOrderRepo = workOrderRepo;
    private readonly DeviceRepository _deviceRepo = deviceRepo;
    private readonly IDialogService _dialog = dialog;
    private readonly IProductionHistoryReader _historyService = historyService;

    public IReadOnlyDictionary<int, WorkOrderProductionSummary> GetProductionSummaries(IReadOnlyList<WorkOrder> workOrders)
    {
        var result = new Dictionary<int, WorkOrderProductionSummary>();
        var pending = workOrders.Where(w => !HasCompletedSnapshot(w)).ToList();
        Dictionary<int, List<ProductionLog>>? batch = null;
        if (_historyService is IWorkOrderProductionBatchQuery batchQuery && pending.Count > 0)
            batch = batchQuery.QueryProductionLogsByWorkOrderBatch(pending.Select(w => w.Id).Where(id => id > 0).ToList());

        foreach (var workOrder in workOrders)
        {
            if (HasCompletedSnapshot(workOrder))
            {
                result[workOrder.Id] = GetProductionSummary(workOrder);
                continue;
            }

            if (batch != null && batch.TryGetValue(workOrder.Id, out var logs) && logs.Count > 0)
                result[workOrder.Id] = CalculateProductionSummary(workOrder, logs);
            else
                result[workOrder.Id] = GetProductionSummary(workOrder);
        }
        return result;
    }

    private static bool HasCompletedSnapshot(WorkOrder workOrder)
        => workOrder.Status is WorkOrderStatus.Completed or WorkOrderStatus.Aborted
            && workOrder.CompletedOkCount.HasValue
            && workOrder.CompletedNgCount.HasValue;

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
        Remark = w.Remark,
        CreatedAt = w.CreatedAt,
        UpdatedAt = w.UpdatedAt,
    };

    /// <inheritdoc />
    public WorkOrder? AddWorkOrder(WorkOrder? template = null)
    {
        var result = _dialog.ShowWorkOrderEditor(template, GetAvailableDevices());
        if (result == null) return null;
        if (!ValidateOrderIdentityAndSchedule(result)) return null;
        var saved = _workOrderRepo.Upsert(result);
        _dialog.NotifySuccess($"已新增工单 {saved.OrderNo}");
        return saved;
    }

    /// <inheritdoc />
    public WorkOrder? CopyWorkOrder(WorkOrder source)
    {
        var template = Clone(source);
        template.Id = 0;
        template.OrderNo = string.IsNullOrWhiteSpace(source.OrderNo) ? string.Empty : $"{source.OrderNo}-COPY";
        template.Status = WorkOrderStatus.Pending;
        template.CompletedOkCount = null;
        template.CompletedNgCount = null;
        template.Production = null;
        template.CreatedAt = DateTime.Now;
        template.UpdatedAt = template.CreatedAt;

        var result = _dialog.ShowWorkOrderEditor(template, GetAvailableDevices());
        if (result == null) return null;
        if (!ValidateOrderIdentityAndSchedule(result)) return null;
        var saved = _workOrderRepo.Upsert(result);
        _dialog.NotifySuccess($"已复制工单 {saved.OrderNo}");
        return saved;
    }

    /// <inheritdoc />
    public WorkOrder? EditWorkOrder(WorkOrder source)
    {
        var template = Clone(source);
        var result = _dialog.ShowWorkOrderEditor(template, GetAvailableDevices());
        if (result == null) return null;
        if (!ValidateOrderIdentityAndSchedule(result)) return null;
        var saved = _workOrderRepo.Upsert(result);
        _dialog.NotifySuccess($"已更新工单 {saved.OrderNo}");
        return saved;
    }

    /// <inheritdoc />
    public bool DeleteWorkOrder(WorkOrder target)
    {
        var r = _dialog.Show(
            $"确定删除工单「{target.OrderNo}」（{target.ProductName}）吗？此操作不可恢复。",
            "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (r != MessageBoxResult.Yes) return false;
        _workOrderRepo.Delete(target.Id);
        _dialog.NotifySuccess("已删除工单");
        return true;
    }

    private bool ValidateOrderIdentityAndSchedule(WorkOrder candidate)
    {
        var duplicate = _workOrderRepo.GetSnapshot().FirstOrDefault(w =>
            w.Id != candidate.Id
            && string.Equals(w.OrderNo.Trim(), candidate.OrderNo.Trim(), StringComparison.OrdinalIgnoreCase));
        if (duplicate != null)
        {
            _dialog.NotifyWarning($"工单号「{candidate.OrderNo}」已存在（工单 Id={duplicate.Id}），请使用唯一工单号");
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
            _dialog.NotifyWarning($"设备「{candidate.DeviceName}」的计划时间与工单 {conflict.OrderNo} 重叠（{conflict.PlannedStart:MM-dd HH:mm} ~ {conflict.PlannedEnd:MM-dd HH:mm}）");
            return false;
        }
        return true;
    }

    /// <inheritdoc />
    public WorkOrder? StartWorkOrder(WorkOrder target)
    {
        // 同设备 Running 检查保留在 Service 层（跨工单约束，实体无法自检）
        var running = _workOrderRepo.GetRunningByDevice(target.DeviceId);
        if (running != null && running.Id != target.Id)
        {
            _dialog.NotifyWarning($"设备「{target.DeviceName}」已有进行中工单 {running.OrderNo}，请先完成或中止");
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
        var saved = _workOrderRepo.Upsert(updated);
        _dialog.NotifySuccess($"工单 {saved.OrderNo} 已开始");
        return saved;
    }

    /// <inheritdoc />
    public WorkOrder? CompleteWorkOrder(WorkOrder target)
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
        var saved = _workOrderRepo.Upsert(updated);
        _dialog.NotifySuccess($"工单 {saved.OrderNo} 已完成");
        return saved;
    }

    /// <inheritdoc />
    public WorkOrder? AbortWorkOrder(WorkOrder target)
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
            $"确定中止工单「{target.OrderNo}」吗？中止后不可恢复为 Running。",
            "确认中止", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (r != MessageBoxResult.Yes) return null;
        // 中止也写入产量快照（Running 中止时已有部分产量，需保留）
        // 注意：此处用 target.Status 判定原状态，updated 已被 Abort() 改为 Aborted
        if (target.Status == WorkOrderStatus.Running)
        {
            var summary = GetProductionSummary(updated);
            updated.CompletedOkCount = summary.OkCount;
            updated.CompletedNgCount = summary.NgCount;
        }
        var saved = _workOrderRepo.Upsert(updated);
        _dialog.NotifySuccess($"工单 {saved.OrderNo} 已中止");
        return saved;
    }

    /// <inheritdoc />
    public WorkOrderProductionSummary GetProductionSummary(WorkOrder workOrder)
    {
        // 快速路径：已结束工单（Completed/Aborted）若已写入产量快照，直接返回，避免重复扫描日志。
        // CompleteWorkOrder/AbortWorkOrder 在状态切换时已调用本方法的日志聚合分支写入快照。
        // 老数据未回填时 CompletedOkCount 为 null，继续走日志聚合。
        if ((workOrder.Status == WorkOrderStatus.Completed || workOrder.Status == WorkOrderStatus.Aborted)
            && workOrder.CompletedOkCount.HasValue && workOrder.CompletedNgCount.HasValue)
        {
            var ok = workOrder.CompletedOkCount.Value;
            var ng = workOrder.CompletedNgCount.Value;
            var target = workOrder.TargetQuantity;
            return new WorkOrderProductionSummary
            {
                OkCount = ok,
                NgCount = ng,
                AchievementRate = target > 0 ? Math.Min(1.0, (double)ok / target) : 0,
                DefectRate = ok + ng > 0 ? (double)ng / (ok + ng) : 0,
            };
        }

        // 查询策略：优先按 WorkOrderId 查询关联快照（新数据）；
        // 若无关联记录（老数据或未启动采集），回退按 DeviceId + 工单时间窗口查询。
        try
        {
            var logs = _historyService.QueryProductionLogsByWorkOrder(workOrder.Id);

            // 回退：按 DeviceId + 工单时间窗口查询（容老数据无 WorkOrderId）
            if (logs.Count == 0)
            {
                // 时间窗口扩展前后各 5 分钟，避免工单开始/结束边界处丢失快照
                var from = workOrder.PlannedStart.AddMinutes(-5);
                var to = workOrder.PlannedEnd > workOrder.PlannedStart
                    ? workOrder.PlannedEnd.AddMinutes(5)
                    : DateTime.Now;
                logs = _historyService.QueryProductionLogs(from, to, workOrder.DeviceId);
            }

            if (logs.Count == 0)
                return new WorkOrderProductionSummary();

            // OkProduction/NgProduction 是班次内累计值（班次切换时重置基线）。
            // 按班次分组，每班次取末条 - 首条的差值，再跨班次累加。
            // 单条记录场景：first == last 会导致差分为 0，丢失该班次的实际产量。
            // 此时查询该班次在工单首条快照之前的最后一条作为基线（PreBaseline），
            // 差分 = last - PreBaseline；若无基线则视为从 0 开始累计，直接取 last 值。
            var okTotal = 0;
            var ngTotal = 0;
            foreach (var group in logs.GroupBy(p => p.ShiftName ?? string.Empty))
            {
                var ordered = group.OrderBy(p => p.Timestamp).ToList();
                if (ordered.Count == 0) continue;
                var first = ordered[0];
                var last = ordered[^1];

                if (ordered.Count == 1)
                {
                    // 单条记录：查询同设备同班次在工单首条快照之前的最后一条作为基线
                    var baselineTime = first.Timestamp;
                    var baselineShift = first.ShiftName ?? string.Empty;
                    var baseline = _historyService.GetLatestProductionBefore(
                        workOrder.DeviceId, baselineTime, baselineShift);
                    if (baseline != null)
                    {
                        okTotal += Math.Max(0, last.OkProduction - baseline.OkProduction);
                        ngTotal += Math.Max(0, last.NgProduction - baseline.NgProduction);
                    }
                    else
                    {
                        // 无基线：PLC 累计值从 0 开始，直接取末条值
                        okTotal += Math.Max(0, last.OkProduction);
                        ngTotal += Math.Max(0, last.NgProduction);
                    }
                }
                else
                {
                    okTotal += Math.Max(0, last.OkProduction - first.OkProduction);
                    ngTotal += Math.Max(0, last.NgProduction - first.NgProduction);
                }
            }

            var target = workOrder.TargetQuantity;
            var achievementRate = target > 0 ? Math.Min(1.0, (double)okTotal / target) : 0;
            var defectRate = okTotal + ngTotal > 0 ? (double)ngTotal / (okTotal + ngTotal) : 0;

            return new WorkOrderProductionSummary
            {
                OkCount = okTotal,
                NgCount = ngTotal,
                AchievementRate = achievementRate,
                DefectRate = defectRate,
            };
        }
        catch
        {
            return new WorkOrderProductionSummary();
        }
    }

    private WorkOrderProductionSummary CalculateProductionSummary(WorkOrder workOrder, List<ProductionLog> logs)
    {
        var okTotal = 0;
        var ngTotal = 0;
        foreach (var group in logs.GroupBy(p => p.ShiftName ?? string.Empty))
        {
            var ordered = group.OrderBy(p => p.Timestamp).ToList();
            if (ordered.Count == 0) continue;
            var first = ordered[0];
            var last = ordered[^1];
            if (ordered.Count == 1)
            {
                var baseline = _historyService.GetLatestProductionBefore(
                    workOrder.DeviceId, first.Timestamp, first.ShiftName ?? string.Empty);
                if (baseline != null)
                {
                    okTotal += Math.Max(0, last.OkProduction - baseline.OkProduction);
                    ngTotal += Math.Max(0, last.NgProduction - baseline.NgProduction);
                }
                else
                {
                    okTotal += Math.Max(0, last.OkProduction);
                    ngTotal += Math.Max(0, last.NgProduction);
                }
            }
            else
            {
                okTotal += Math.Max(0, last.OkProduction - first.OkProduction);
                ngTotal += Math.Max(0, last.NgProduction - first.NgProduction);
            }
        }

        var target = workOrder.TargetQuantity;
        return new WorkOrderProductionSummary
        {
            OkCount = okTotal,
            NgCount = ngTotal,
            AchievementRate = target > 0 ? Math.Min(1.0, (double)okTotal / target) : 0,
            DefectRate = okTotal + ngTotal > 0 ? (double)ngTotal / (okTotal + ngTotal) : 0,
        };
    }
}
