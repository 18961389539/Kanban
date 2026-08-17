using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CsvHelper.Configuration.Attributes;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Models;
using MainAPP.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using Serilog;
using MainAPP.Resources;

namespace MainAPP.ViewModels;

public partial class ProductionQueryViewModel : ObservableObject
{
    private readonly IProductionHistoryService _historyService;

    /// <summary>最近一次查询的全量结果（按时间降序缓存，供翻页内存分页复用，避免每次翻页重新全量查询）。</summary>
    private List<ProductionLog> _allLogs = [];

    [ObservableProperty]
    private ObservableCollection<ProductionLog> _productionLogs = new();

    [ObservableProperty]
    private int _totalOk;

    [ObservableProperty]
    private int _totalNg;

    [ObservableProperty]
    private double _qualityRate;

    [ObservableProperty]
    private PlotModel? _productionChart;

    [ObservableProperty]
    private string? _productionInsight;

    public List<string> LastQueryShiftNames { get; private set; } = new();
    public string? QueryError { get; private set; }

    public ProductionQueryViewModel(IProductionHistoryService historyService)
    {
        _historyService = historyService;
    }

    public (int TotalCount, int TotalPages) Query(
        string? deviceId, DateTime from, DateTime to, string? shiftName, int currentPage, int pageSize)
    {
        ProductionLogs.Clear();
        TotalOk = 0; TotalNg = 0; QualityRate = 0;
        ProductionInsight = null;
        ProductionChart = null;
        LastQueryShiftNames.Clear();
        QueryError = null;

        if (string.IsNullOrEmpty(deviceId))
            return (0, 0);

        try
        {
            var sortedAll = QueryProductionLogs(from, to, deviceId, shiftName);

            // 缓存降序全量（ThenBy Id 稳定次级键），翻页仅内存分页复用，不重新查询
            _allLogs = sortedAll.OrderByDescending(p => p.Timestamp).ThenByDescending(p => p.Id).ToList();

            var totalCount = _allLogs.Count;
            var totalPages = HistoryQueryHelper.CalcTotalPages(totalCount, pageSize);

            Page(currentPage, pageSize);

            if (totalCount > 0)
            {
                var shiftGroups = HistoryQueryHelper.SplitShiftInstances(sortedAll);

                // 窗口基准：窗口起点之前、同班次实例的累计值，用于窗口差分（产量与时间同口径）
                var baselineCandidates = QueryProductionLogs(from.AddDays(-1), from, deviceId, null);

                (TotalOk, TotalNg) = HistoryQueryHelper.SumWindowProduction(sortedAll, baselineCandidates, from);
                QualityRate = OeeCalculator.CalculateQualityRate(TotalOk, TotalNg);

                LastQueryShiftNames = sortedAll.Select(p => p.ShiftName)
                    .Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).Distinct().ToList();

                var chartData = shiftGroups
                    .SelectMany(g => g
                        .GroupBy(p => new DateTime(
                            p.Timestamp.Year, p.Timestamp.Month, p.Timestamp.Day,
                            p.Timestamp.Hour, p.Timestamp.Minute / 15 * 15, 0))
                        .OrderBy(b => b.Key)
                        .Select(b => b.OrderByDescending(p => p.Timestamp).First()))
                    .OrderBy(p => p.Timestamp)
                    .Select(p => (p.Timestamp, p.OkProduction, p.NgProduction))
                    .ToList();
                ProductionChart = ChartService.BuildProductionChart(chartData);

                ProductionInsight = BuildProductionInsight(chartData);

                AnnotateProductionChart(chartData);
            }

            return (totalCount, totalPages);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "产量查询失败");
            QueryError = string.Format(Strings.F063, ex.Message);
            return (0, 0);
        }
    }

    /// <summary>翻页：从缓存全量结果内存分页，不重新查询（KPI/图表不变）。</summary>
    public void Page(int currentPage, int pageSize)
    {
        var pageItems = HistoryQueryHelper.PageItems(_allLogs, currentPage, pageSize);
        ProductionLogs.Clear();
        foreach (var item in pageItems)
            ProductionLogs.Add(item);
    }

    private List<ProductionLog> QueryProductionLogs(DateTime from, DateTime to, string? deviceId, string? shiftName)
        => _historyService is IHistoryQueryExecutor strict
            ? strict.QueryProductionLogsStrict(from, to, deviceId, shiftName)
            : _historyService.QueryProductionLogs(from, to, deviceId, shiftName);

    public void Reset()
    {
        ProductionLogs.Clear();
        _allLogs = [];
        TotalOk = 0; TotalNg = 0; QualityRate = 0;
        ProductionChart = null;
        ProductionInsight = null;
        LastQueryShiftNames.Clear();
    }

    public string? BuildCsv(DateTime fromDate, DateTime toDate)
    {
        if (ProductionLogs.Count == 0) return null;

        var rows = ProductionLogs.Select(p => new ProductionCsvRow
        {
            Timestamp = p.Timestamp,
            DeviceId = p.DeviceId,
            DeviceName = p.DeviceName,
            ShiftName = p.ShiftName,
            OkProduction = p.OkProduction,
            NgProduction = p.NgProduction,
            StatusWord = p.StatusWord
        }).ToList();

        return HistoryQueryHelper.BuildCsv(rows,
            $"# 查询区间：{fromDate:yyyy-MM-dd HH:mm:ss} ~ {toDate:yyyy-MM-dd HH:mm:ss}",
            $"# 总 OK：{TotalOk} 件，总 NG：{TotalNg} 件，C良品率：{QualityRate:P2}",
            $"# {ProductionInsight ?? Strings.M176}");
    }

    private static string? BuildProductionInsight(List<(DateTime Time, int Ok, int Ng)> chartData)
    {
        if (chartData.Count < 2) return null;

        var okValues = chartData.Select(d => (double)d.Ok).ToList();
        var avgOk = okValues.Average();
        var peakIdx = okValues.IndexOf(okValues.Max());
        var peak = chartData[peakIdx];

        if (avgOk <= 0) return null;
        var deviation = (peak.Ok - avgOk) / avgOk;

        var main = deviation switch
        {
            > 0.2 => string.Format(Strings.F239, peak.Time, peak.Ok, deviation),
            < -0.2 => string.Format(Strings.F240, peak.Time, peak.Ok, -deviation),
            _ => string.Format(Strings.F064, avgOk, peak.Ok, peak.Time)
        };

        // 突降检测：复用 AnnotateProductionChart 的判断逻辑（curr.Ok < prev.Ok * 0.8），
        // 在文本洞察中追加提示，避免用户错过图表标注。
        List<(DateTime Time, int PrevOk, int CurrOk)> drops = [];
        for (int i = 1; i < chartData.Count; i++)
        {
            var prev = chartData[i - 1];
            var curr = chartData[i];
            if (prev.Ok > 0 && curr.Ok < prev.Ok * 0.8)
                drops.Add((curr.Time, prev.Ok, curr.Ok));
        }
        if (drops.Count > 0)
        {
            // 取最严重的突降（降幅最大）
            var worst = drops.MaxBy(d => (d.PrevOk - d.CurrOk) / (double)d.PrevOk);
            var dropPct = 1.0 - worst.CurrOk / (double)worst.PrevOk;
            var suffix = drops.Count == 1
                ? string.Format(Strings.F048, worst.Time, dropPct, worst.PrevOk, worst.CurrOk)
                : string.Format(Strings.F050, drops.Count, worst.Time, dropPct, worst.PrevOk, worst.CurrOk);
            return $"{main}\n{suffix}";
        }

        return main;
    }

    private void AnnotateProductionChart(List<(DateTime Time, int Ok, int Ng)> chartData)
    {
        if (ProductionChart == null || chartData.Count < 2) return;

        for (int i = 1; i < chartData.Count; i++)
        {
            var prev = chartData[i - 1];
            var curr = chartData[i];

            if (prev.Ok > 0 && curr.Ok < prev.Ok * 0.8)
            {
                var annot = new PointAnnotation
                {
                    X = DateTimeAxis.ToDouble(curr.Time),
                    Y = curr.Ok,
                    Text = string.Format(Strings.F043, curr.Time),
                    Fill = ChartPalette.Alarm,
                    Stroke = OxyColors.White,
                    TextColor = ChartPalette.Alarm,
                };
                ProductionChart.Annotations.Add(annot);
            }
        }

        ProductionChart.InvalidatePlot(true);
    }

    private class ProductionCsvRow
    {
        [Name("时间")] public DateTime Timestamp { get; set; }
        [Name("设备ID")] public string? DeviceId { get; set; }
        [Name("设备名称")] public string? DeviceName { get; set; }
        [Name("班次")] public string? ShiftName { get; set; }
        [Name("OK产量")] public int OkProduction { get; set; }
        [Name("NG产量")] public int NgProduction { get; set; }
        [Name("状态字")] public int StatusWord { get; set; }
    }
}
