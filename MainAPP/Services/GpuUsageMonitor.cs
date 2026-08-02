using System.Diagnostics;

namespace MainAPP.Services;

/// <summary>
/// Reads the busiest Windows GPU Engine counter. GPU Engine values are per engine,
/// so the maximum is used instead of summing engines and exceeding 100 percent.
/// </summary>
public sealed class GpuUsageMonitor : IDisposable
{
    private readonly object _syncRoot = new();
    private List<PerformanceCounter> _counters = [];
    private bool _initialized;

    public bool IsAvailable { get; private set; }
    public double UsagePercent { get; private set; }

    public void Sample()
    {
        lock (_syncRoot)
        {
            try
            {
                EnsureCounters();
                if (_counters.Count == 0)
                {
                    UsagePercent = 0;
                    return;
                }

                var maximum = _counters.Max(counter => counter.NextValue());
                UsagePercent = Math.Clamp(maximum, 0, 100);
                IsAvailable = true;
            }
            catch
            {
                UsagePercent = 0;
                IsAvailable = false;
                DisposeCounters();
            }
        }
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            DisposeCounters();
        }
    }

    private void EnsureCounters()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        var category = new PerformanceCounterCategory("GPU Engine");
        _counters = category.GetInstanceNames()
            .Where(instance => instance.Contains("engtype_", StringComparison.OrdinalIgnoreCase))
            .Select(instance => new PerformanceCounter("GPU Engine", "Utilization Percentage", instance, true))
            .ToList();
    }

    private void DisposeCounters()
    {
        foreach (var counter in _counters)
        {
            counter.Dispose();
        }

        _counters.Clear();
        _initialized = false;
    }
}
