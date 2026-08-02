using System.Threading.Channels;
using Kanban.Contracts.Dtos;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kanban.Collector.Services;

/// <summary>
/// 低频元数据发布器：约 5s 一次组装"全部设备当前工单 + 班次进度"（MetaStateDto）
/// 并广播给订阅者（展示端经 Hub 订阅 OnMeta）。替代客户端轮询 Invoke——
/// 数据服务端单源、推给所有人，与快照/事件流的订阅-扇出模型一致。
/// 订阅者列表 + 扇出（每个订阅者独立 channel），断开自动退订。
/// 本身为单例 + IHostedService（同一实例）。
/// </summary>
public sealed class MetaPublisher : IHostedService, IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private readonly ConfigSyncHandler _configSyncHandler;
    private readonly ShiftProgressProvider _shiftProgressProvider;
    private readonly ILogger<MetaPublisher> _logger;
    private readonly object _gate = new();
    private readonly List<Channel<MetaStateDto>> _subscribers = new();
    private Timer? _timer;
    private MetaStateDto? _latest;

    public MetaPublisher(
        ConfigSyncHandler configSyncHandler,
        ShiftProgressProvider shiftProgressProvider,
        ILogger<MetaPublisher> logger)
    {
        _configSyncHandler = configSyncHandler;
        _shiftProgressProvider = shiftProgressProvider;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("MetaPublisher 已启动（5s 周期）");
        _timer = new Timer(_ => Publish(), null, TimeSpan.Zero, Interval);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>组装并广播元数据包（Timer 回调；组装/扇出均不阻塞长于单次查询）。</summary>
    public void Publish()
    {
        try
        {
            var meta = new MetaStateDto
            {
                Devices = _configSyncHandler.GetWorkOrderSnapshot(),
                Shift = _shiftProgressProvider.GetProgress(),
            };
            lock (_gate)
            {
                _latest = meta;
                foreach (var subscriber in _subscribers)
                    subscriber.Writer.TryWrite(meta);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "元数据发布失败");
        }
    }

    /// <summary>订阅元数据流（补发最新包；连接断开自动退订）。</summary>
    public ValueTask<ChannelReader<MetaStateDto>> SubscribeAsync(CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<MetaStateDto>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        lock (_gate)
        {
            if (_latest is not null)
                channel.Writer.TryWrite(_latest);
            _subscribers.Add(channel);
        }

        cancellationToken.Register(() =>
        {
            lock (_gate)
                _subscribers.Remove(channel);
        });

        return ValueTask.FromResult<ChannelReader<MetaStateDto>>(channel.Reader);
    }

    public void Dispose() => _timer?.Dispose();
}
