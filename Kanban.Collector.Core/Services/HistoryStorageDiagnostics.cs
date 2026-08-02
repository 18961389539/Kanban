using System.IO;

namespace Kanban.Core.Services;

public sealed record HistoryStorageSnapshot(long ProductionDatabaseBytes, long ProductionWalBytes);

public sealed class HistoryStorageDiagnostics : IDisposable
{
    private readonly string _productionDatabasePath;
    private readonly object _syncRoot = new();
    private readonly Timer _timer;
    private HistoryStorageSnapshot _snapshot = new(0, 0);

    public HistoryStorageDiagnostics(AppSettings appSettings)
    {
        _productionDatabasePath = appSettings.GetFilePath("production_logs.db");
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
            var snapshot = new HistoryStorageSnapshot(
                GetFileLength(_productionDatabasePath),
                GetFileLength(_productionDatabasePath + "-wal"));
            lock (_syncRoot) _snapshot = snapshot;
        }
        catch { }
    }

    private static long GetFileLength(string path)
    {
        return File.Exists(path) ? new FileInfo(path).Length : 0;
    }
}
