using System.IO;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using Kanban.Contracts.Metrics;
using Xunit;

namespace MainAPP.Tests.Unit;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class DefectParetoInputBuilderTests
{
    [Fact]
    public void BuildHomeInputs_UsesShiftWindowIncrement_NotLiveCumulative()
    {
        var configDirectory = "kanban-pareto-input-" + Guid.NewGuid().ToString("N");
        var settings = new AppSettings { ConfigDirectory = configDirectory };
        var databaseProvider = new DatabaseProvider(settings);
        try
        {
            using var context = databaseProvider.CreateDefectHistoryContext();
            context.Database.EnsureCreated();
            using var store = new DefectHistoryStore(databaseProvider);

            var shift = new ShiftConfig { Name = "白班", StartTime = TimeSpan.FromHours(8), EndTime = TimeSpan.FromHours(20) };
            settings.Shifts.Add(shift);
            var from = new DateTime(2026, 8, 8, 8, 0, 0);
            var now = from.AddHours(3);
            var device = new Device { Id = "dev1", Name = "A" };
            device.Defects.Add(new Defect { Id = "def1", Name = "划痕", Count = 50, PlcAddress = "D1" });

            store.Append(
            [
                CreateSnapshot(from.AddHours(-1), 40, "def1", "划痕", "白班", device),
                CreateSnapshot(from.AddHours(1), 45, "def1", "划痕", "白班", device),
                CreateSnapshot(now, 48, "def1", "划痕", "白班", device),
            ]);
            store.Flush();

            var inputs = DefectParetoInputBuilder.BuildHomeInputs(device, store, settings.GetShiftsSnapshot(), now);

            Assert.Single(inputs);
            Assert.Equal(8, inputs[0].Count);
            var result = DefectParetoMetrics.Build(inputs, ngCount: 10);
            Assert.Equal(DefectParetoEmptyKind.HasData, result.EmptyKind);
            Assert.Equal(8, result.TotalCount);
        }
        finally
        {
            var root = Path.Combine(AppSettings.DataRoot, configDirectory);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch (IOException) { }
        }
    }

    private static DefectSnapshotRecord CreateSnapshot(
        DateTime timestamp, int count, string defectId, string defectName, string shiftName, Device device)
        => new()
        {
            DeviceId = device.Id,
            DeviceName = device.Name,
            DefectId = defectId,
            DefectName = defectName,
            ShiftName = shiftName,
            Count = count,
            Timestamp = timestamp,
        };
}
