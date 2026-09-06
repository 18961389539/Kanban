using System.IO;
using System.Collections.ObjectModel;
using Kanban.Collector.Core.Models;
using MainAPP.Models;
using Kanban.Collector.Core.Services;
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
    [InlineData(0, "离线")]
    [InlineData(1, "运行")]
    [InlineData(2, "报警")]
    [InlineData(3, "待机")]
    [InlineData(99, "未知")]
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
            var dataSourceDatabasePath = settings.GetFilePath("datasource_snapshots.db");
            File.WriteAllBytes(dataSourceDatabasePath, new byte[13]);
            File.WriteAllBytes(dataSourceDatabasePath + "-wal", new byte[5]);

            using var diagnostics = new HistoryStorageDiagnostics(settings);
            var snapshot = diagnostics.GetSnapshot();

            Assert.Equal(11, snapshot.ProductionDatabaseBytes);
            Assert.Equal(7, snapshot.ProductionWalBytes);
            Assert.Equal(13, snapshot.DataSourceDatabaseBytes);
            Assert.Equal(5, snapshot.DataSourceWalBytes);
            Assert.Equal(24, snapshot.TotalDatabaseBytes);
            Assert.Equal(12, snapshot.TotalWalBytes);
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
            new() { DeviceName = "设备3", StatusText = "离线" },
        };

        DeviceStatusCollectionSynchronizer.Synchronize(target, desired);

        Assert.Same(original, Assert.Single(target, item => item.DeviceName == "设备1"));
        Assert.Equal("待机", original.StatusText);
        Assert.Equal(4, original.ConfiguredAddressCount);
        Assert.Equal(9, original.OkProduction);
        Assert.DoesNotContain(target, item => item.DeviceName == "设备2");
        Assert.Contains(target, item => item.DeviceName == "设备3");
    }

    [Fact]
    public void DeviceStatusCollectionSynchronizer_DuplicateDeviceNames_DoesNotThrow()
    {
        // P0 回归：原实现 target.ToDictionary(item => item.DeviceName)，设备重名时抛
        // ArgumentException；同步发生在 1s 刷新定时器回调内，异常直通 Dispatcher 会终止进程。
        var target = new ObservableCollection<DeviceAcquisitionStatusItem>
        {
            new() { DeviceId = "d1", DeviceName = "重名设备" },
            new() { DeviceId = "d2", DeviceName = "重名设备" },
        };
        var desired = new List<DeviceAcquisitionStatusItem>
        {
            new() { DeviceId = "d1", DeviceName = "重名设备", StatusText = "运行" },
            new() { DeviceId = "d2", DeviceName = "重名设备", StatusText = "报警" },
        };

        DeviceStatusCollectionSynchronizer.Synchronize(target, desired);

        Assert.Equal(2, target.Count);
        Assert.Contains(target, item => item.DeviceId == "d1" && item.StatusText == "运行");
        Assert.Contains(target, item => item.DeviceId == "d2" && item.StatusText == "报警");
    }

    [Fact]
    public void DeviceStatusCollectionSynchronizer_MissingDeviceId_FallsBackToNameAndDeduplicates()
    {
        // 旧版 Collector 未下发 DeviceId 时回退到设备名做键：重名必须去重而不是抛异常/留重复行。
        var target = new ObservableCollection<DeviceAcquisitionStatusItem>
        {
            new() { DeviceName = "重名设备" },
            new() { DeviceName = "重名设备" },
        };
        var desired = new List<DeviceAcquisitionStatusItem>
        {
            new() { DeviceName = "重名设备", StatusText = "运行" },
        };

        DeviceStatusCollectionSynchronizer.Synchronize(target, desired);

        var only = Assert.Single(target);
        Assert.Equal("运行", only.StatusText);
    }

    [Fact]
    public void DeviceStatusCollectionSynchronizer_SyncsRenamedDevice()
    {
        // 设备改名后按 Id 命中旧行并更新名称，而不是新增一行、留下同 Id 的两条记录。
        var target = new ObservableCollection<DeviceAcquisitionStatusItem>
        {
            new() { DeviceId = "d1", DeviceName = "旧名称" },
        };
        var desired = new List<DeviceAcquisitionStatusItem>
        {
            new() { DeviceId = "d1", DeviceName = "新名称", StatusText = "运行" },
        };

        DeviceStatusCollectionSynchronizer.Synchronize(target, desired);

        var only = Assert.Single(target);
        Assert.Equal("新名称", only.DeviceName);
        Assert.Equal("运行", only.StatusText);
    }
}
