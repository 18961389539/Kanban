using System.Diagnostics;
using Kanban.Core.Services;
using Kanban.Core.Models;
using Kanban.Core.Data;
using Kanban.Core.Entities;
using System.IO;

namespace MainAPP.Services;

public sealed record SystemResourceSnapshot(
    double CpuUsagePercent,
    double GpuUsagePercent,
    bool GpuAvailable,
    double ProcessMemoryMb,
    double AvailableMemoryMb,
    TimeSpan ProcessUptime,
    int ThreadCount,
    long HandleCount,
    double FreeDiskGb);

public sealed class SystemResourceMonitor : IDisposable
{
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly GpuUsageMonitor _gpuUsageMonitor;
    private TimeSpan _lastCpuTime;
    private DateTime _lastSampleAt = DateTime.Now;

    public SystemResourceMonitor(GpuUsageMonitor gpuUsageMonitor)
    {
        _gpuUsageMonitor = gpuUsageMonitor;
        _lastCpuTime = _process.TotalProcessorTime;
    }

    public SystemResourceSnapshot Sample()
    {
        _process.Refresh();
        var now = DateTime.Now;
        var elapsed = (now - _lastSampleAt).TotalSeconds;
        var currentCpuTime = _process.TotalProcessorTime;
        var cpuPercent = elapsed > 0
            ? Math.Clamp((currentCpuTime - _lastCpuTime).TotalSeconds / elapsed / Environment.ProcessorCount * 100, 0, 100)
            : 0;
        _lastCpuTime = currentCpuTime;
        _lastSampleAt = now;

        _gpuUsageMonitor.Sample();
        return new SystemResourceSnapshot(
            cpuPercent,
            _gpuUsageMonitor.UsagePercent,
            _gpuUsageMonitor.IsAvailable,
            _process.WorkingSet64 / 1024d / 1024d,
            TryGetAvailableMemoryMb(),
            now - _process.StartTime,
            TryGetThreadCount(),
            TryGetHandleCount(),
            TryGetFreeDiskGb());
    }

    public void Dispose()
    {
        _gpuUsageMonitor.Dispose();
        _process.Dispose();
    }

    private static double TryGetAvailableMemoryMb()
    {
        try
        {
            using var counter = new PerformanceCounter("Memory", "Available MBytes");
            return counter.NextValue();
        }
        catch { return 0; }
    }

    private int TryGetThreadCount()
    {
        try { return _process.Threads.Count; }
        catch { return 0; }
    }

    private long TryGetHandleCount()
    {
        try { return _process.HandleCount; }
        catch { return 0; }
    }

    private static double TryGetFreeDiskGb()
    {
        try
        {
            var root = Path.GetPathRoot(AppContext.BaseDirectory);
            return string.IsNullOrWhiteSpace(root) ? 0 : new DriveInfo(root).AvailableFreeSpace / 1024d / 1024d / 1024d;
        }
        catch { return 0; }
    }
}
