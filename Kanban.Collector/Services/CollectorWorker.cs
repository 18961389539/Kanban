using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Services;
using Kanban.Collector.Core.Localization;
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
    private readonly CollectorHealthState _healthState;
    private readonly ILogger<CollectorWorker> _logger;
    private PlcDataAcquisitionService? _acquisition;

    public CollectorWorker(
        IServiceProvider services,
        SnapshotPublisher snapshotPublisher,
        CollectorHealthState healthState,
        ILogger<CollectorWorker> logger)
    {
        _services = services;
        _snapshotPublisher = snapshotPublisher;
        _healthState = healthState;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await InitializeAsync(stoppingToken);
            _logger.LogInformation("采集服务初始化完成，开始轮询采集");
            _healthState.MarkReady();

            // 快照发布节奏：与采集轮询（200ms）解耦，500ms 推送一次全量快照
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                Interlocked.Increment(ref CollectorMetrics.AcquisitionCycleCount);
                try
                {
                    _snapshotPublisher.PublishAll();
                }
                catch (Exception ex)
                {
                    // 单轮发布异常隔离（审查修复 2026-08-15）：任一异常不得终止采集服务——
                    // 此前异常会跳出循环 → MarkFailed + rethrow，形成"进程活着、采集已死"的假健康。
                    // 记录错误后继续下一轮；若连续失败，readiness 探针仍可凭诊断新鲜度判不健康。
                    _logger.LogError(ex, "快照发布失败（已隔离，下一轮重试）");
                }
                // 每轮把最新采集诊断写入健康状态（readiness 判活依据；无采集实例时跳过）
                if (_acquisition is not null)
                {
                    try
                    {
                        _healthState.UpdateAcquisition(_acquisition.GetDiagnosticsSnapshot());
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "采集诊断更新失败（已隔离，下一轮重试）");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("采集服务停止");
        }
        catch (Exception ex)
        {
            // 关键：初始化失败必须重新抛出，宿主（Windows Service 恢复策略 / 控制台退出）才能拉起进程。
            // 修复前异常被吞掉 → 进程与 /healthz 仍"存活"，但采集已永久停止（假健康）。
            _healthState.MarkFailed(ex.Message);
            _logger.LogCritical(ex, "采集服务初始化失败，进程退出等待宿主拉起");
            throw;
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

        // 1.0 刷新 PLC 运行时配置档案：宿主启动时 MetaPublisher 等单例（AddHostedService/AddSingleton）
        // 可能在 settings.Load() 之前构造 PlcDataAcquisitionService→SharedPlcDriverRouter→PlcRuntimeProfileProvider，
        // 此时 AppSettings 仍是默认值（127.0.0.1）→ 单例档案被永久污染，采集永远连不上真实 PLC。
        // Load 完成后显式刷新，保证 EnsureConnected 使用实际配置（回归自 04e8442：MetaPublisher 注入 plcService）。
        var runtimeSessions = _services.GetService<IPlcRuntimeSessionManager>();
        if (runtimeSessions is not null)
            runtimeSessions.RefreshFromSettings();
        else
            _services.GetRequiredService<IPlcRuntimeProfileProvider>().Refresh(settings.PlcConfig);

        // 1.1 应用界面语言（Collector 进程独立应用，与 MainAPP 保持一致）
        // 读取 settings.json 的 Language 字段，覆盖 Kanban.Collector.Core 共享的连接状态文案与校验消息。
        // 确保 Collector 进程在 en/ja 模式下也使用正确语言（不依赖 MainAPP 推送）。
        var langCode = settings.EffectiveLanguageCode;
        var localizationOverride = LocalizationOverrideLoader.Load(
            settings.GetFilePath(LocalizationOverrideLoader.FileName));
        if (!localizationOverride.IsValid)
        {
            _logger.LogWarning("本地化覆盖文件无效，已回退内置资源: {Errors}",
                string.Join("; ", localizationOverride.Errors));
        }
        else if (localizationOverride.AppliedCount > 0)
        {
            _logger.LogInformation("已加载本地化覆盖: {Count} 项", localizationOverride.AppliedCount);
        }
        Kanban.Collector.Core.Localization.ConnectionStatusMessages.ApplyLanguage(langCode);
        Kanban.Collector.Core.Localization.ValidationMessages.ApplyLanguage(langCode);
        Kanban.Collector.Core.Localization.RecipeValidationMessages.ApplyLanguage(langCode);
        _logger.LogInformation("界面语言已应用：{Lang}", langCode);

        // 1.2 审计门面接通：Remote 管理写接口（设备/设置/工单）经 Hub 用 AuditLog 记录，
        // 未初始化时静默忽略（MainAPP 本地模式在 App 启动时自行初始化，互不干扰）。
        AuditLog.Initialize(_services.GetService<IAuditService>());

        // 2. 设备配置加载
        var deviceRepo = _services.GetRequiredService<DeviceRepository>();
        deviceRepo.LoadAll();

        // 3. 数据库 schema + WAL
        var dbProvider = _services.GetRequiredService<DatabaseProvider>();
        dbProvider.EnsureCreatedAll();
        dbProvider.EnsureWalModeEnabled();

        // 4. 工单加载（依赖 schema）
        _services.GetRequiredService<WorkOrderRepository>().LoadAll();

        // 4.1 配方库加载（recipes.json，Remote 模式下 MainAPP 经 Hub 同步/下发）
        _services.GetRequiredService<IRecipeStore>().LoadAll();

        // 5. 启动 PLC 采集
        _acquisition = _services.GetRequiredService<PlcDataAcquisitionService>();
        // 采集层 → 事件广播器接线（修复 Remote 事件流缺口：EventBroadcaster.Publish* 此前无生产调用点）。
        // ⚠️ 必须放在 settings.Load() 之后、本方法内（此处）——若在 Program.cs 的 app.Build() 后提前接线，
        // 会用尚未 Load 的默认 PlcConfig 构造采集服务/驱动，导致 PLC 永远连不上（实测对照确认）。
        var eventBroadcaster = _services.GetRequiredService<EventBroadcaster>();
        _acquisition.AlarmEdgeDetected += eventBroadcaster.PublishAlarmEvent;
        _acquisition.StatusEdgeDetected += eventBroadcaster.PublishStatusEvent;
        _acquisition.Start();

        await Task.CompletedTask;
    }
}
