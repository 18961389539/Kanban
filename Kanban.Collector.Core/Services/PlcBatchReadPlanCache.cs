using System.Diagnostics;

namespace MainAPP.Services;

internal sealed record PlcBatchReadPlanGroup(
    IDeviceAdapter Adapter,
    IReadOnlyList<PlcReadBlock> Blocks,
    IReadOnlySet<string> Addresses);

internal sealed class PlcBatchReadPlanCache
{
    private readonly object _sync = new();
    private string? _signature;
    private IReadOnlyList<PlcBatchReadPlanGroup> _plan = [];
    private int _rebuildCount;
    private long _lastBuildMilliseconds;

    public int RebuildCount
    {
        get { lock (_sync) return _rebuildCount; }
    }

    public long LastBuildMilliseconds
    {
        get { lock (_sync) return _lastBuildMilliseconds; }
    }

    public IReadOnlyList<PlcBatchReadPlanGroup> GetOrBuild(
        string signature,
        Func<IReadOnlyList<PlcBatchReadPlanGroup>> factory)
    {
        lock (_sync)
        {
            if (string.Equals(_signature, signature, StringComparison.Ordinal))
                return _plan;

            var stopwatch = Stopwatch.StartNew();
            _plan = factory();
            _signature = signature;
            _rebuildCount++;
            _lastBuildMilliseconds = stopwatch.ElapsedMilliseconds;
            return _plan;
        }
    }

    public void Invalidate()
    {
        lock (_sync)
        {
            _signature = null;
            _plan = [];
        }
    }
}
