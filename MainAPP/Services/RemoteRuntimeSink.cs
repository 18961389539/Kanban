using Kanban.Core.Services;
using Kanban.Core.Models;
using Kanban.Core.Data;
using Kanban.Core.Entities;
using Kanban.Contracts.Dtos;
using Kanban.Contracts.Enums;
using AlarmEventType = Kanban.Contracts.Enums.AlarmEventType;
using Kanban.Core.Data;
using Kanban.Core.Models;
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
        _client.Reconnected += (_, _) => { _ = OnReconnectedAsync(); };

        _ = RefreshAsync();
        _ = _client.SubscribeSnapshotsAsync();
        // 断线重连后按游标补拉未消费的报警事件
        _ = SubscribeAlarmEventsWithResumeAsync();
    }

    /// <summary>报警事件游标：已消费的最大 Seq。断线重连后从此处补拉，避免漏报。</summary>
    private long _lastAlarmSeq;

    /// <summary>订阅报警事件流：带游标断线续传（服务端环形缓冲按 afterSeq 补发）。</summary>
    private async Task SubscribeAlarmEventsWithResumeAsync()
    {
        try
        {
            await _client.SubscribeAlarmEventsAsync(_lastAlarmSeq);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "订阅报警事件流失败（游标 {Seq}）", _lastAlarmSeq);
        }
    }

    /// <summary>重连成功：恢复快照订阅 + 按游标补拉报警事件。</summary>
    private async Task OnReconnectedAsync()
    {
        _logger.LogInformation("Collector 重连成功，恢复订阅（报警游标 {Seq}）", _lastAlarmSeq);
        try
        {
            await _client.SubscribeSnapshotsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "重连后恢复快照订阅失败");
        }
        await SubscribeAlarmEventsWithResumeAsync();
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
        // 数据新鲜度：收到实时数据即刷新时间戳（采集停滞监控依据）
        _client.MarkDataReceived();
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
        // 数据新鲜度：报警事件也是实时数据信号
        _client.MarkDataReceived();
        // 更新游标：无论 UI 应用是否成功，事件已消费（防止补拉风暴）
        Interlocked.Exchange(ref _lastAlarmSeq, evt.Seq);
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
        // 数据新鲜度：状态事件也是实时数据信号
        _client.MarkDataReceived();
        // 状态事件仅入库（Collector 侧），UI 状态以快照 StatusWord 为准，此处无需处理。
        _logger.LogTrace("状态事件 {Device} {From}->{To}", evt.DeviceId, evt.PreviousState, evt.CurrentState);
    }

    public async ValueTask DisposeAsync()
    {
        // 连接由 KanbanDataClient 统一释放
        await Task.CompletedTask;
    }
}
