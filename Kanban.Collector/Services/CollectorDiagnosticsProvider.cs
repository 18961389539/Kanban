using Kanban.Contracts.Enums;
using Kanban.Contracts.Dtos;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Services;
using Microsoft.Extensions.Logging;

namespace Kanban.Collector.Services;

/// <summary>
/// Collector 诊断快照：聚合采集循环 + 历史写入 + 连接状态，供运行监控页 Remote 模式展示。
/// </summary>
public sealed class CollectorDiagnosticsProvider
{
    private readonly PlcDataAcquisitionService _acquisition;
    private readonly HistoryService _history;
    private readonly PlcConnectionManager _connectionManager;
    private readonly IPlcRuntimeSessionManager? _runtimeSessions;
    private readonly IDeviceRepository _deviceRepository;
    private readonly SystemResourceMonitor _resources;
    private readonly ILogger<CollectorDiagnosticsProvider> _logger;
    private bool _resourceFailureLogged;

    public CollectorDiagnosticsProvider(
        PlcDataAcquisitionService acquisition,
        HistoryService history,
        PlcConnectionManager connectionManager,
        IDeviceRepository deviceRepository,
        SystemResourceMonitor resources,
        ILogger<CollectorDiagnosticsProvider> logger,
        IPlcRuntimeSessionManager? runtimeSessions = null)
    {
        _acquisition = acquisition;
        _history = history;
        _connectionManager = connectionManager;
        _runtimeSessions = runtimeSessions;
        _deviceRepository = deviceRepository;
        _resources = resources;
        _logger = logger;
    }

    public CollectorDiagnosticsDto GetSnapshot()
    {
        try
        {
            var acq = _acquisition.GetDiagnosticsSnapshot();
            var hist = _history.GetDiagnosticsSnapshot();
            CollectorMetrics.UpdateDiagnostics(acq, hist);
            CollectorMetrics.UpdateReaderDiagnostics(acq.DataSourceReaderDiagnostics);
            var profileDiagnostics = _runtimeSessions?.GetDiagnosticsSnapshot() ?? [];
            CollectorMetrics.UpdateRuntimeSessionDiagnostics(profileDiagnostics);

            SystemResourceSnapshot? resources = null;
            try
            {
                resources = _resources.Sample();
            }
            catch (Exception ex)
            {
                // 资源采样失败不能把整份诊断快照变成空对象，否则监控页会把采集状态也显示成 0。
                if (!_resourceFailureLogged)
                {
                    _resourceFailureLogged = true;
                    _logger.LogWarning(ex, "采集进程资源采样失败，运行监控的资源指标显示为空");
                }
            }

            var devices = _deviceRepository.GetDevicesSnapshot();
            return CollectorDiagnosticsMapper.Create(
                acq,
                hist,
                _connectionManager.IsConnected,
                _acquisition.IsRunning,
                _connectionManager.ConnectionStatus,
                _connectionManager.TotalDisconnectCount,
                _connectionManager.ConsecutiveFailures,
                _connectionManager.DisconnectedAt,
                devices,
                deviceId => _deviceRepository.RuntimeMap.TryGetValue(deviceId, out var runtime) ? runtime : null,
                resources,
                profileDiagnostics);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "生成 Collector 诊断快照失败");
            return new CollectorDiagnosticsDto();
        }
    }
}

