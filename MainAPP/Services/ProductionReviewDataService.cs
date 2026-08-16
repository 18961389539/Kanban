using Kanban.Collector.Core.Services;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;

namespace MainAPP.Services;

public sealed record ProductionReviewDataSnapshot(
    Dictionary<string, List<ProductionLog>> ProductionLogsByDevice,
    Dictionary<string, List<StatusTransitionRecord>> StatusTransitionsByDevice,
    Dictionary<string, List<AlarmEventRecord>> AlarmEventsByDevice);

public interface IProductionReviewDataService
{
    ProductionReviewDataSnapshot QueryWindow(
        DateTime from,
        DateTime to,
        IReadOnlyList<string> deviceIds);

    DeviceReviewRangeData QueryDeviceRange(
        string deviceId,
        DateTime from,
        DateTime to);

    StatusTransitionRecord? GetLatestStatusBefore(string deviceId, DateTime before);

    List<AlarmEventRecord> QueryAlarmEvents(DateTime from, DateTime to, string deviceId);
}

public sealed record DeviceReviewRangeData(
    List<ProductionLog> ProductionLogs,
    List<StatusTransitionRecord> StatusTransitions,
    StatusTransitionRecord? LatestStatusBefore,
    List<AlarmEventRecord> AlarmEvents);

/// <summary>
/// 生产复盘历史数据访问服务。统一处理窗口、基线和设备状态初始记录查询。
/// </summary>
public sealed class ProductionReviewDataService : IProductionReviewDataService
{
    private readonly IHistoryService _historyService;

    public ProductionReviewDataService(IHistoryService historyService)
    {
        _historyService = historyService;
    }

    public ProductionReviewDataSnapshot QueryWindow(
        DateTime from,
        DateTime to,
        IReadOnlyList<string> deviceIds)
    {
        return new ProductionReviewDataSnapshot(
            _historyService.QueryProductionLogsBatch(from.AddDays(-1), to, deviceIds),
            _historyService.QueryStatusTransitionsBatch(from, to, deviceIds),
            _historyService.QueryAlarmEventsBatch(from, to, deviceIds));
    }

    public DeviceReviewRangeData QueryDeviceRange(
        string deviceId,
        DateTime from,
        DateTime to)
    {
        var productionByDevice = _historyService.QueryProductionLogsBatch(from.AddDays(-1), to, [deviceId]);
        productionByDevice.TryGetValue(deviceId, out var productionLogs);
        productionLogs ??= [];
        return new DeviceReviewRangeData(
            productionLogs,
            _historyService.QueryStatusTransitions(deviceId, from, to),
            _historyService.GetLatestStatusBefore(deviceId, from),
            _historyService.QueryAlarmEvents(from, to, deviceId));
    }

    public StatusTransitionRecord? GetLatestStatusBefore(string deviceId, DateTime before)
        => _historyService.GetLatestStatusBefore(deviceId, before);

    public List<AlarmEventRecord> QueryAlarmEvents(DateTime from, DateTime to, string deviceId)
        => _historyService.QueryAlarmEvents(from, to, deviceId);
}
