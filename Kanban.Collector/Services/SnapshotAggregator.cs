using System.Threading.Channels;
using Kanban.Contracts.Abstractions;
using Kanban.Contracts.Dtos;

namespace Kanban.Collector.Services;

/// <summary>
/// 快照聚合器：采集服务把最新设备状态写入 <see cref="PublishAsync"/>，
/// 展示端通过 SignalR Hub 订阅（<see cref="SubscribeAsync"/>）接收全量快照流。
/// 第 3 步由 PlcDataAcquisitionService（迁入）驱动发布。
/// </summary>
public sealed class SnapshotAggregator
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DeviceSnapshotDto> _latest = new();
    private readonly Channel<DeviceSnapshotDto> _broadcast = Channel.CreateUnbounded<DeviceSnapshotDto>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });

    /// <summary>
    /// 采集服务发布最新快照（内部维护最新副本 + 广播给订阅者）。
    /// </summary>
    public void Publish(DeviceSnapshotDto snapshot)
    {
        lock (_gate)
        {
            _latest[snapshot.DeviceId] = snapshot;
        }
        _broadcast.Writer.TryWrite(snapshot);
    }

    /// <summary>订阅快照流（从当前时刻开始推送后续快照）</summary>
    public async ValueTask<ChannelReader<DeviceSnapshotDto>> SubscribeAsync(CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<DeviceSnapshotDto>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        // 先补发当前最新快照，保证客户端连接即有数据
        DeviceSnapshotDto[] current;
        lock (_gate)
        {
            current = _latest.Values.ToArray();
        }
        foreach (var snapshot in current)
        {
            await channel.Writer.WriteAsync(snapshot, cancellationToken);
        }

        // 转发广播流到该订阅者专属 channel
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var snapshot in _broadcast.Reader.ReadAllAsync(cancellationToken))
                {
                    await channel.Writer.WriteAsync(snapshot, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, CancellationToken.None);

        return channel.Reader;
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
