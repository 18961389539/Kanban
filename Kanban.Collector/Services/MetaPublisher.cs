using System.Threading.Channels;
using Kanban.Contracts.Dtos;
using Kanban.Contracts.Enums;
using Kanban.Contracts.Metrics;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kanban.Collector.Services;

/// <summary>
/// 低频元数据发布器：约 5s 一次组装"全部设备当前工单 + 班次进度 + 缺陷 TOP5 + 上班次汇总"
/// （MetaStateDto）并广播给订阅者（展示端经 Hub 订阅 OnMeta）。替代客户端轮询 Invoke——
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
    private readonly CollectorHealthState? _healthState;
    private readonly IServiceProvider? _services;
    private DeviceRepository? _deviceRepository;
    private IPlcDataAcquisitionService? _plcService;
    private IDefectHistoryReader? _defectHistoryReader;
    private AppSettings? _appSettings;
    private readonly object _gate = new();
    private readonly List<Channel<MetaStateDto>> _subscribers = new();
    private readonly CancellationTokenSource _stopCts = new();
    private Timer? _timer;
    private Task? _runTask;
    private MetaStateDto? _latest;
    // 脏标记：工单变更版本 + 上次组装的快照缓存（无变更时跳过全量拷贝/索引）
    private long _lastWorkOrderVersion = -1;
    private IReadOnlyList<DeviceWorkOrderDto>? _cachedWorkOrders;

    public MetaPublisher(
        ConfigSyncHandler configSyncHandler,
        ShiftProgressProvider shiftProgressProvider,
        ILogger<MetaPublisher> logger,
        DeviceRepository? deviceRepository = null,
        IPlcDataAcquisitionService? plcService = null,
        CollectorHealthState? healthState = null,
        IServiceProvider? services = null)
    {
        _configSyncHandler = configSyncHandler;
        _shiftProgressProvider = shiftProgressProvider;
        _logger = logger;
        _deviceRepository = deviceRepository;
        _plcService = plcService;
        _healthState = healthState;
        _services = services;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _runTask = RunAsync(cancellationToken);
        return Task.CompletedTask;
    }

    private async Task RunAsync(CancellationToken startupCancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            startupCancellationToken,
            _stopCts.Token);
        try
        {
            if (_healthState is not null &&
                !await _healthState.WaitUntilReadyAsync(linkedCts.Token))
            {
                _logger.LogWarning("Collector 初始化失败，跳过元数据发布");
                return;
            }

            // 延迟到 CollectorWorker 完成 settings.Load 后再解析采集服务，避免默认 PLC 配置污染单例。
            _deviceRepository ??= _services?.GetService<DeviceRepository>();
            _plcService ??= _services?.GetService<IPlcDataAcquisitionService>();
            _defectHistoryReader ??= _services?.GetService<IDefectHistoryReader>();
            _appSettings ??= _services?.GetService<AppSettings>();
            _logger.LogInformation("MetaPublisher 已启动（5s 周期）");
            _timer = new Timer(_ => Publish(), null, TimeSpan.Zero, Interval);
            await Task.Delay(Timeout.InfiniteTimeSpan, linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            // 正常停止或启动取消。
        }
        finally
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopCts.Cancel();
        _timer?.Dispose();
        if (_runTask is not null)
            await _runTask.WaitAsync(cancellationToken);
    }

    /// <summary>组装并广播元数据包（Timer 回调；组装/扇出均不阻塞长于单次查询）。</summary>
    public void Publish()
    {
        try
        {
            // 脏标记：工单版本未变时复用缓存快照（消除每 5s 的全量拷贝 + 索引组装）；
            // 版本变了才重组装。构建失败（null）时跳过本次：客户端保留上次数据，避免"暂无工单"假空态
            var version = _configSyncHandler.WorkOrderChangeVersion;
            if (version != _lastWorkOrderVersion || _cachedWorkOrders is null)
            {
                var workOrders = _configSyncHandler.GetWorkOrderSnapshot();
                if (workOrders is null)
                    return;
                _cachedWorkOrders = workOrders;
                _lastWorkOrderVersion = version;
            }
            var (defectTop, defectSummaries) = BuildDefectPareto();
            var meta = new MetaStateDto
            {
                Devices = _cachedWorkOrders!,
                Shift = _shiftProgressProvider.GetProgress(),
                DefectTop = defectTop,
                DefectSummaries = defectSummaries,
                LastShifts = BuildLastShifts(),
            };
            lock (_gate)
            {
                _latest = meta;
                foreach (var subscriber in _subscribers)
                    subscriber.Writer.TryWrite(meta);
                CollectorMetrics.TrackSubscriberCount(ref CollectorMetrics.MetaSubscriberPeak, _subscribers.Count);
            }
            Interlocked.Increment(ref CollectorMetrics.MetaPublishCount);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref CollectorMetrics.PublishErrorCount);
            _logger.LogError(ex, "元数据发布失败");
        }
    }

    /// <summary>
    /// 各设备缺陷帕累托（与 WPF 首页同源）：本班次窗口内新增件数（历史快照差分）。
    /// 行 = 正计数 TOP5 + 可选「其他」；摘要按设备下发。
    /// </summary>
    private (List<DeviceDefectCountDto> Top, List<DeviceDefectSummaryDto> Summaries) BuildDefectPareto()
    {
        if (_deviceRepository is null) return ([], []);

        List<Core.Models.ShiftConfig> shifts;
        if (_appSettings != null)
        {
            lock (_appSettings.ShiftsLock)
                shifts = _appSettings.Shifts.ToList();
        }
        else
            shifts = [];

        var now = DateTime.Now;
        var top = new List<DeviceDefectCountDto>();
        var summaries = new List<DeviceDefectSummaryDto>();
        foreach (var device in _deviceRepository.GetDevicesSnapshot())
        {
            var ng = 0;
            if (_deviceRepository.RuntimeMap.TryGetValue(device.Id, out var runtime))
                ng = runtime.TotalNgProduction;

            var inputs = DefectParetoInputBuilder.BuildHomeInputs(device, _defectHistoryReader, shifts, now);
            var result = DefectParetoMetrics.Build(inputs, ng);
            summaries.Add(new DeviceDefectSummaryDto
            {
                DeviceId = device.Id,
                ConfiguredCount = result.ConfiguredCount,
                TotalCount = result.TotalCount,
                EmptyKind = result.EmptyKind,
            });
            foreach (var row in result.Rows)
            {
                top.Add(new DeviceDefectCountDto
                {
                    DeviceId = device.Id,
                    DeviceName = device.Name,
                    Name = row.Name,
                    Count = row.Count,
                    Rank = row.Rank,
                    ShareOfTotal = row.ShareOfTotal,
                    CumulativeShare = row.CumulativeShare,
                    IsVitalFew = row.IsVitalFew,
                    IsOthers = row.IsOthers,
                    OtherKindCount = row.OtherKindCount,
                    Severity = row.Severity,
                    Category = row.Category,
                    PlcAddress = row.PlcAddress,
                });
            }
        }
        return (top, summaries);
    }

    /// <summary>
    /// 各设备上一班次产量汇总（班次切换缓存）。对齐 WPF 首页合格率卡的
    /// ShiftOutputDiff/ShiftNgDiff 口径（HomeViewModel.RefreshLastShiftComparison）。
    /// </summary>
    private List<DeviceShiftSummaryDto> BuildLastShifts()
    {
        if (_plcService is null) return [];
        var list = new List<DeviceShiftSummaryDto>();
        foreach (var device in _deviceRepository?.GetDevicesSnapshot() ?? [])
        {
            var (ok, ng, shiftName) = _plcService.GetLastShiftSummary(device.Id);
            if (string.IsNullOrEmpty(shiftName)) continue;
            list.Add(new DeviceShiftSummaryDto
            {
                DeviceId = device.Id,
                ShiftName = shiftName,
                Ok = ok,
                Ng = ng,
            });
        }
        return list;
    }

    /// <summary>订阅元数据流（补发最新包；连接断开自动退订）。</summary>
    public ValueTask<ChannelReader<MetaStateDto>> SubscribeAsync(CancellationToken cancellationToken)
    {
        // 有界 + DropOldest（5s 低频，容量 16 ≈ 80s 缓冲；慢客户端不无界积压，丢最旧保最新）
        var channel = Channel.CreateBounded<MetaStateDto>(
            new BoundedChannelOptions(16)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
            });

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

    public void Dispose()
    {
        _stopCts.Cancel();
        _timer?.Dispose();
        _stopCts.Dispose();
    }
}
