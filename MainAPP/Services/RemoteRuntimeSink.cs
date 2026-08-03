using Kanban.Client;
using Kanban.Core.Services;
using Kanban.Core.Models;
using Kanban.Core.Data;
using Kanban.Core.Entities;
using Kanban.Core.Mapping;
using Kanban.Contracts.Dtos;
using Kanban.Contracts.Enums;
using AlarmEventType = Kanban.Contracts.Enums.AlarmEventType;
using MainAPP.Models;
using Microsoft.Extensions.Logging;
using System.Windows.Threading;

namespace MainAPP.Services;

/// <summary>
/// Remote 模式运行时同步器：把 Collector 推来的快照/事件灌回本地内存状态。
/// 双连接架构（与 WASM 端同源约束，桌面端同适用）：
///   _client        快照连接：唯一长驻 = 快照订阅（500ms）；Invoke（初始快照/设备/工单）走此连接
///   _eventsClient  事件连接：唯一长驻优先级——报警（第一个长驻，必须实时，不受"多长驻延迟"影响）
///                   + Meta（第二个长驻，一次性延迟约 13s 可接受，工单/班次低频）
/// 说明：桌面端"长驻+Invoke"合法（RemoteHistoryQueryService 已用），但"同连接多长驻订阅"
/// 第二个起会延迟（实测 13s）——报警若与快照同连接将成为第二长驻而延迟，故必须分连接。
/// </summary>
public sealed class RemoteRuntimeSink : IAsyncDisposable
{
    private readonly KanbanDataClient _client;
    private readonly KanbanDataClient _eventsClient;
    private readonly DeviceRepository _deviceRepository;
    private readonly WorkOrderRepository _workOrderRepository;
    private readonly ILogger<RemoteRuntimeSink> _logger;
    private readonly Dispatcher _dispatcher;

    public RemoteRuntimeSink(
        KanbanDataClient client,
        DeviceRepository deviceRepository,
        WorkOrderRepository workOrderRepository,
        ILoggerFactory loggerFactory,
        ILogger<RemoteRuntimeSink> logger)
    {
        _client = client;
        _deviceRepository = deviceRepository;
        _workOrderRepository = workOrderRepository;
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;
        // 事件连接：与快照连接分开，保证报警是事件连接的第一个长驻订阅（实时不延迟）
        _eventsClient = new KanbanDataClient(
            client.HubUrl, loggerFactory.CreateLogger<KanbanDataClient>(), useMessagePack: true);
    }

    /// <summary>启动订阅。调用方须确保主 KanbanDataClient 已连接（事件连接在内部连接）。</summary>
    public void Start()
    {
        // 连接后先拉一次当前快照（覆盖 Collector 重启导致的内存清空）
        _client.OnSnapshot(OnSnapshotReceived);
        _client.Reconnected += (_, _) => { _ = OnReconnectedAsync(); };
        // 事件连接：报警（第一个长驻）+ Meta（工单/班次，第二个长驻）
        _eventsClient.OnAlarmEvent(OnAlarmEventReceived);
        _eventsClient.OnMeta(OnMetaReceived);
        _eventsClient.Reconnected += (_, _) => { _ = OnEventsReconnectedAsync(); };

        _ = RefreshAsync();
        _ = _client.SubscribeSnapshotsAsync();
        _ = StartEventLinkAsync();
    }

    /// <summary>建立事件连接并订阅报警（第一个长驻）+ Meta（第二个长驻，一次性延迟可接受）。</summary>
    private async Task StartEventLinkAsync()
    {
        try
        {
            await _eventsClient.ConnectAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "事件连接建立失败：报警事件与工单/班次推送不可用（快照链路不受影响）");
            return;
        }
        await SubscribeAlarmEventsWithResumeAsync();
        await SubscribeMetaSafeAsync();
    }

    /// <summary>报警事件游标：已消费的最大 Seq。断线重连后从此处补拉，避免漏报。</summary>
    private long _lastAlarmSeq;

    /// <summary>订阅报警事件流：带游标断线续传（服务端环形缓冲按 afterSeq 补发）。</summary>
    private async Task SubscribeAlarmEventsWithResumeAsync()
    {
        try
        {
            await _eventsClient.SubscribeAlarmEventsAsync(_lastAlarmSeq);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "订阅报警事件流失败（游标 {Seq}）", _lastAlarmSeq);
        }
    }

    /// <summary>订阅元数据流（工单/班次，Collector 5s 推送）：长驻 fault 属正常生命周期，观察防未观察异常。</summary>
    private async Task SubscribeMetaSafeAsync()
    {
        try
        {
            await _eventsClient.SubscribeMetaAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "元数据订阅结束（连接断开/重连触发，属正常）");
        }
    }

    /// <summary>主连接重连成功：恢复快照订阅 + 按游标补拉报警事件。</summary>
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
    }

    /// <summary>事件连接重连成功：恢复报警 + 元数据订阅。</summary>
    private async Task OnEventsReconnectedAsync()
    {
        _logger.LogInformation("事件连接重连成功，恢复报警/元数据订阅");
        await SubscribeAlarmEventsWithResumeAsync();
        await SubscribeMetaSafeAsync();
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
        // tombstone：Collector 设备配置删除广播，从本地移除该设备（Runtimes 集合 + RuntimeMap）
        if (snapshot.Removed)
        {
            _deviceRepository.RemoveRuntime(snapshot.DeviceId);
            return;
        }

        var device = _deviceRepository.GetDeviceById(snapshot.DeviceId);
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
                var device = _deviceRepository.GetDeviceById(evt.DeviceId);
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

    /// <summary>
    /// 元数据推送（Collector 5s）：**增量同步**工单列表。
    /// Meta 只携带"每设备当前工单"（Running 或最新一条 Pending），是全量工单表的子集——
    /// 绝不能全量重建本地集合（会把未绑定设备的工单/多条 Pending 从 UI 清掉）。
    /// 增量语义：meta 中出现 → 服务器权威（已存在则整项替换触发 UI 通知，不存在则插入首位）；meta 未出现 → 本地保留。
    /// 局限：跨端删除的工单不会出现在 meta 中，本机无法感知（低频场景，可接受）。
    /// 工单列表绑定 UI（ObservableCollection），走 Dispatcher 封送。
    /// </summary>
    private void OnMetaReceived(MetaStateDto meta)
    {
        // 数据新鲜度：Meta 约 5s 一帧，快照增量发布后静止设备不再触发 OnSnapshot，
        // 必须由 Meta 维持 LastDataReceivedAt 前进（否则全厂静止时 WPF 10s 停滞判定误报）
        _client.MarkDataReceived();
        _dispatcher.InvokeAsync(() =>
        {
            try
            {
                foreach (var d in meta.Devices)
                {
                    if (d.WorkOrder is null) continue;
                    var entity = WorkOrderMapper.ToEntity(d.WorkOrder);
                    var existing = _workOrderRepository.WorkOrders.FirstOrDefault(w => w.Id == entity.Id);
                    if (existing is not null)
                    {
                        // 服务器权威：整项替换（与 SyncMemoryCollection 一致，触发 UI 重新读取）
                        var idx = _workOrderRepository.WorkOrders.IndexOf(existing);
                        if (idx >= 0)
                            _workOrderRepository.WorkOrders[idx] = entity;
                    }
                    else
                    {
                        // 跨端新增（另一台 WPF/浏览器创建的工单）：按 CreatedAt 倒序约定插到首位
                        _workOrderRepository.WorkOrders.Insert(0, entity);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "应用元数据推送失败（工单列表增量同步）");
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _eventsClient.DisposeAsync();
        // 主连接由 KanbanDataClient 统一释放（StartupCoordinator/退出流程）
    }
}
