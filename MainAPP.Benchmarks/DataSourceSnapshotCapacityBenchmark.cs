using BenchmarkDotNet.Attributes;
using System.IO;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MainAPP.Benchmarks;

[MemoryDiagnoser]
[RankColumn]
public class DataSourceSnapshotCapacityBenchmark
{
    private const int SourcesPerDevice = 3;
    private const int ValuesPerSource = 4;

    private readonly List<DataSourceSnapshotRecord> _records = [];
    private string _benchmarkRoot = string.Empty;
    private DataSourceSnapshotStore? _store;
    private DatabaseProvider? _databaseProvider;
    private string _databasePath = string.Empty;
    private string _operation = string.Empty;

    private string DiagnosticsReportPath => Path.Combine(
        Path.GetTempPath(),
        "KanbanDataSourceCapacityBenchmark",
        $"diagnostics-{Environment.ProcessId}.jsonl");

    [Params(20, 50, 100, 200)]
    public int DeviceCount { get; set; }

    public int RecordsPerBatch => DeviceCount * SourcesPerDevice * ValuesPerSource;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _benchmarkRoot = "DataSourceCapacityBenchmark_" + Guid.NewGuid().ToString("N");
        if (File.Exists(DiagnosticsReportPath))
            File.Delete(DiagnosticsReportPath);

        for (var deviceIndex = 0; deviceIndex < DeviceCount; deviceIndex++)
        {
            for (var sourceIndex = 0; sourceIndex < SourcesPerDevice; sourceIndex++)
            {
                for (var valueIndex = 0; valueIndex < ValuesPerSource; valueIndex++)
                {
                    _records.Add(new DataSourceSnapshotRecord
                    {
                        DeviceId = $"device-{deviceIndex:D3}",
                        DeviceName = $"Device {deviceIndex:D3}",
                        SourceId = $"source-{sourceIndex:D2}",
                        ValueId = $"value-{valueIndex:D2}",
                        SourceName = $"Source {sourceIndex:D2}",
                        SourceType = "analog",
                        Unit = "unit",
                        Value = deviceIndex + sourceIndex + valueIndex,
                        DataType = 0,
                        IsValid = true,
                        ShiftName = "shift",
                        Timestamp = DateTime.UtcNow,
                        PersistedAt = DateTime.UtcNow,
                    });
                }
            }
        }
    }

    [IterationSetup]
    public void IterationSetup()
    {
        var directory = Path.Combine(_benchmarkRoot, Guid.NewGuid().ToString("N"));
        var settings = new AppSettings { ConfigDirectory = directory };
        _databaseProvider = new DatabaseProvider(settings);
        _databasePath = settings.GetFilePath("datasource_snapshots.db");
        using (var context = _databaseProvider.CreateDataSourceSnapshotContext())
            context.Database.EnsureCreated();
        _databaseProvider.EnsureWalModeEnabled();
        _store = new DataSourceSnapshotStore(_databaseProvider);
    }

    [Benchmark(Baseline = true)]
    public int AppendEnqueue()
    {
        _operation = nameof(AppendEnqueue);
        _store!.Append(_records);
        return RecordsPerBatch;
    }

    [Benchmark]
    public int AppendAndFlush()
    {
        _operation = nameof(AppendAndFlush);
        _store!.Append(_records);
        _store.FlushAsync().GetAwaiter().GetResult();
        return RecordsPerBatch;
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        if (_store is not null)
        {
            WriteDiagnosticsReport(_store.GetDiagnosticsSnapshot(), "after-benchmark");
            _store.FlushAsync().GetAwaiter().GetResult();
            WriteDiagnosticsReport(_store.GetDiagnosticsSnapshot(), "after-cleanup");
            _store.Dispose();
            _store = null;
        }

        _databaseProvider = null;
        _databasePath = string.Empty;
        _operation = string.Empty;
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        SqliteConnection.ClearAllPools();
        var root = Path.Combine(AppSettings.DataRoot, _benchmarkRoot);
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch
        {
        }
    }

    private void WriteDiagnosticsReport(DataSourceSnapshotWriterDiagnosticsSnapshot diagnostics, string phase)
    {
        var directory = Path.GetDirectoryName(DiagnosticsReportPath)!;
        Directory.CreateDirectory(directory);
        var report = new
        {
            DeviceCount,
            RecordsPerBatch,
            Operation = _operation,
            Phase = phase,
            diagnostics.PendingCount,
            diagnostics.QueuePeakCount,
            diagnostics.OverflowCount,
            diagnostics.RecoveryFileBytes,
            diagnostics.RecoveryFileLines,
            diagnostics.FlushFailureCount,
            diagnostics.TotalFlushedCount,
            diagnostics.FlushP95Milliseconds,
            diagnostics.FlushP99Milliseconds,
            DatabaseBytes = GetFileLength(_databasePath),
            WalBytes = GetFileLength(_databasePath + "-wal"),
        };
        File.AppendAllText(DiagnosticsReportPath, System.Text.Json.JsonSerializer.Serialize(report) + Environment.NewLine);
    }

    private static long GetFileLength(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }
}
