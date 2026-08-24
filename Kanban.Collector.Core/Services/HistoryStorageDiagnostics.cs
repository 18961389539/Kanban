using System.IO;

namespace Kanban.Collector.Core.Services;

public sealed record HistoryStorageSnapshot
{
    public long ProductionDatabaseBytes { get; init; }
    public long ProductionWalBytes { get; init; }
    public long DataSourceDatabaseBytes { get; init; }
    public long DataSourceWalBytes { get; init; }
    public long TotalDatabaseBytes { get; init; }
    public long TotalWalBytes { get; init; }
}

public sealed class HistoryStorageDiagnostics : IDisposable
{
    private readonly string _productionDatabasePath;
    private readonly string _dataSourceDatabasePath;
    private readonly string[] _historyDatabasePaths;
    private readonly object _syncRoot = new();
    private readonly Timer _timer;
    private HistoryStorageSnapshot _snapshot = new();

    public HistoryStorageDiagnostics(AppSettings appSettings)
    {
        _productionDatabasePath = appSettings.GetFilePath("production_logs.db");
        _dataSourceDatabasePath = appSettings.GetFilePath("datasource_snapshots.db");
        _historyDatabasePaths =
        [
            "production_logs.db",
            "alarm_events.db",
            "status_transitions.db",
            "work_orders.db",
            "defect_history.db",
            "audit_logs.db",
            "datasource_snapshots.db",
        ];
        _historyDatabasePaths = _historyDatabasePaths
            .Select(appSettings.GetFilePath)
            .ToArray();
        Refresh();
        _timer = new Timer(_ => Refresh(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    public HistoryStorageSnapshot GetSnapshot()
    {
        lock (_syncRoot)
        {
            return _snapshot;
        }
    }

    public void Dispose() => _timer.Dispose();

    private void Refresh()
    {
        try
        {
            var productionDatabaseBytes = GetFileLength(_productionDatabasePath);
            var productionWalBytes = GetFileLength(_productionDatabasePath + "-wal");
            var dataSourceDatabaseBytes = GetFileLength(_dataSourceDatabasePath);
            var dataSourceWalBytes = GetFileLength(_dataSourceDatabasePath + "-wal");
            var totalDatabaseBytes = _historyDatabasePaths.Sum(GetFileLength);
            var totalWalBytes = _historyDatabasePaths.Sum(path => GetFileLength(path + "-wal"));
            var snapshot = new HistoryStorageSnapshot
            {
                ProductionDatabaseBytes = productionDatabaseBytes,
                ProductionWalBytes = productionWalBytes,
                DataSourceDatabaseBytes = dataSourceDatabaseBytes,
                DataSourceWalBytes = dataSourceWalBytes,
                TotalDatabaseBytes = totalDatabaseBytes,
                TotalWalBytes = totalWalBytes,
            };
            lock (_syncRoot) _snapshot = snapshot;
        }
        catch { }
    }

    private static long GetFileLength(string path)
    {
        return File.Exists(path) ? new FileInfo(path).Length : 0;
    }
}
