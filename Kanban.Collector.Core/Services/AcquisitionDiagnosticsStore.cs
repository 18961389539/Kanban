using System.Collections.Frozen;
using System.Diagnostics;

namespace Kanban.Core.Services;

internal sealed class AcquisitionDiagnosticsStore
{
    private readonly object _sync = new();
    private readonly Stopwatch _stopwatch = new();
    private long _totalCycleMs;
    private long _lastCycleMs;
    private long _maxCycleMs;
    private int _completedCycles;
    private int _failedCycles;
    private int _successfulCycles;
    private int _lastSuccessfulDevices;
    private int _configuredDevices;
    private int _estimatedReadOperations;
    private int _batchReadRequests;
    private int _batchReadSuccesses;
    private int _batchReadValues;
    private int _batchReadFallbacks;
    private int _batchPlanRebuilds;
    private long _batchPlanBuildMilliseconds;
    private long _dwordReadMilliseconds;
    private long _alarmReadMilliseconds;
    private long _defectReadMilliseconds;
    private long _countAlarmReadMilliseconds;
    private long _historyWriteMilliseconds;
    private DateTime? _lastSuccessfulAt;
    private HashSet<string> _lastSuccessfulDeviceIds = [];
    private bool _lastCycleSucceeded;
    private int _consecutiveFailureCycles;
    private DateTime? _lastFailureAt;
    private string? _lastFailureMessage;

    public void Start() => _stopwatch.Restart();

    public void Stop() => _stopwatch.Stop();

    public AcquisitionDiagnosticsSnapshot Snapshot()
    {
        lock (_sync)
        {
            return new AcquisitionDiagnosticsSnapshot
            {
                CompletedCycles = _completedCycles,
                FailedCycles = _failedCycles,
                LastCycleMilliseconds = _lastCycleMs,
                AverageCycleMilliseconds = _completedCycles == 0 ? 0 : (double)_totalCycleMs / _completedCycles,
                MaxCycleMilliseconds = _maxCycleMs,
                LastSuccessfulDevices = _lastSuccessfulDevices,
                ConfiguredDevices = _configuredDevices,
                LastSuccessfulAt = _lastSuccessfulAt,
                SuccessfulCycles = _successfulCycles,
                EstimatedReadOperations = _estimatedReadOperations,
                BatchReadRequests = _batchReadRequests,
                BatchReadSuccesses = _batchReadSuccesses,
                BatchReadValues = _batchReadValues,
                BatchReadFallbacks = _batchReadFallbacks,
                BatchPlanRebuilds = _batchPlanRebuilds,
                BatchPlanBuildMilliseconds = _batchPlanBuildMilliseconds,
                DWordReadMilliseconds = _dwordReadMilliseconds,
                AlarmReadMilliseconds = _alarmReadMilliseconds,
                DefectReadMilliseconds = _defectReadMilliseconds,
                CountAlarmReadMilliseconds = _countAlarmReadMilliseconds,
                HistoryWriteMilliseconds = _historyWriteMilliseconds,
                LastSuccessfulDeviceIds = _lastSuccessfulDeviceIds.ToFrozenSet(),
                LastCycleSucceeded = _lastCycleSucceeded,
                ConsecutiveFailureCycles = _consecutiveFailureCycles,
                LastFailureAt = _lastFailureAt,
                LastFailureMessage = _lastFailureMessage,
            };
        }
    }

    public void RecordConnectedCycle(
        IReadOnlySet<string> successDeviceIds,
        bool noDevicesToRead,
        int configuredDevices,
        int estimatedReadOperations,
        int batchReadRequests,
        int batchReadSuccesses,
        int batchReadValues,
        int batchReadFallbacks,
        int batchPlanRebuilds,
        long batchPlanBuildMilliseconds)
    {
        lock (_sync)
        {
            _lastSuccessfulDevices = successDeviceIds.Count;
            _configuredDevices = configuredDevices;
            _estimatedReadOperations = estimatedReadOperations;
            _batchReadRequests = batchReadRequests;
            _batchReadSuccesses = batchReadSuccesses;
            _batchReadValues = batchReadValues;
            _batchReadFallbacks = batchReadFallbacks;
            _batchPlanRebuilds = batchPlanRebuilds;
            _batchPlanBuildMilliseconds = batchPlanBuildMilliseconds;
            _lastSuccessfulDeviceIds = successDeviceIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            _lastCycleSucceeded = noDevicesToRead || successDeviceIds.Count > 0;
            if (_lastCycleSucceeded)
            {
                _consecutiveFailureCycles = 0;
            }
            else
            {
                _consecutiveFailureCycles++;
                _lastFailureAt = DateTime.Now;
                _lastFailureMessage = "所有设备读取失败";
            }

            if (successDeviceIds.Count > 0)
            {
                _successfulCycles++;
                _lastSuccessfulAt = DateTime.Now;
            }
            if (!noDevicesToRead && successDeviceIds.Count == 0)
                _failedCycles++;
        }
    }

    public void RecordStageTimings(
        long dwordReadMilliseconds,
        long alarmReadMilliseconds,
        long defectReadMilliseconds,
        long countAlarmReadMilliseconds,
        long historyWriteMilliseconds)
    {
        lock (_sync)
        {
            _dwordReadMilliseconds = dwordReadMilliseconds;
            _alarmReadMilliseconds = alarmReadMilliseconds;
            _defectReadMilliseconds = defectReadMilliseconds;
            _countAlarmReadMilliseconds = countAlarmReadMilliseconds;
            _historyWriteMilliseconds = historyWriteMilliseconds;
        }
    }

    public void RecordDisconnected(int configuredDevices, int estimatedReadOperations)
    {
        lock (_sync)
        {
            _lastSuccessfulDevices = 0;
            _configuredDevices = configuredDevices;
            _estimatedReadOperations = estimatedReadOperations;
            _lastSuccessfulDeviceIds = [];
            _lastCycleSucceeded = false;
            _consecutiveFailureCycles++;
            _lastFailureAt = DateTime.Now;
            _lastFailureMessage = "PLC 未连接";
            _failedCycles++;
        }
    }

    public void RecordFailure(string message)
    {
        lock (_sync)
        {
            _lastCycleSucceeded = false;
            _consecutiveFailureCycles++;
            _lastFailureAt = DateTime.Now;
            _lastFailureMessage = message;
        }
    }

    public void CompleteCycle()
    {
        var cycleMilliseconds = _stopwatch.ElapsedMilliseconds;
        _stopwatch.Restart();
        lock (_sync)
        {
            _completedCycles++;
            _lastCycleMs = cycleMilliseconds;
            _totalCycleMs += cycleMilliseconds;
            _maxCycleMs = Math.Max(_maxCycleMs, cycleMilliseconds);
        }
    }
}
