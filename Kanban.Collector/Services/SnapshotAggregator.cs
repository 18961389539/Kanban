using System.Threading.Channels;
using Kanban.Contracts.Abstractions;
using Kanban.Contracts.Dtos;

namespace Kanban.Collector.Services;

/// <summary>
/// 快照聚合器：采集服务把最新设备状态写入 <see cref="Publish"/>，
/// 展示端通过 SignalR Hub 订阅（<see cref="SubscribeAsync"/>）接收全量快照流。
/// 采用"订阅者列表 + 扇出"模型：每个订阅者持有独立 channel，Publish 时写入全部订阅者，
/// 保证**每个**屏端都收到完整快照（多屏广播，非竞争消费）。
/// </summary>
public sealed class SnapshotAggregator
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DeviceSnapshotDto> _latest = new();
    private readonly List<Channel<DeviceSnapshotDto>> _subscribers = new();

    /// <summary>
    /// 采集服务发布最新快照：更新最新副本 + 扇出广播给全部订阅者（不阻塞；订阅者 channel 有界
    /// 128 + DropOldest，慢/停流客户端不无界积压，丢最旧保最新——见 SubscribeAsync）。
    /// </summary>
    public void Publish(DeviceSnapshotDto snapshot)
    {
        lock (_gate)
        {
            _latest[snapshot.DeviceId] = snapshot;
            foreach (var subscriber in _subscribers)
            {
                subscriber.Writer.TryWrite(snapshot);
            }
        }
    }

    /// <summary>
    /// 订阅快照流（从当前时刻开始推送后续快照）。
    /// 先补发当前最新快照保证客户端连接即有数据；连接断开（cancellationToken 触发）时自动退订。
    /// </summary>
    public ValueTask<ChannelReader<DeviceSnapshotDto>> SubscribeAsync(CancellationToken cancellationToken)
    {
        // 有界 + DropOldest：快照每次全量、丢最旧保最新（客户端始终拿到最新状态）；
        // 慢/停流客户端（如 WASM 切后台 JS 冻结）不再无界积压——无界缓冲可 OOM 拖垮采集进程。
        var channel = Channel.CreateBounded<DeviceSnapshotDto>(
            new BoundedChannelOptions(128)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
            });

        lock (_gate)
        {
            // 补发最新快照，保证客户端连接即有数据
            foreach (var snapshot in _latest.Values)
            {
                channel.Writer.TryWrite(snapshot);
            }
            _subscribers.Add(channel);
            CollectorMetrics.TrackSubscriberCount(ref CollectorMetrics.SnapshotSubscriberPeak, _subscribers.Count);
        }

        // 连接断开（Hub 的 ConnectionAborted）时退订，避免订阅者泄漏
        cancellationToken.Register(() =>
        {
            lock (_gate)
            {
                _subscribers.Remove(channel);
            }
        });

        return ValueTask.FromResult<ChannelReader<DeviceSnapshotDto>>(channel.Reader);
    }

    /// <summary>
    /// 设备配置删除：从最新副本移除，并向所有订阅者广播 tombstone 快照（Removed=true），
    /// 客户端据此从内存移除该设备（快照流只有 upsert 语义，删除必须显式表达）。
    /// </summary>
    public void RemoveDevice(string deviceId)
    {
        var tombstone = new DeviceSnapshotDto
        {
            DeviceId = deviceId,
            DeviceName = "",
            Status = default,
            Removed = true,
        };
        lock (_gate)
        {
            _latest.Remove(deviceId);
            foreach (var subscriber in _subscribers)
            {
                subscriber.Writer.TryWrite(tombstone);
            }
        }
    }

    /// <summary>获取当前全部设备快照（连接后首次拉取用）</summary>
    public Task<IReadOnlyList<DeviceSnapshotDto>> GetCurrentSnapshotsAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<DeviceSnapshotDto>>(_latest.Values.ToArray());
        }
    }
}
