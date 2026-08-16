using Kanban.Core.Services;
using Kanban.Core.Models;
using Kanban.Core.Data;
using Kanban.Core.Entities;
using MainAPP.Models;
using MainAPP.ViewModels;
using OxyPlot;
using Serilog;
using System.IO;

namespace MainAPP.Services;

/// <summary>
/// 自动日报服务：按配置时刻为每台有数据的设备生成上一自然日 PDF。
/// 通过文件存在性保证重启和同日重复检查不会重复生成。
/// </summary>
public sealed class ProductionDailyReportService : IDisposable
{
    private readonly AppSettings _settings;
    private readonly DeviceRepository _deviceRepository;
    private readonly IHistoryService _historyService;
    private readonly IDefectHistoryReader _defectHistoryStore;
    private readonly IProductionReviewPdfService _pdfService;
    private readonly object _lifecycleLock = new();
    private CancellationTokenSource? _cancellation;
    private Task? _worker;

    public ProductionDailyReportService(
        AppSettings settings,
        DeviceRepository deviceRepository,
        IHistoryService historyService,
        IDefectHistoryReader defectHistoryStore,
        IProductionReviewPdfService pdfService)
    {
        _settings = settings;
        _deviceRepository = deviceRepository;
        _historyService = historyService;
        _defectHistoryStore = defectHistoryStore;
        _pdfService = pdfService;
    }

    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (_worker != null) return;
            _cancellation = new CancellationTokenSource();
            _worker = Task.Run(() => RunAsync(_cancellation.Token));
        }
    }

    public async Task StopAsync()
    {
        Task? worker;
        lock (_lifecycleLock)
        {
            worker = _worker;
            _worker = null;
            _cancellation?.Cancel();
        }

        if (worker != null)
        {
            try { await worker.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { Log.Warning("自动日报服务停止超时"); }
        }

        lock (_lifecycleLock)
        {
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                RunOnce(DateTime.Now);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Error(ex, "自动日报服务异常退出");
        }
    }

    internal void RunOnce(DateTime now)
    {
        // 仅“主节点”实例生成日报（多屏部署只此一台出 PDF，避免重复）。
        if (!_settings.EnableAutomaticDailyReport
            || !_settings.AutomaticDailyReportIsMaster
            || now.TimeOfDay < _settings.AutomaticDailyReportTime)
            return;

        GenerateForDate(now.Date.AddDays(-1));
    }

    internal void GenerateForDate(DateTime reportDate)
    {
        var outputDirectory = Path.Combine(AppSettings.DataRoot, _settings.ConfigDirectory, "Reports");
        Directory.CreateDirectory(outputDirectory);

        foreach (var device in _deviceRepository.GetDevicesSnapshot())
        {
            try
            {
                var safeName = string.Join("_", device.Name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
                var path = Path.Combine(outputDirectory, $"生产日报_{reportDate:yyyyMMdd}_{safeName}.pdf");
                if (File.Exists(path)) continue;

                var data = BuildData(device, reportDate);
                if (data == null) continue;
                _pdfService.Export(path, data);
                Log.Information("自动日报已生成：设备={Device} 日期={Date} 文件={Path}", device.Name, reportDate, path);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "自动日报生成失败：设备={Device} 日期={Date}", device.Name, reportDate);
            }
        }
    }

    private ProductionReviewPdfData? BuildData(Device device, DateTime reportDate)
    {
        var from = reportDate.Date;
        var to = from.AddDays(1).AddTicks(-1);
        var allProduction = _historyService.QueryProductionLogs(from.AddDays(-1), to, device.Id);
        var inWindow = allProduction.Where(log => log.Timestamp >= from && log.Timestamp <= to).ToList();
        var baseline = allProduction.Where(log => log.Timestamp < from).ToList();
        var (ok, ng) = HistoryQueryHelper.SumWindowProduction(inWindow, baseline, from);
        if (inWindow.Count == 1 && HistoryQueryHelper.FindBaselineBeforeWindow(baseline, inWindow[0].ShiftName) == null)
        {
            ok = Math.Max(0, inWindow[0].OkProduction);
            ng = Math.Max(0, inWindow[0].NgProduction);
        }

        var transitions = _historyService.QueryStatusTransitions(device.Id, from, to);
        var initialState = _historyService.GetLatestStatusBefore(device.Id, from)?.CurrentState
            ?? (int)DeviceStatus.Unknown;
        var durations = OeeCalculator.CalculateStateDurations(transitions, from, to, initialState);
        var alarms = _historyService.QueryAlarmEvents(from, to, device.Id);
        if (inWindow.Count == 0 && transitions.Count == 0 && alarms.Count == 0) return null;

        var quality = OeeCalculator.CalculateQualityRate(ok, ng);
        var performance = OeeCalculator.CalculatePerformanceRate(ok, ng, device.TargetCycle, durations.RunTime);
        var availability = OeeCalculator.CalculateAvailabilityRate(durations.RunTime, durations.AlarmTime);
        var totalOutput = ok + ng;
        // 班次锁内快照（审查修复 2026-08-13 复查）：日报生成线程与 UI 线程的 CopySettings 原地写入
        // （锁内 Clear+Add）可能并发——此处两次枚举 Shifts，改为一次锁内快照，避免枚举中途被修改抛异常
        List<ShiftConfig> shifts;
        lock (_settings.ShiftsLock) shifts = _settings.Shifts.ToList();
        var targetHours = shifts.Sum(shift => shift.DurationHours);
        var target = (int)Math.Max(0, Math.Round(device.TargetCycle * targetHours));
        var trend = BuildTrend(inWindow);
        var topAlarms = BuildTopAlarms(alarms);
        var defects = BuildDefects(device, from, to);

        return new ProductionReviewPdfData(
            from,
            to,
            reportDate.ToString("yyyy-MM-dd"),
            ok,
            ng,
            quality,
            OeeCalculator.CalculateOee(quality, performance, availability),
            durations.RunTime / 3600.0,
            durations.PausedTime / 3600.0,
            durations.AlarmTime / 3600.0,
            alarms.Count(eventRecord => eventRecord.EventType == AlarmEventType.Triggered),
            [new DeviceOverviewSummary
            {
                DeviceId = device.Id,
                DeviceName = device.Name,
                OkCount = ok,
                NgCount = ng,
                QualityRate = quality,
                Oee = OeeCalculator.CalculateOee(quality, performance, availability),
                RunTimeHours = durations.RunTime / 3600.0,
                PausedTimeHours = durations.PausedTime / 3600.0,
                AlarmDurationHours = durations.AlarmTime / 3600.0,
                AlarmCount = alarms.Count(eventRecord => eventRecord.EventType == AlarmEventType.Triggered),
            }],
            BuildShiftComparisons(device, shifts, allProduction, alarms, from, to),
            topAlarms,
            trend,
            null,
            null,
            target,
            target > 0 ? Math.Clamp((double)totalOutput / target, 0, 1) : 0,
            defects);
    }

    private static PlotModel? BuildTrend(List<ProductionLog> logs)
    {
        if (logs.Count == 0) return null;
        var points = logs
            .GroupBy(log => new DateTime(log.Timestamp.Year, log.Timestamp.Month, log.Timestamp.Day, log.Timestamp.Hour, 0, 0))
            .OrderBy(group => group.Key)
            .Select(group => group.OrderByDescending(log => log.Timestamp).First())
            .Select(log => (log.Timestamp, log.OkProduction, log.NgProduction));
        return ChartService.BuildProductionChart(points);
    }

    private static List<AlarmOverviewSummary> BuildTopAlarms(List<AlarmEventRecord> alarms)
        => alarms.Where(record => record.EventType == AlarmEventType.Triggered)
            .GroupBy(record => new { record.AlarmName, record.DeviceName, record.PlcAddress })
            .Select(group => new AlarmOverviewSummary
            {
                AlarmName = group.Key.AlarmName,
                DeviceName = group.Key.DeviceName,
                PlcAddress = group.Key.PlcAddress,
                TriggerCount = group.Count(),
            })
            .OrderByDescending(item => item.TriggerCount)
            .Take(5)
            .ToList();

    private List<DefectParetoSummary> BuildDefects(Device device, DateTime from, DateTime to)
    {
        var snapshots = _defectHistoryStore.QueryWindowBounds(from, to, device.Id);
        var result = new List<DefectParetoSummary>();
        foreach (var group in snapshots.GroupBy(snapshot => new { snapshot.DefectId, snapshot.ShiftName }))
        {
            var ordered = group.OrderBy(snapshot => snapshot.Timestamp).ToList();
            var window = ordered.Where(snapshot => snapshot.Timestamp >= from).ToList();
            if (window.Count == 0) continue;
            var baseline = ordered.LastOrDefault(snapshot => snapshot.Timestamp < from);
            var count = baseline != null
                ? Math.Max(0, window[^1].Count - baseline.Count)
                : window.Count > 1 ? Math.Max(0, window[^1].Count - window[0].Count) : window[^1].Count;
            if (count > 0)
                result.Add(new DefectParetoSummary { DefectName = window[^1].DefectName, DeviceName = device.Name, Count = count });
        }
        var total = result.Sum(item => item.Count);
        var cumulative = 0;
        foreach (var item in result.OrderByDescending(item => item.Count).Take(10))
        {
            cumulative += item.Count;
            item.CumulativePercent = total > 0 ? cumulative * 100.0 / total : 0;
        }
        return result.OrderByDescending(item => item.Count).Take(10).ToList();
    }

    private static List<ShiftComparisonSummary> BuildShiftComparisons(
        Device device,
        IReadOnlyList<ShiftConfig> shifts,
        List<ProductionLog> allProduction,
        List<AlarmEventRecord> alarms,
        DateTime from,
        DateTime to)
    {
        var result = new List<ShiftComparisonSummary>();
        foreach (var group in allProduction.Where(log => log.Timestamp >= from && log.Timestamp <= to).GroupBy(log => log.ShiftName))
        {
            var ordered = group.OrderBy(log => log.Timestamp).ToList();
            var baseline = allProduction.Where(log => log.Timestamp < from && log.ShiftName == group.Key).OrderByDescending(log => log.Timestamp).FirstOrDefault();
            var ok = baseline == null ? Math.Max(0, ordered[^1].OkProduction - ordered[0].OkProduction) : Math.Max(0, ordered[^1].OkProduction - baseline.OkProduction);
            var ng = baseline == null ? Math.Max(0, ordered[^1].NgProduction - ordered[0].NgProduction) : Math.Max(0, ordered[^1].NgProduction - baseline.NgProduction);
            var shiftHours = shifts.FirstOrDefault(shift => shift.Name == group.Key)?.DurationHours ?? 0;
            var shiftTarget = device.TargetCycle * shiftHours;
            result.Add(new ShiftComparisonSummary
            {
                ShiftName = group.Key,
                OkCount = ok,
                NgCount = ng,
                AlarmCount = alarms.Count(alarm => alarm.ShiftName == group.Key && alarm.EventType == AlarmEventType.Triggered),
                TargetAchievementRate = shiftTarget > 0
                    ? Math.Clamp((double)(ok + ng) / shiftTarget, 0, 1)
                    : 0,
            });
        }
        return result;
    }

    public void Dispose() => StopAsync().GetAwaiter().GetResult();
}
