using Kanban.Contracts.Dtos;
using Kanban.Contracts.Enums;
using Kanban.Core.Entities;
using Kanban.Core.Services;
using Microsoft.Extensions.Logging;
using DeviceStatus = Kanban.Contracts.Enums.DeviceStatus;
using DefectSeverity = Kanban.Contracts.Enums.DefectSeverity;
using DefectCategory = Kanban.Contracts.Enums.DefectCategory;
using AlarmEventType = Kanban.Contracts.Enums.AlarmEventType;

namespace Kanban.Collector.Services;

/// <summary>
/// 历史查询服务端实现：把 <see cref="HistoryQueryRequest"/> 分发到 Core 的 Store 查询，
/// 实体 → DTO 映射后返回。展示端（MainAPP）不再直接持有 SQLite，统一经此查询。
/// 语义约定：返回范围内全部匹配记录（客户端内存分页），LatestFirst=true 时只返回最新一条。
/// </summary>
public sealed class HistoryQueryHandler
{
    private readonly IHistoryService _history;
    private readonly DefectHistoryStore _defectStore;
    private readonly ILogger<HistoryQueryHandler> _logger;

    public HistoryQueryHandler(
        IHistoryService history,
        DefectHistoryStore defectStore,
        ILogger<HistoryQueryHandler> logger)
    {
        _history = history;
        _defectStore = defectStore;
        _logger = logger;
    }

    public async Task<HistoryQueryResponse> QueryAsync(HistoryQueryRequest request, CancellationToken cancellationToken = default)
    {
        // EF Core 查询为同步 IO，且批量落在后台线程；直接包 Task.Run 避免阻塞 SignalR 调度线程
        return await Task.Run(() => QueryCore(request), cancellationToken);
    }

    private HistoryQueryResponse QueryCore(HistoryQueryRequest request)
    {
        try
        {
            var (from, to) = NormalizeRange(request);
            var deviceId = request.DeviceId;

            return request.QueryType switch
            {
                HistoryQueryType.ProductionLog => QueryProductionLogs(request, from, to, deviceId),
                HistoryQueryType.AlarmEvent => QueryAlarmEvents(request, from, to, deviceId),
                HistoryQueryType.StatusTransition => QueryStatusTransitions(request, from, to, deviceId),
                HistoryQueryType.DefectSnapshot => QueryDefectSnapshots(request, from, to, deviceId),
                _ => Empty(request),
            };
        }
        catch (Exception ex)
        {
            // 结构化错误：客户端据此区分"真实空数据"与"查询失败"，而非把故障当空结果显示
            _logger.LogError(ex, "历史查询失败 QueryType={QueryType} Device={DeviceId}", request.QueryType, request.DeviceId);
            return new HistoryQueryResponse
            {
                Page = request.Page,
                PageSize = request.PageSize,
                Error = $"历史查询失败: {ex.Message}",
            };
        }
    }

    // ──────────── 各类型查询 ────────────

    private HistoryQueryResponse QueryProductionLogs(HistoryQueryRequest request, DateTime from, DateTime to, string? deviceId)
    {
        // 按工单查询：工单关联记录量有限，全量拉取
        if (request.WorkOrderId.HasValue)
        {
            var items = _history.QueryProductionLogsByWorkOrder(request.WorkOrderId.Value)
                .Select(ToDto)
                .ToList();
            return Build(items, request);
        }

        // 服务端分页下推（SQL Skip/Take + Count）：历史查询不再全量 ToList 传输百万级记录；
        // LatestFirst 同样下推为 SQL 层 OrderByDescending().Take(1)
        if (request.LatestFirst)
        {
            var latest = _history.QueryLatestProductionLog(from, to, deviceId, request.ShiftName);
            var items = latest is null
                ? new List<ProductionLogDto>()
                : new List<ProductionLogDto> { ToDto(latest) };
            return Build(items, request);
        }

        var (pageItems, total) = _history.QueryProductionLogsPaged(
            from, to, deviceId, request.ShiftName, request.Page, request.PageSize);
        return Build(pageItems.Select(ToDto).ToList(), request, total);
    }

    private HistoryQueryResponse QueryAlarmEvents(HistoryQueryRequest request, DateTime from, DateTime to, string? deviceId)
    {
        // AlarmId 精确过滤（GetLatestAlarmEvent 语义）
        if (!string.IsNullOrWhiteSpace(request.AlarmId))
        {
            var latest = _history.QueryAlarmEvents(from, to, deviceId, request.ShiftName)
                .Where(e => e.AlarmId == request.AlarmId)
                .OrderByDescending(e => e.EventTime)
                .FirstOrDefault();
            return Build(latest is null
                ? new List<AlarmEventRecordDto>()
                : new List<AlarmEventRecordDto> { ToDto(latest) }, request);        }

        var events = _history.QueryAlarmEvents(from, to, deviceId, request.ShiftName);
        var items = request.LatestFirst
            ? events.OrderByDescending(e => e.EventTime).Take(1).Select(ToDto).ToList()
            : events.Select(ToDto).ToList();
        return Build(items, request);
    }

    private HistoryQueryResponse QueryStatusTransitions(HistoryQueryRequest request, DateTime from, DateTime to, string? deviceId)
    {
        if (deviceId is null)
            return Empty(request);

        var transitions = _history.QueryStatusTransitions(deviceId, from, to, request.ShiftName);
        var items = request.LatestFirst
            ? transitions.OrderByDescending(t => t.EventTime).Take(1).Select(ToDto).ToList()
            : transitions.Select(ToDto).ToList();
        return Build(items, request);
    }

    private HistoryQueryResponse QueryDefectSnapshots(HistoryQueryRequest request, DateTime from, DateTime to, string? deviceId)
    {
        if (deviceId is null)
            return Empty(request);

        var snapshots = _defectStore.Query(from, to, deviceId);
        var items = request.LatestFirst
            ? snapshots.OrderByDescending(s => s.Timestamp).Take(1).Select(ToDto).ToList()
            : snapshots.Select(ToDto).ToList();
        return Build(items, request);
    }

    // ──────────── 工具 ────────────

    private static (DateTime From, DateTime To) NormalizeRange(HistoryQueryRequest request)
    {
        var from = request.From ?? DateTime.MinValue;
        var to = request.To ?? DateTime.MaxValue;
        return (from, to);
    }

    private static HistoryQueryResponse Build<T>(List<T> items, HistoryQueryRequest request, int? totalOverride = null)
    {
        var response = new HistoryQueryResponse
        {
            // 分页查询时 totalOverride 为服务端 Count（全量总数）；否则等于本页 items 数量（未分页类型）
            Total = totalOverride ?? items.Count,
            Page = request.Page,
            PageSize = request.PageSize,
        };
        switch (items)
        {
            case List<ProductionLogDto> logs:
                response = response with { ProductionLogs = logs };
                break;
            case List<AlarmEventRecordDto> alarms:
                response = response with { AlarmEvents = alarms };
                break;
            case List<StatusTransitionRecordDto> transitions:
                response = response with { StatusTransitions = transitions };
                break;
            case List<DefectSnapshotRecordDto> defects:
                response = response with { DefectSnapshots = defects };
                break;
        }
        return response;
    }

    private static HistoryQueryResponse Empty(HistoryQueryRequest request)
        => new()
        {
            Total = 0,
            Page = request.Page,
            PageSize = request.PageSize,
        };

    // ──────────── 实体 → DTO ────────────

    private static ProductionLogDto ToDto(ProductionLog e) => new()
    {
        Id = e.Id,
        DeviceId = e.DeviceId,
        DeviceName = e.DeviceName,
        ShiftName = e.ShiftName,
        WorkOrderId = e.WorkOrderId,
        OkProduction = e.OkProduction,
        NgProduction = e.NgProduction,
        StatusWord = e.StatusWord,
        Timestamp = e.Timestamp,
    };

    private static AlarmEventRecordDto ToDto(AlarmEventRecord e) => new()
    {
        Id = e.Id,
        DeviceId = e.DeviceId,
        DeviceName = e.DeviceName,
        AlarmId = e.AlarmId,
        AlarmName = e.AlarmName,
        PlcAddress = e.PlcAddress,
        EventType = (AlarmEventType)e.EventType,
        EventTime = e.EventTime,
        ShiftName = e.ShiftName,
    };

    private static StatusTransitionRecordDto ToDto(StatusTransitionRecord e) => new()
    {
        Id = e.Id,
        DeviceId = e.DeviceId,
        DeviceName = e.DeviceName,
        PreviousState = (DeviceStatus)e.PreviousState,
        CurrentState = (DeviceStatus)e.CurrentState,
        EventTime = e.EventTime,
        ShiftName = e.ShiftName,
    };

    private static DefectSnapshotRecordDto ToDto(DefectSnapshotRecord e) => new()
    {
        Id = e.Id,
        DeviceId = e.DeviceId,
        DeviceName = e.DeviceName,
        DefectId = e.DefectId,
        DefectName = e.DefectName,
        Severity = (DefectSeverity)e.Severity,
        Category = (DefectCategory)e.Category,
        ShiftName = e.ShiftName,
        Count = e.Count,
        Timestamp = e.Timestamp,
    };
}
