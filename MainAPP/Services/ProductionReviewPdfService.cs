using Kanban.Core.Services;
using Kanban.Core.Models;
using Kanban.Core.Data;
using Kanban.Core.Entities;
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
        canvas.Title("生产复盘报表");
        canvas.Text("生产复盘报表", 18, true);
        canvas.Text($"统计范围：{data.From:yyyy-MM-dd HH:mm:ss} ~ {data.To:yyyy-MM-dd HH:mm:ss}", 9);
        canvas.Text($"当前班次：{data.ShiftName}", 9);
        canvas.Space(8);

        canvas.Section("核心指标");
        canvas.TableHeader("指标", "数值");
        canvas.Row("总合格产量", data.TotalOk.ToString("N0", CultureInfo.InvariantCulture));
        canvas.Row("总不良产量", data.TotalNg.ToString("N0", CultureInfo.InvariantCulture));
        canvas.Row("良品率", data.QualityRate.ToString("P1", CultureInfo.InvariantCulture));
        canvas.Row("OEE", data.Oee.ToString("P1", CultureInfo.InvariantCulture));
        canvas.Row("运行时长", $"{data.RunTimeHours:F2}h");
        canvas.Row("待机时长", $"{data.PausedTimeHours:F2}h");
        canvas.Row("报警时长", $"{data.AlarmDurationHours:F2}h");
        canvas.Row("报警次数", data.AlarmCount.ToString(CultureInfo.InvariantCulture));
        canvas.Row("目标产量", data.TargetOutput.ToString("N0", CultureInfo.InvariantCulture));
        canvas.Row("目标达成率", data.OutputAchievementRate.ToString("P1", CultureInfo.InvariantCulture));

        canvas.Section("产量趋势");
        canvas.Chart(data.TrendChart, 520, 220);

        canvas.Section("设备明细");
        canvas.TableHeader("设备", "OK", "NG", "良品率", "OEE", "报警次数");
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
        canvas.Text("生产复盘报表", 16, true);
        canvas.Section("OEE 损失拆解");
        canvas.Chart(data.OeeWaterfallChart, 520, 210);
        canvas.Row("可用率", data.AvailabilityLossText);
        canvas.Row("性能率", data.PerformanceLossText);
        canvas.Row("良品率", data.QualityLossText);
        canvas.Section("周期对比");
        canvas.Row(data.ComparisonLabel, $"产量 {data.BaselineTotalOutput:N0} · 良品率 {data.BaselineQualityRate:P1} · OEE {data.BaselineOee:P1}");
        canvas.Row("当前变化", $"产量 {FormatSigned(data.OutputDelta)} · 良品率 {FormatSignedPercentage(data.QualityRateDelta)} · OEE {FormatSignedPercentage(data.OeeDelta)}");
        canvas.Section("停机分析");
        canvas.Row("总停机时长", $"{data.TotalDowntimeHours:F2}h");
        canvas.Row("平均报警时长", $"{data.AverageAlarmDurationMinutes:F1}min");
        canvas.Row("MTBF", $"{data.MtbfHours:F2}h");
        canvas.Section("生产热力图");
        canvas.Chart(data.ProductionHeatmapChart, 520, 230);

        canvas.NewPage();
        canvas.Text("生产复盘报表", 16, true);
        canvas.Section("班次对比");
        canvas.TableHeader("班次", "OK", "NG", "总产量", "良品率", "报警次数");
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

        canvas.Section("Top 报警");
        canvas.TableHeader("报警名称", "设备", "触发次数", "累计时长");
        foreach (var alarm in data.TopAlarms)
        {
            canvas.Row(
                alarm.AlarmName,
                alarm.DeviceName,
                alarm.TriggerCount.ToString(CultureInfo.InvariantCulture),
                $"{alarm.TotalDurationHours:F2}h");
        }

            canvas.Section("缺陷帕累托");
            canvas.TableHeader("缺陷", "设备", "数量", "累计占比");
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

    private static string FormatSigned(int value) => value > 0 ? $"+{value:N0}" : value.ToString("N0", CultureInfo.InvariantCulture);
    private static string FormatSignedPercentage(double value) => value > 0 ? $"+{value:P1}" : value.ToString("P1", CultureInfo.InvariantCulture);

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
                throw new FileNotFoundException("未找到 Microsoft YaHei 中文字体，无法生成 PDF", path);
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
                Text("暂无图表数据", 9);
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
