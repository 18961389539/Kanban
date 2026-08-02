using System.Text;
using System.IO;
using Kanban.Core.Services;
using MainAPP.Services;
using MainAPP.ViewModels;
using OxyPlot;
using OxyPlot.Series;
using Xunit;

namespace MainAPP.Tests.Unit;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public class ProductionReviewPdfServiceTests
{
    [Fact]
    public void Export_WritesPdfDocument()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kanban_pdf_{Guid.NewGuid():N}.pdf");
        var data = new ProductionReviewPdfData(
            DateTime.Now.AddHours(-8),
            DateTime.Now,
            "白班",
            120,
            3,
            120.0 / 123.0,
            0.82,
            6.5,
            0.8,
            0.7,
            2,
            [new DeviceOverviewSummary
            {
                DeviceName = "测试设备",
                OkCount = 120,
                NgCount = 3,
                QualityRate = 120.0 / 123.0,
                Oee = 0.82,
                AlarmCount = 2,
            }],
            [new ShiftComparisonSummary
            {
                ShiftName = "白班",
                OkCount = 120,
                NgCount = 3,
                AlarmCount = 2,
            }],
            [new AlarmOverviewSummary
            {
                AlarmName = "高温报警",
                DeviceName = "测试设备",
                TriggerCount = 2,
                TotalDurationHours = 0.5,
            }],
            new PlotModel
            {
                Title = "产量趋势",
                Series = { new LineSeries { Points = { new DataPoint(0, 1), new DataPoint(1, 3) } } },
            });

        try
        {
            new ProductionReviewPdfService().Export(path, data);

            Assert.True(File.Exists(path));
            var header = Encoding.ASCII.GetString(File.ReadAllBytes(path), 0, 5);
            var content = Encoding.Latin1.GetString(File.ReadAllBytes(path));
            Assert.Equal("%PDF-", header);
            Assert.Contains("/Subtype /Image", content);
            Assert.True(new FileInfo(path).Length > 500);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
