using Kanban.Core.Services;
using Kanban.Core.Models;
using Kanban.Core.Data;
using Kanban.Core.Entities;
using Kanban.Core.Entities;
using Kanban.Core.Data;
using Kanban.Core.Models;
using MainAPP.Models;
using MainAPP.Resources;

namespace MainAPP.Services;

public sealed record ReviewAlarmAnalysisData(
    string AlarmName,
    string DeviceName,
    string PlcAddress,
    int TriggerCount,
    double AverageIntervalMinutes,
    bool IsHighFrequency,
    int OutputBefore,
    int OutputAfter,
    string ShiftName,
    double TotalDurationHours);

public sealed record ReviewStatusSegmentData(
    DateTime Start,
    DateTime End,
    string StatusText,
    int StatusWord,
    int OutputDelta,
    int AlarmCount,
    bool HasNoOutput);

public sealed record ReviewDefectConcentrationData(
    string DefectName,
    string ShiftName,
    string TimeRangeText,
    int Count,
    double Share);

public sealed record ProductionReviewAnalysisResult(
    IReadOnlyList<ReviewAlarmAnalysisData> Alarms,
    IReadOnlyList<ReviewStatusSegmentData> StatusTimeline,
    IReadOnlyList<ReviewDefectConcentrationData> DefectConcentrations,
    IReadOnlyList<string> HealthIssues,
    int HealthScore,
    string WorkOrderText,
    string ProductText,
    string RecipeText);

public interface IProductionReviewAnalysisService
{
    ProductionReviewAnalysisResult Analyze(
        Device device,
        IReadOnlyList<StatusTransitionRecord> transitions,
        IReadOnlyList<AlarmEventRecord> alarms,
        IReadOnlyList<ProductionLog> productionLogs,
        DateTime from,
        DateTime to,
        DateTime comparisonFrom,
        DateTime comparisonTo);
}

/// <summary>
/// 生产复盘领域分析服务。只负责单设备历史数据的业务计算，WPF 层仅负责展示结果。
/// </summary>
public sealed class ProductionReviewAnalysisService : IProductionReviewAnalysisService
{
    private readonly IProductionReviewDataService _reviewDataService;
    private readonly IDefectHistoryReader? _defectHistoryStore;
    private readonly WorkOrderRepository? _workOrderRepository;
    private readonly IProductionReviewAlarmAnalysisService _alarmAnalysisService;
    private readonly IProductionReviewStatusTimelineService _statusTimelineService;
    private readonly IProductionReviewHealthScoreService _healthScoreService;

    public ProductionReviewAnalysisService(
        IProductionReviewDataService reviewDataService,
        IDefectHistoryReader? defectHistoryStore = null,
        WorkOrderRepository? workOrderRepository = null,
        IProductionReviewAlarmAnalysisService? alarmAnalysisService = null,
        IProductionReviewStatusTimelineService? statusTimelineService = null,
        IProductionReviewHealthScoreService? healthScoreService = null)
    {
        _reviewDataService = reviewDataService;
        _defectHistoryStore = defectHistoryStore;
        _workOrderRepository = workOrderRepository;
        _alarmAnalysisService = alarmAnalysisService ?? new ProductionReviewAlarmAnalysisService();
        _statusTimelineService = statusTimelineService ?? new ProductionReviewStatusTimelineService();
        _healthScoreService = healthScoreService ?? new ProductionReviewHealthScoreService();
    }

    public ProductionReviewAnalysisService(
        IHistoryService historyService,
        IDefectHistoryReader? defectHistoryStore = null,
        WorkOrderRepository? workOrderRepository = null,
        IProductionReviewAlarmAnalysisService? alarmAnalysisService = null,
        IProductionReviewStatusTimelineService? statusTimelineService = null,
        IProductionReviewHealthScoreService? healthScoreService = null)
        : this(
            new ProductionReviewDataService(historyService),
            defectHistoryStore,
            workOrderRepository,
            alarmAnalysisService,
            statusTimelineService,
            healthScoreService)
    {
    }

    public ProductionReviewAnalysisResult Analyze(
        Device device,
        IReadOnlyList<StatusTransitionRecord> transitions,
        IReadOnlyList<AlarmEventRecord> alarms,
        IReadOnlyList<ProductionLog> productionLogs,
        DateTime from,
        DateTime to,
        DateTime comparisonFrom,
        DateTime comparisonTo)
    {
        var initialStatus = _reviewDataService.GetLatestStatusBefore(device.Id, from);
        var timeline = _statusTimelineService.Build(
            device.Id,
            transitions,
            alarms,
            productionLogs,
            initialStatus?.EventTime ?? from,
            initialStatus?.CurrentState ?? (int)DeviceStatus.Unknown,
            from,
            to);
        var alarmAnalysis = _alarmAnalysisService.Analyze(alarms, productionLogs);
        var concentrations = BuildDefectConcentrations(device, from, to);
        var previousAlarmCount = _reviewDataService.QueryAlarmEvents(comparisonFrom, comparisonTo, device.Id)
            .Count(alarm => alarm.EventType == AlarmEventType.Triggered);
        var previousDefectCount = _defectHistoryStore == null
            ? 0
            : BuildDefectConcentrations(device, comparisonFrom, comparisonTo).Sum(item => item.Count);
        var health = _healthScoreService.Calculate(
            device,
            productionLogs,
            alarms,
            timeline,
            concentrations,
            previousAlarmCount,
            previousDefectCount,
            from,
            to);
        var runningWorkOrder = _workOrderRepository?.GetRunningByDevice(device.Id);

        return new ProductionReviewAnalysisResult(
            alarmAnalysis,
            timeline,
            concentrations,
            health.Issues,
            health.Score,
            runningWorkOrder == null
                ? "暂无运行工单"
                : string.Format(Strings.F038, runningWorkOrder.OrderNo, runningWorkOrder.TargetQuantity),
            runningWorkOrder == null
                ? "暂无产品信息"
                : $"{runningWorkOrder.ProductCode} · {runningWorkOrder.ProductName}",
            string.IsNullOrWhiteSpace(device.RecipeName)
                ? "暂无配方信息"
                : $"{device.RecipeName} · {device.RecipeValue:N0}");
    }

    private List<ReviewDefectConcentrationData> BuildDefectConcentrations(Device device, DateTime from, DateTime to)
    {
        if (_defectHistoryStore == null) return [];
        var snapshots = _defectHistoryStore.Query(from.AddDays(-1), to, device.Id);
        var cells = new List<(string Name, string Shift, DateTime Bucket, int Count)>();
        foreach (var group in snapshots.GroupBy(snapshot => new { snapshot.DefectId, snapshot.ShiftName }))
        {
            var previous = group.OrderBy(snapshot => snapshot.Timestamp)
                .LastOrDefault(snapshot => snapshot.Timestamp < from);
            foreach (var snapshot in group.Where(snapshot => snapshot.Timestamp >= from && snapshot.Timestamp <= to)
                .OrderBy(snapshot => snapshot.Timestamp))
            {
                var count = previous == null
                    ? Math.Max(0, snapshot.Count)
                    : Math.Max(0, snapshot.Count - previous.Count);
                if (count > 0)
                {
                    var bucket = new DateTime(snapshot.Timestamp.Year, snapshot.Timestamp.Month,
                        snapshot.Timestamp.Day, snapshot.Timestamp.Hour, 0, 0);
                    cells.Add((snapshot.DefectName, snapshot.ShiftName, bucket, count));
                }
                previous = snapshot;
            }
        }

        var total = cells.Sum(cell => cell.Count);
        return cells
            .GroupBy(cell => new { cell.Name, cell.Shift, cell.Bucket })
            .Select(group => new ReviewDefectConcentrationData(
                group.Key.Name,
                group.Key.Shift,
                group.Key.Bucket.ToString("MM-dd HH:00"),
                group.Sum(cell => cell.Count),
                total > 0 ? (double)group.Sum(cell => cell.Count) / total : 0))
            .OrderByDescending(item => item.Count)
            .Take(10)
            .ToList();
    }

}
