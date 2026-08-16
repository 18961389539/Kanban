using System.IO;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Models;
using MainAPP.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MainAPP.Tests.Unit;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public sealed class ProductionDailyReportServiceTests
{
    [Fact]
    public void RunOnce_WhenDisabled_DoesNotGenerate()
    {
        using var fixture = new Fixture();
        fixture.AddDevice();

        fixture.Service.RunOnce(new DateTime(2026, 1, 2, 23, 59, 0));

        Assert.Empty(fixture.Pdf.Exports);
    }

    [Fact]
    public void RunOnce_BeforeConfiguredTime_DoesNotGenerate()
    {
        using var fixture = new Fixture { EnableAutomaticReport = true, AutomaticReportTime = new TimeSpan(23, 0, 0) };
        fixture.AddDevice();

        fixture.Service.RunOnce(new DateTime(2026, 1, 2, 22, 59, 0));

        Assert.Empty(fixture.Pdf.Exports);
    }

    [Fact]
    public void RunOnce_WhenNoHistory_DoesNotGenerate()
    {
        using var fixture = new Fixture { EnableAutomaticReport = true, AutomaticReportTime = TimeSpan.Zero };
        fixture.AddDevice();

        fixture.Service.RunOnce(new DateTime(2026, 1, 2, 0, 1, 0));

        Assert.Empty(fixture.Pdf.Exports);
    }

    [Fact]
    public void RunOnce_WhenReportAlreadyExists_SkipsExport()
    {
        using var fixture = new Fixture { EnableAutomaticReport = true, AutomaticReportTime = TimeSpan.Zero };
        fixture.AddDevice("device-a", "Device A");
        var reportDate = new DateTime(2026, 1, 1);
        var path = fixture.ReportPath(reportDate, "Device A");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3]);

        fixture.Service.RunOnce(reportDate.AddDays(1).AddMinutes(1));

        Assert.Empty(fixture.Pdf.Exports);
        Assert.Equal(3, new FileInfo(path).Length);
    }

    [Fact]
    public void RunOnce_WithSingleDeviceHistory_ExportsPreviousDayReport()
    {
        using var fixture = new Fixture { EnableAutomaticReport = true, AutomaticReportTime = TimeSpan.Zero };
        fixture.AddDevice("device-a", "Device A", targetCycle: 60);
        var reportDate = new DateTime(2026, 1, 1);
        fixture.History.ProductionLogs.Add(new ProductionLog
        {
            DeviceId = "device-a",
            DeviceName = "Device A",
            ShiftName = "day",
            OkProduction = 120,
            NgProduction = 3,
            Timestamp = reportDate.AddHours(8),
        });

        fixture.Service.RunOnce(reportDate.AddDays(1).AddMinutes(1));

        var export = Assert.Single(fixture.Pdf.Exports);
        Assert.EndsWith("20260101_Device A.pdf", export.Path, StringComparison.Ordinal);
        Assert.Equal(120, export.Data.TotalOk);
        Assert.Equal(3, export.Data.TotalNg);
        Assert.Equal(1440, export.Data.TargetOutput);
    }

    [Fact]
    public async Task StopAsync_CancelsWorker()
    {
        using var fixture = new Fixture();
        fixture.Service.Start();

        await fixture.Service.StopAsync();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _configDirectory = "KanbanDailyReportTests_" + Guid.NewGuid().ToString("N");
        private readonly AppSettings _settings;
        private readonly DatabaseProvider _databaseProvider;

        public Fixture()
        {
            _settings = new AppSettings { ConfigDirectory = _configDirectory };
            _databaseProvider = new DatabaseProvider(_settings);
            using var context = _databaseProvider.CreateDefectHistoryContext();
            context.Database.EnsureCreated();

            Devices = new DeviceRepository(_settings);
            History = new InMemoryHistoryService();
            Defects = new DefectHistoryStore(_databaseProvider);
            Pdf = new RecordingPdfService();
            Service = new ProductionDailyReportService(_settings, Devices, History, Defects, Pdf);
        }

        public bool EnableAutomaticReport
        {
            set => _settings.EnableAutomaticDailyReport = value;
        }

        public TimeSpan AutomaticReportTime
        {
            set => _settings.AutomaticDailyReportTime = value;
        }

        public DeviceRepository Devices { get; }
        public InMemoryHistoryService History { get; }
        public DefectHistoryStore Defects { get; }
        public RecordingPdfService Pdf { get; }
        public ProductionDailyReportService Service { get; }

        public void AddDevice(string id = "device-1", string name = "Device 1", int targetCycle = 100)
        {
            Devices.Devices.Add(new Device
            {
                Id = id,
                Name = name,
                TargetCycle = targetCycle,
            });
        }

        public string ReportPath(DateTime reportDate, string deviceName)
            => Path.Combine(
                AppSettings.DataRoot,
                _configDirectory,
                "Reports",
                $"生产日报_{reportDate:yyyyMMdd}_{deviceName}.pdf");

        public void Dispose()
        {
            Service.Dispose();
            SqliteConnection.ClearAllPools();
            var root = Path.Combine(AppSettings.DataRoot, _configDirectory);
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class RecordingPdfService : IProductionReviewPdfService
    {
        public List<(string Path, ProductionReviewPdfData Data)> Exports { get; } = [];

        public void Export(string path, ProductionReviewPdfData data)
            => Exports.Add((path, data));
    }
}
