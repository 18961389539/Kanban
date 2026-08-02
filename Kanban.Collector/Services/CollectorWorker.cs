using Kanban.Core.Data;
using Kanban.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kanban.Collector.Services;

/// <summary>
/// 采集主循环：启动期初始化（配置加载 → 建库 → 设备/工单加载），
/// 启动 PLC 采集，并周期性把快照发布到 <see cref="SnapshotPublisher"/>。
/// </summary>
public sealed class CollectorWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly SnapshotPublisher _snapshotPublisher;
    private readonly ILogger<CollectorWorker> _logger;
    private PlcDataAcquisitionService? _acquisition;

    public CollectorWorker(
        IServiceProvider services,
        SnapshotPublisher snapshotPublisher,
        ILogger<CollectorWorker> logger)
    {
        _services = services;
        _snapshotPublisher = snapshotPublisher;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await InitializeAsync(stoppingToken);
            _logger.LogInformation("采集服务初始化完成，开始轮询采集");

            // 快照发布节奏：与采集轮询（200ms）解耦，500ms 推送一次全量快照
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                _snapshotPublisher.PublishAll();
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("采集服务停止");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "采集服务初始化失败");
        }
        finally
        {
            if (_acquisition is not null)
            {
                try { await _acquisition.StopAsync(); }
                catch (Exception ex) { _logger.LogWarning(ex, "停止采集异常"); }
            }
        }
    }

    /// <summary>
    /// 启动初始化（对齐 MainAPP.App.OnStartup 的采集相关顺序）。
    /// </summary>
    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        // 1. 配置加载
        var settings = _services.GetRequiredService<AppSettings>();
        settings.Load();
        if (!string.IsNullOrEmpty(settings.LoadErrorMessage))
        {
            _logger.LogWarning("settings.json 加载警告: {Msg}", settings.LoadErrorMessage);
        }
        _logger.LogInformation("配置加载完成，DataRoot={DataRoot}", AppSettings.DataRoot);

        // 2. 设备配置加载
        var deviceRepo = _services.GetRequiredService<DeviceRepository>();
        deviceRepo.LoadAll();

        // 3. 数据库 schema + WAL
        var dbProvider = _services.GetRequiredService<DatabaseProvider>();
        dbProvider.EnsureCreatedAll();
        dbProvider.EnsureWalModeEnabled();

        // 4. 工单加载（依赖 schema）
        _services.GetRequiredService<WorkOrderRepository>().LoadAll();

        // 5. 启动 PLC 采集
        _acquisition = _services.GetRequiredService<PlcDataAcquisitionService>();
        _acquisition.Start();

        await Task.CompletedTask;
    }
}
