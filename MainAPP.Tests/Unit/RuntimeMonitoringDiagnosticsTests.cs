using System.IO;
using System.Collections.ObjectModel;
using Kanban.Core.Models;
using MainAPP.Models;
using Kanban.Core.Services;
using MainAPP.Services;
using MainAPP.ViewModels;
using Xunit;

namespace MainAPP.Tests.Unit;

[CollectionDefinition("RuntimeMonitoringDiagnostics", DisableParallelization = true)]
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public sealed class RuntimeMonitoringDiagnosticsCollectionDefinition { }

[Collection("RuntimeMonitoringDiagnostics")]
public sealed class RuntimeMonitoringDiagnosticsTests
{
    [Theory]
    [InlineData(false, true, true, 0, "通信中断")]
    [InlineData(true, false, true, 0, "采集已停止")]
    [InlineData(true, true, false, 3, "连续失败 3 次")]
    [InlineData(true, true, true, 3, "运行正常")]
    public void RuntimeHealthText_UsesCurrentCycleState(
        bool connected, bool running, bool lastCycleSucceeded, int consecutiveFailures, string expected)
    {
        Assert.Equal(expected, RuntimeHealthText.Format(connected, running, lastCycleSucceeded, consecutiveFailures));
    }

    [Fact]
    public void DeviceStatusWord_UsesExplicitSemanticValues()
    {
        Assert.Equal(0, (int)DeviceStatusWord.Offline);
        Assert.Equal(1, (int)DeviceStatusWord.Running);
        Assert.Equal(2, (int)DeviceStatusWord.Alarm);
        Assert.Equal(3, (int)DeviceStatusWord.Standby);
    }

    [Theory]
    [InlineData(0, "离线/未知")]
    [InlineData(1, "运行")]
    [InlineData(2, "报警")]
    [InlineData(3, "待机")]
    [InlineData(99, "状态 99")]
    public void RuntimeDeviceStatusText_HandlesKnownAndUnknownValues(int value, string expected)
    {
        Assert.Equal(expected, RuntimeDeviceStatusText.Format(value));
    }

    [Fact]
    public void DeviceAcquisitionStatusItem_FormatsReadAndProductionSummaries()
    {
        var item = new DeviceAcquisitionStatusItem
        {
            ConfiguredAddressCount = 3,
            OkProduction = 1200,
            NgProduction = 12,
        };

        Assert.Equal("3 个地址", item.ReadSummary);
        Assert.Equal("1,200 / 12", item.ProductionSummary);
    }

    [Fact]
    public void DeviceAcquisitionStatusItem_NotifiesDerivedSummaries()
    {
        var item = new DeviceAcquisitionStatusItem();
        var changed = new List<string>();
        item.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is not null)
                changed.Add(args.PropertyName);
        };

        item.ConfiguredAddressCount = 2;
        item.OkProduction = 10;
        item.NgProduction = 1;

        Assert.Contains(nameof(DeviceAcquisitionStatusItem.ReadSummary), changed);
        Assert.Equal(2, changed.Count(name => name == nameof(DeviceAcquisitionStatusItem.ProductionSummary)));
    }

    [Fact]
    public void HistoryStorageDiagnostics_ReadsDatabaseAndWalSizes()
    {
        var directoryName = "kanban_storage_diag_" + Guid.NewGuid().ToString("N");
        var tempDirectory = Path.Combine(AppSettings.DataRoot, directoryName);
        Directory.CreateDirectory(tempDirectory);
        try
        {
            var settings = new AppSettings { ConfigDirectory = directoryName };
            var databasePath = settings.GetFilePath("production_logs.db");
            Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
            File.WriteAllBytes(databasePath, new byte[11]);
            File.WriteAllBytes(databasePath + "-wal", new byte[7]);

            using var diagnostics = new HistoryStorageDiagnostics(settings);
            var snapshot = diagnostics.GetSnapshot();

            Assert.Equal(11, snapshot.ProductionDatabaseBytes);
            Assert.Equal(7, snapshot.ProductionWalBytes);
        }
        finally
        {
            try { Directory.Delete(tempDirectory, true); } catch { }
        }
    }

    [Fact]
    public void HistoryStorageDiagnostics_MissingFiles_ReturnsZero()
    {
        var settings = new AppSettings
        {
            ConfigDirectory = "kanban_missing_storage_" + Guid.NewGuid().ToString("N")
        };

        using var diagnostics = new HistoryStorageDiagnostics(settings);
        var snapshot = diagnostics.GetSnapshot();

        Assert.Equal(0, snapshot.ProductionDatabaseBytes);
        Assert.Equal(0, snapshot.ProductionWalBytes);
    }

    [Fact]
    public void SystemResourceMonitor_SampleReturnsBoundedSnapshot()
    {
        using var monitor = new SystemResourceMonitor(new GpuUsageMonitor());

        var snapshot = monitor.Sample();

        Assert.InRange(snapshot.CpuUsagePercent, 0, 100);
        Assert.True(snapshot.ProcessMemoryMb >= 0);
        Assert.True(snapshot.AvailableMemoryMb >= 0);
        Assert.True(snapshot.ProcessUptime >= TimeSpan.Zero);
        Assert.True(snapshot.ThreadCount >= 0);
        Assert.True(snapshot.HandleCount >= 0);
        Assert.True(snapshot.FreeDiskGb >= 0);
        Assert.InRange(snapshot.GpuUsagePercent, 0, 100);
    }

    [Fact]
    public void PollingTrendBuffer_CapsPointsAtSixty()
    {
        var buffer = new PollingTrendBuffer();

        for (var index = 0; index < 75; index++)
            buffer.Add(DateTime.Today.AddSeconds(index), index);

        Assert.Equal(60, buffer.Count);
        var points = buffer.Add(DateTime.Today.AddSeconds(75), 75);
        Assert.Equal(60, points.Count);
        Assert.Equal(16, points[0].Y);
        Assert.Equal(75, points[^1].Y);
    }

    [Fact]
    public void DeviceStatusCollectionSynchronizer_ReusesRowsAndRemovesStaleRows()
    {
        var target = new ObservableCollection<DeviceAcquisitionStatusItem>
        {
            new() { DeviceName = "设备1", StatusText = "运行", ConfiguredAddressCount = 1 },
            new() { DeviceName = "设备2", StatusText = "报警" },
        };
        var original = target[0];
        var desired = new List<DeviceAcquisitionStatusItem>
        {
            new() { DeviceName = "设备1", StatusText = "待机", ConfiguredAddressCount = 4, OkProduction = 9 },
            new() { DeviceName = "设备3", StatusText = "离线/未知" },
        };

        DeviceStatusCollectionSynchronizer.Synchronize(target, desired);

        Assert.Same(original, Assert.Single(target, item => item.DeviceName == "设备1"));
        Assert.Equal("待机", original.StatusText);
        Assert.Equal(4, original.ConfiguredAddressCount);
        Assert.Equal(9, original.OkProduction);
        Assert.DoesNotContain(target, item => item.DeviceName == "设备2");
        Assert.Contains(target, item => item.DeviceName == "设备3");
    }
}
