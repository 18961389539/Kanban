using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MainAPP.Services;

/// <summary>
/// Reads the busiest Windows GPU Engine counter. GPU Engine values are per engine,
/// so the maximum is used instead of summing engines and exceeding 100 percent.
/// </summary>
public sealed class GpuUsageMonitor : IDisposable
{
    private readonly object _syncRoot = new();
    private readonly ILogger<GpuUsageMonitor> _logger;
    private List<PerformanceCounter> _counters = [];
    private bool _initialized;
    private bool _failureLogged;

    public bool IsAvailable { get; private set; }
    public double UsagePercent { get; private set; }

    public GpuUsageMonitor(ILogger<GpuUsageMonitor>? logger = null)
    {
        _logger = logger ?? NullLogger<GpuUsageMonitor>.Instance;
    }

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
            catch (Exception ex)
            {
                // 无 GPU/驱动缺失时每次采样都会失败并重建计数器：只记录一次 Warning 后降级，
                // 避免每秒刷屏（排障时日志有据可查）
                if (!_failureLogged)
                {
                    _failureLogged = true;
                    _logger.LogWarning(ex, "GPU 计数器采样失败，GPU 使用率降级为不可用（无 GPU 或驱动缺失）");
                }
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
