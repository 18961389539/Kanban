using Kanban.Collector.Core.Services;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using MainAPP.Resources;
using System.Globalization;
using System.IO;
using MainAPP.ViewModels;
using PdfSharpCore.Drawing;
using PdfSharpCore.Fonts;
using PdfSharpCore.Pdf;
using OxyPlot;
using OxyPlot.SkiaSharp;

namespace MainAPP.Services;

public sealed record ProductionReviewPdfData(
    DateTime From,
    DateTime To,
    string ShiftName,
    int TotalOk,
    int TotalNg,
    double QualityRate,
    double Oee,
    double RunTimeHours,
    double PausedTimeHours,
    double AlarmDurationHours,
    int AlarmCount,
    IReadOnlyList<DeviceOverviewSummary> Devices,
    IReadOnlyList<ShiftComparisonSummary> Shifts,
    IReadOnlyList<AlarmOverviewSummary> TopAlarms,
    PlotModel? TrendChart = null,
    PlotModel? OeeWaterfallChart = null,
    PlotModel? ProductionHeatmapChart = null,
    int TargetOutput = 0,
    double OutputAchievementRate = 0,
    IReadOnlyList<DefectParetoSummary>? Defects = null,
    string ComparisonLabel = "",
    int BaselineTotalOutput = 0,
    double BaselineQualityRate = 0,
    double BaselineOee = 0,
    int OutputDelta = 0,
    double QualityRateDelta = 0,
    double OeeDelta = 0,
    double TotalDowntimeHours = 0,
    double AverageAlarmDurationMinutes = 0,
    double MtbfHours = 0,
    string AvailabilityLossText = "",
    string PerformanceLossText = "",
    string QualityLossText = "");

public interface IProductionReviewPdfService
{
    void Export(string path, ProductionReviewPdfData data);
}

/// <summary>
/// 生产复盘 PDF 导出服务。使用系统中文字体，避免把 PDF 排版和文件写入放进 ViewModel。
/// </summary>
public sealed class ProductionReviewPdfService : IProductionReviewPdfService
{
    private static readonly object FontResolverLock = new();
    private static bool _fontResolverConfigured;

    public void Export(string path, ProductionReviewPdfData data)
    {
        EnsureFontResolver();

        using var document = new PdfDocument();
        var canvas = new PdfCanvas(document);
        canvas.Title(Strings.Msg_ProductionReviewReport);
        canvas.Text(Strings.Msg_ProductionReviewReport, 18, true);
        canvas.Text(string.Format(Strings.Prompt_Range, data.From, data.To), 9);
        canvas.Text(string.Format(Strings.Prompt_CurrentShift, data.ShiftName), 9);
        canvas.Space(8);

        canvas.Section(Strings.Msg_CoreMetrics);
        canvas.TableHeader(Strings.Msg_Metric, Strings.Msg_Value);
        canvas.Row(Strings.Msg_TotalOK, data.TotalOk.ToString("N0", CultureInfo.InvariantCulture));
        canvas.Row(Strings.Msg_TotalNG, data.TotalNg.ToString("N0", CultureInfo.InvariantCulture));
        canvas.Row(Strings.Msg_QualityRate, data.QualityRate.ToString("P1", CultureInfo.InvariantCulture));
        canvas.Row("OEE", data.Oee.ToString("P1", CultureInfo.InvariantCulture));
        canvas.Row(Strings.Msg_RunTime, $"{data.RunTimeHours:F2}h");
        canvas.Row(Strings.Msg_IdleTime, $"{data.PausedTimeHours:F2}h");
        canvas.Row(Strings.Msg_AlarmTime, $"{data.AlarmDurationHours:F2}h");
        canvas.Row(Strings.Msg_AlarmCount2, data.AlarmCount.ToString(CultureInfo.InvariantCulture));
        canvas.Row(Strings.Msg_TargetOutput, data.TargetOutput.ToString("N0", CultureInfo.InvariantCulture));
        canvas.Row(Strings.Msg_TargetAchievementRate, data.OutputAchievementRate.ToString("P1", CultureInfo.InvariantCulture));

        canvas.Section(Strings.Msg_ProductionTrend);
        canvas.Chart(data.TrendChart, 520, 220);

        canvas.Section(Strings.Msg_DeviceDetails);
        canvas.TableHeader(Strings.Msg_Device, "OK", "NG", Strings.Msg_QualityRate, "OEE", Strings.Msg_AlarmCount2);
        foreach (var device in data.Devices)
        {
            canvas.Row(
                device.DeviceName,
                device.OkCount.ToString("N0", CultureInfo.InvariantCulture),
                device.NgCount.ToString("N0", CultureInfo.InvariantCulture),
                device.QualityRate.ToString("P1", CultureInfo.InvariantCulture),
                device.Oee.ToString("P1", CultureInfo.InvariantCulture),
                device.AlarmCount.ToString(CultureInfo.InvariantCulture));
        }

        canvas.NewPage();
        canvas.Text(Strings.Msg_ProductionReviewReport, 16, true);
        canvas.Section(Strings.Msg_OEELossBreakdown);
        canvas.Chart(data.OeeWaterfallChart, 520, 210);
        canvas.Row(Strings.Msg_Availability, data.AvailabilityLossText);
        canvas.Row(Strings.Msg_Performance, data.PerformanceLossText);
        canvas.Row(Strings.Msg_QualityRate, data.QualityLossText);
        canvas.Section(Strings.Msg_PeriodComparison);
        canvas.Row(data.ComparisonLabel, string.Format(Strings.Prompt_OutputQualityRateOEE2, data.BaselineTotalOutput, data.BaselineQualityRate, data.BaselineOee));
        canvas.Row(Strings.Msg_CurrentChange, string.Format(Strings.Prompt_OutputQualityRateOEE, FormatSigned(data.OutputDelta), FormatSignedPercentage(data.QualityRateDelta), FormatSignedPercentage(data.OeeDelta)));
        canvas.Section(Strings.Msg_DowntimeAnalysis2);
        canvas.Row(Strings.Lbl_TotalDowntime, $"{data.TotalDowntimeHours:F2}h");
        canvas.Row(Strings.Lbl_AverageAlarmDuration, $"{data.AverageAlarmDurationMinutes:F1}min");
        canvas.Row("MTBF", $"{data.MtbfHours:F2}h");
        canvas.Section(Strings.Msg_ProductionHeatmap);
        canvas.Chart(data.ProductionHeatmapChart, 520, 230);

        canvas.NewPage();
        canvas.Text(Strings.Msg_ProductionReviewReport, 16, true);
        canvas.Section(Strings.Msg_ShiftComparison);
        canvas.TableHeader(Strings.Lbl_Shift, "OK", "NG", Strings.Lbl_TotalOutput, Strings.Msg_QualityRate, Strings.Msg_AlarmCount2);
        foreach (var shift in data.Shifts)
        {
            canvas.Row(
                shift.ShiftName,
                shift.OkCount.ToString("N0", CultureInfo.InvariantCulture),
                shift.NgCount.ToString("N0", CultureInfo.InvariantCulture),
                shift.TotalCount.ToString("N0", CultureInfo.InvariantCulture),
                shift.OkRatio.ToString("P1", CultureInfo.InvariantCulture),
                shift.AlarmCount.ToString(CultureInfo.InvariantCulture));
        }

        canvas.Section(Strings.Msg_TopAlarms);
        canvas.TableHeader(Strings.Lbl_AlarmName, Strings.Msg_Device, Strings.Lbl_TriggeredCount, Strings.Msg_TotalDuration);
        foreach (var alarm in data.TopAlarms)
        {
            canvas.Row(
                alarm.AlarmName,
                alarm.DeviceName,
                alarm.TriggerCount.ToString(CultureInfo.InvariantCulture),
                $"{alarm.TotalDurationHours:F2}h");
        }

            canvas.Section(Strings.Msg_DefectPareto);
            canvas.TableHeader(Strings.Msg_Product, Strings.Msg_Device, Strings.Msg_Count2, Strings.Msg_CumulativeRatio);
            foreach (var defect in data.Defects ?? Array.Empty<DefectParetoSummary>())
            {
                canvas.Row(defect.DefectName, defect.DeviceName,
                defect.Count.ToString("N0", CultureInfo.InvariantCulture),
                defect.CumulativePercent.ToString("F1", CultureInfo.InvariantCulture) + "%");
            }

        document.Save(path);
    }

    private static void EnsureFontResolver()
    {
        if (_fontResolverConfigured) return;
        lock (FontResolverLock)
        {
            if (_fontResolverConfigured) return;
            GlobalFontSettings.FontResolver = new WindowsChineseFontResolver();
            _fontResolverConfigured = true;
        }
    }

    private static string FormatSigned(int value) => ProductionReviewCalculations.FormatSigned(value);
    private static string FormatSignedPercentage(double value) => ProductionReviewCalculations.FormatSignedPercentage(value);

    private sealed class WindowsChineseFontResolver : IFontResolver
    {
        public string DefaultFontName => "Microsoft YaHei";

        public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
        {
            var face = isBold ? "msyh-bold" : "msyh";
            return new FontResolverInfo(face);
        }

        public byte[] GetFont(string faceName)
        {
            var fontDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            var fileName = faceName == "msyh-bold" ? "Dengb.ttf" : "Deng.ttf";
            var path = Path.Combine(fontDirectory, fileName);
            if (!File.Exists(path))
                throw new FileNotFoundException(Strings.Msg_MicrosoftYaHeiFontFoundCannotGenerate, path);
            return File.ReadAllBytes(path);
        }
    }

    private sealed class PdfCanvas : IDisposable
    {
        private readonly PdfDocument _document;
        private PdfPage _page;
        private XGraphics _graphics;
        private double _y = 36;
        private readonly XFont _bodyFont = new("Microsoft YaHei", 9, XFontStyle.Regular);
        private readonly XFont _boldFont = new("Microsoft YaHei", 9, XFontStyle.Bold);
        private readonly XBrush _textBrush = XBrushes.Black;
        private readonly XPen _linePen = new(XColors.LightGray, 0.5);

        public PdfCanvas(PdfDocument document)
        {
            _document = document;
            _page = document.AddPage();
            _page.Size = PdfSharpCore.PageSize.A4;
            _graphics = XGraphics.FromPdfPage(_page);
        }

        public void Title(string title) => _document.Info.Title = title;

        public void Text(string value, double size, bool bold = false)
        {
            EnsureSpace(size + 8);
            var font = new XFont("Microsoft YaHei", size, bold ? XFontStyle.Bold : XFontStyle.Regular);
            _graphics.DrawString(value, font, _textBrush, new XRect(36, _y, _page.Width - 72, size + 6), XStringFormats.TopLeft);
            _y += size + 7;
        }

        public void Section(string title)
        {
            Space(10);
            Text(title, 12, true);
            _graphics.DrawLine(_linePen, 36, _y, _page.Width - 36, _y);
            _y += 6;
        }

        public void TableHeader(params string[] cells) => Row(cells, true);

        public void Row(params string[] cells) => Row(cells, false);

        private void Row(string[] cells, bool header)
        {
            const double rowHeight = 20;
            EnsureSpace(rowHeight + 4);
            var widths = GetColumnWidths(cells.Length);
            var x = 36.0;
            var font = header ? _boldFont : _bodyFont;
            for (var i = 0; i < cells.Length; i++)
            {
                _graphics.DrawRectangle(header ? XBrushes.Gainsboro : XBrushes.White, x, _y, widths[i], rowHeight);
                _graphics.DrawRectangle(_linePen, x, _y, widths[i], rowHeight);
                _graphics.DrawString(
                    Trim(cells[i], widths[i]), font, _textBrush,
                    new XRect(x + 4, _y + 4, widths[i] - 8, rowHeight - 4),
                    XStringFormats.TopLeft);
                x += widths[i];
            }
            _y += rowHeight;
        }

        public void Space(double amount) => _y += amount;

        public void Chart(PlotModel? model, double width, double height)
        {
            EnsureSpace(height + 20);
            if (model == null)
            {
                Text(Strings.Msg_NoChartData, 9);
                return;
            }

            using var png = new MemoryStream();
            PngExporter.Export(model, png, 1200, 520, 96);
            var bytes = png.ToArray();
            using var image = XImage.FromStream(() => new MemoryStream(bytes));
            _graphics.DrawImage(image, 36, _y, width, height);
            _y += height + 8;
        }

        public void NewPage()
        {
            _graphics.Dispose();
            _page = _document.AddPage();
            _page.Size = PdfSharpCore.PageSize.A4;
            _graphics = XGraphics.FromPdfPage(_page);
            _y = 36;
        }

        private void EnsureSpace(double required)
        {
            if (_y + required <= _page.Height - 36) return;
            NewPage();
        }

        private double[] GetColumnWidths(int count)
        {
            var width = (_page.Width - 72) / Math.Max(1, count);
            return Enumerable.Repeat(width, count).ToArray();
        }

        private static string Trim(string value, double width)
        {
            var max = Math.Max(8, (int)(width / 6));
            return value.Length <= max ? value : value[..Math.Max(0, max - 1)] + "…";
        }

        public void Dispose() => _graphics.Dispose();
    }
}
