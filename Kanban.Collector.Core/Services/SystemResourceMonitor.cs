using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kanban.Collector.Core.Services;

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

/// <summary>当前进程的 CPU / 内存 / 磁盘采样。本地模式采 MainAPP，Remote 模式由 Collector 进程采样后随诊断快照下发。</summary>
[SupportedOSPlatform("windows")]
public sealed class SystemResourceMonitor : IDisposable
{
    private readonly ILogger<SystemResourceMonitor> _logger;
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly GpuUsageMonitor _gpuUsageMonitor;
    private TimeSpan _lastCpuTime;
    private DateTime _lastSampleAt = DateTime.Now;
    // 可用内存计数器缓存：每次 Sample 新建/Dispose 是高频重复分配（采样周期 1s），
    // 缓存后仅在失败时重建（照 GpuUsageMonitor.EnsureCounters 模式）
    private PerformanceCounter? _availableMemoryCounter;
    private bool _memoryCounterFailureLogged;

    public SystemResourceMonitor(GpuUsageMonitor gpuUsageMonitor, ILogger<SystemResourceMonitor>? logger = null)
    {
        _gpuUsageMonitor = gpuUsageMonitor;
        _logger = logger ?? NullLogger<SystemResourceMonitor>.Instance;
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
        _availableMemoryCounter?.Dispose();
        _gpuUsageMonitor.Dispose();
        _process.Dispose();
    }

    private double TryGetAvailableMemoryMb()
    {
        try
        {
            _availableMemoryCounter ??= new PerformanceCounter("Memory", "Available MBytes");
            return _availableMemoryCounter.NextValue();
        }
        catch (Exception ex)
        {
            // 首败记录一次 Warning（性能计数器不可用），避免每秒刷屏；降级返回 0
            if (!_memoryCounterFailureLogged)
            {
                _memoryCounterFailureLogged = true;
                _logger.LogWarning(ex, "可用内存性能计数器不可用，可用内存显示为 0");
            }
            return 0;
        }
    }

    private int TryGetThreadCount()
    {
        try { return _process.Threads.Count; }
        catch (Exception ex) { _logger.LogDebug(ex, "读取进程线程数失败"); return 0; }
    }

    private long TryGetHandleCount()
    {
        try { return _process.HandleCount; }
        catch (Exception ex) { _logger.LogDebug(ex, "读取进程句柄数失败"); return 0; }
    }

    private double TryGetFreeDiskGb()
    {
        try
        {
            var root = Path.GetPathRoot(AppContext.BaseDirectory);
            return string.IsNullOrWhiteSpace(root) ? 0 : new DriveInfo(root).AvailableFreeSpace / 1024d / 1024d / 1024d;
        }
        catch (Exception ex) { _logger.LogDebug(ex, "读取磁盘剩余空间失败"); return 0; }
    }
}
