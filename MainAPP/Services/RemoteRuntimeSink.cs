using Kanban.Contracts.Dtos;
using Kanban.Contracts.Enums;
using MainAPP.Data;
using MainAPP.Models;
using Microsoft.Extensions.Logging;
using System.Windows.Threading;

namespace MainAPP.Services;

/// <summary>
/// Remote 模式运行时同步器：订阅 <see cref="KanbanDataClient"/> 的快照/事件流，
/// 把 Collector 推来的 <see cref="DeviceSnapshotDto"/> 灌回 <see cref="DeviceRepository.Runtimes"/>
/// （对齐 AlarmStateTracker 的 UI 线程封送约定），报警事件同步到 Device.Alarms 的 StartTime/EndTime。
/// ViewModel 层完全无感——它们照常读 Runtimes / Device.Alarms。
/// </summary>
public sealed class RemoteRuntimeSink : IAsyncDisposable
{
    private readonly KanbanDataClient _client;
    private readonly DeviceRepository _deviceRepository;
    private readonly ILogger<RemoteRuntimeSink> _logger;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, DeviceRuntime> _runtimeById = new();
    private readonly Dictionary<string, Alarm> _alarmByKey = new();

    public RemoteRuntimeSink(
        KanbanDataClient client,
        DeviceRepository deviceRepository,
        ILogger<RemoteRuntimeSink> logger)
    {
        _client = client;
        _deviceRepository = deviceRepository;
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    /// <summary>启动订阅。调用方须确保 KanbanDataClient 已连接。</summary>
    public void Start()
    {
        // 连接后先拉一次当前快照（覆盖 Collector 重启导致的内存清空）
        _client.OnSnapshot(OnSnapshotReceived);
        _client.OnAlarmEvent(OnAlarmEventReceived);
        _client.OnStatusEvent(OnStatusEventReceived);

        _ = RefreshAsync();
        _ = _client.SubscribeSnapshotsAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            var snapshots = await _client.GetCurrentSnapshotsAsync();
            foreach (var s in snapshots)
                ApplySnapshot(s);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "拉取初始快照失败（等待订阅推送）");
        }
    }

    private void OnSnapshotReceived(DeviceSnapshotDto snapshot)
    {
        // 快照频率 500ms，直接跑在 SignalR 回调线程；设备运行时状态非绑定主源（Runtimes 已注册集合同步锁），
        // 但 Alarm.StartTime/EndTime 绑定 UI，走 Dispatcher 封送，避免跨线程绑定异常。
        _dispatcher.InvokeAsync(() =>
        {
            try
            {
                ApplySnapshot(snapshot);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "应用快照失败 Device={DeviceId}", snapshot.DeviceId);
            }
        });
    }

    private void ApplySnapshot(DeviceSnapshotDto snapshot)
    {
        var device = _deviceRepository.GetDevicesSnapshot().FirstOrDefault(d => d.Id == snapshot.DeviceId);
        if (device is null) return; // Collector 的设备列表未与本地同步时跳过（等设备配置同步后再灌入）

        var runtime = _deviceRepository.EnsureRuntime(device);
        runtime.SyncTargetCycle(snapshot.TargetCycle);
        runtime.UpdateFromCollector(
            snapshot.OkProduction, snapshot.NgProduction, snapshot.StatusWord,
            snapshot.TotalOkProduction, snapshot.TotalNgProduction,
            snapshot.RunTime, snapshot.AlarmTime, snapshot.PausedTime);
    }

    private void OnAlarmEventReceived(AlarmEventDto evt)
    {
        _dispatcher.InvokeAsync(() =>
        {
            try
            {
                var device = _deviceRepository.GetDevicesSnapshot().FirstOrDefault(d => d.Id == evt.DeviceId);
                if (device is null) return;

                var alarm = device.Alarms.FirstOrDefault(a => a.Id == evt.AlarmId);
                if (alarm is null) return;

                switch (evt.EventType)
                {
                    case AlarmEventType.Triggered:
                    case AlarmEventType.ShiftChange:
                        alarm.StartTime = evt.EventTime;
                        alarm.EndTime = default;
                        break;
                    case AlarmEventType.Recovered:
                        alarm.EndTime = evt.EventTime;
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "应用报警事件失败 Alarm={AlarmId}", evt.AlarmId);
            }
        });
    }

    private void OnStatusEventReceived(StatusEventDto evt)
    {
        // 状态事件仅入库（Collector 侧），UI 状态以快照 StatusWord 为准，此处无需处理。
        _logger.LogTrace("状态事件 {Device} {From}->{To}", evt.DeviceId, evt.PreviousState, evt.CurrentState);
    }

    public async ValueTask DisposeAsync()
    {
        // 连接由 KanbanDataClient 统一释放
        await Task.CompletedTask;
    }
}
