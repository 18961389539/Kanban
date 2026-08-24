using Kanban.Client;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Mapping;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using Kanban.Collector.Core.Localization;
using MainAPP.Resources;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace MainAPP.Services;

/// <summary>
/// Remote 模式启动引导：连接 Collector、启用远程存储适配器并加载初始配置。
/// 运行时快照/事件订阅仍由 <see cref="RemoteRuntimeSink"/> 独立负责。
/// </summary>
public sealed class RemoteDataLinkBootstrapper(IServiceProvider services) : IAsyncDisposable
{
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly object _retryGate = new();
    private int _disposeStarted;
    private bool _retryLoopStarted;
    private Task? _retryTask;
    private bool _adminRetryLoopStarted;
    private Task? _adminRetryTask;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var client = services.GetRequiredService<KanbanDataClient>();
        var adminClient = services.GetRequiredService<KanbanAdminClient>();
        var sink = services.GetRequiredService<RemoteRuntimeSink>();

        // 远程写操作审计溯源：把当前登录用户作为 operator 经连接查询串传给 Collector Hub。
        client.OperatorName = services.GetService<UserSession>()?.CurrentUserDisplay ?? string.Empty;
        adminClient.OperatorName = client.OperatorName;

        client.ConnectionStateChanged += (_, connected) =>
        {
            if (connected)
                services.GetRequiredService<PlcConnectionManager>().SyncRemoteConnected(Strings.M134);
            else
                services.GetRequiredService<PlcConnectionManager>().MarkDisconnected(DisconnectionReason.ReadFailure);
        };
        client.Reconnecting += (_, _) =>
        {
            var attempt = client.ConsecutiveFailures;
            services.GetRequiredService<PlcConnectionManager>().SyncRemoteReconnecting(attempt);
        };

        var sinkStarted = false;
        void EnsureSinkStarted()
        {
            if (sinkStarted) return;
            sinkStarted = true;
            sink.Start();
        }

        client.Reconnected += (_, _) =>
        {
            EnsureSinkStarted();
            _ = SynchronizeRemoteLocalizationSafeAsync(client);
        };
        try
        {
            await client.ConnectAsync(cancellationToken);
            EnsureSinkStarted();
            await SynchronizeRemoteLocalizationAsync(client, cancellationToken);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "首次连接采集服务失败，转入后台重连循环（5s 间隔）");
            StartRetryLoop(client, EnsureSinkStarted, cancellationToken);
        }

        try
        {
            await adminClient.ConnectAsync(cancellationToken);
            Log.Information("已连接采集管理服务 {Url}", adminClient.HubUrl);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "首次连接采集管理服务失败，管理写入将在连接恢复后可用");
            StartAdminRetryLoop(adminClient, cancellationToken);
        }

        try
        {
            var serverVersion = await client.GetServerVersionAsync();
            Log.Information("已连接采集服务 {Url}，服务端版本 {ServerVersion}", client.HubUrl, serverVersion);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "获取采集服务版本失败（不影响连接）");
        }

        // 设备/工单/配方写操作统一经构造期注入的远程存储端口委托 Collector，
        // MainAPP 不再在启动过程中修改仓储对象的可变 Hook。
        var configIO = services.GetRequiredService<DeviceConfigIOService>();
        adminClient.Reconnected += (_, _) => configIO.RefreshRemoteBackupAvailabilityAsync().Forget();
        await configIO.RefreshRemoteBackupAvailabilityAsync();

        var deviceRepo = services.GetRequiredService<DeviceRepository>();
        var recipeStore = services.GetRequiredService<IRecipeStore>();

        try
        {
            var remoteDevices = await client.GetDevicesAsync();
            var entities = DeviceMapper.ToEntities(remoteDevices);
            deviceRepo.ReplaceAll(entities);
            services.GetRequiredService<MainAPP.ViewModels.DeviceManagerViewModel>().SyncAuditBaseline();
            Log.Information("Remote 设备配置已从采集服务加载：{Count} 台", entities.Count);
        }
        catch (Exception ex)
        {
            deviceRepo.ReplaceAll([]);
            Log.Warning(ex, "远程设备配置加载失败，不使用本地缓存，设备配置保持不可用");
        }

        try
        {
            var remoteRecipes = await client.GetRecipesAsync();
            if (remoteRecipes.Count > 0)
            {
                recipeStore.ReplaceAll(RecipeMapper.ToEntities(remoteRecipes));
                Log.Information("Remote 配方已从采集服务加载：{Count} 条", remoteRecipes.Count);
            }
            else
            {
                recipeStore.ReplaceAll([]);
                Log.Information("Remote 配方为空，保持空配方库，不使用本地缓存");
            }
        }
        catch (Exception ex)
        {
            recipeStore.ReplaceAll([]);
            Log.Warning(ex, "远程配方加载失败，不使用本地缓存，配方库保持不可用");
        }

        Log.Information("Remote 模式数据链路已建立：{Url}",
            services.GetRequiredService<AppSettings>().CollectorHubUrl);
    }

    /// <summary>
    /// Remote 模式以 Collector 为语言和现场覆盖文件权威源。
    /// MainAPP 本机的覆盖文件只在 Collector 尚未连接时作为启动降级，避免跨主机/账户各自读取造成界面不一致。
    /// </summary>
    public async Task SynchronizeRemoteLocalizationAsync(
        CancellationToken cancellationToken = default)
    {
        var client = services.GetRequiredService<KanbanDataClient>();
        var operatorName = services.GetService<UserSession>()?.CurrentUserDisplay ?? string.Empty;
        client.OperatorName = operatorName;
        services.GetRequiredService<KanbanAdminClient>().OperatorName = operatorName;
        if (!client.IsConnected)
            await client.ConnectAsync(cancellationToken);
        await SynchronizeRemoteLocalizationAsync(client, cancellationToken);
    }

    private async Task SynchronizeRemoteLocalizationAsync(
        KanbanDataClient client,
        CancellationToken cancellationToken)
    {
        var languageCode = string.Empty;
        try
        {
            languageCode = await client.GetLanguageCodeAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Collector 不支持动态语言接口，尝试读取兼容语言序号");
            var legacyIndex = await client.GetLanguageAsync(cancellationToken);
            languageCode = Enum.IsDefined(typeof(AppLanguage), legacyIndex)
                ? AppSettings.LegacyLanguageCode((AppLanguage)legacyIndex)
                : LocalizationCatalog.DefaultLanguage;
        }

        languageCode = LocalizationCatalog.Normalize(languageCode);
        var settings = services.GetRequiredService<AppSettings>();
        settings.LanguageCode = languageCode;
        MainAPP.Services.Localization.Apply(languageCode);

        try
        {
            var remoteOverrides = await client.GetLocalizationOverridesAsync(cancellationToken);
            LocalizationOverrideStore.Replace(remoteOverrides.Select(item =>
                new LocalizationOverrideEntry(item.Resource, item.Key, item.CultureName, item.Value)));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "读取 Collector 本地化覆盖失败，保留本机启动覆盖");
        }

        ConnectionStatusMessages.ApplyLanguage(languageCode);
        ValidationMessages.ApplyLanguage(languageCode);
        RecipeValidationMessages.ApplyLanguage(languageCode);
        Log.Information("已同步 Collector 权威本地化：语言 {Language}", languageCode);
    }

    private async Task SynchronizeRemoteLocalizationSafeAsync(KanbanDataClient client)
    {
        try
        {
            await SynchronizeRemoteLocalizationAsync(client, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // 重连期间应用退出时，忽略正常取消。
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Collector 重连后同步本地化失败，将等待下一次重连");
        }
    }

    private void StartRetryLoop(
        KanbanDataClient client,
        Action onConnected,
        CancellationToken cancellationToken)
    {
        lock (_retryGate)
        {
            if (_retryLoopStarted) return;
            _retryLoopStarted = true;
        }

        var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdownCts.Token);
        _retryTask = Task.Run(async () =>
        {
            try
            {
                while (!linked.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), linked.Token);
                        await client.ConnectAsync(linked.Token);
                        await SynchronizeRemoteLocalizationAsync(client, linked.Token);
                        Log.Information("采集服务后台重连成功，启动数据同步");
                        onConnected();
                        return;
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "采集服务后台重连失败，5s 后重试");
                    }
                }
            }
            finally
            {
                linked.Dispose();
            }
        });
    }

    private void StartAdminRetryLoop(KanbanAdminClient client, CancellationToken cancellationToken)
    {
        lock (_retryGate)
        {
            if (_adminRetryLoopStarted) return;
            _adminRetryLoopStarted = true;
        }

        var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdownCts.Token);
        _adminRetryTask = Task.Run(async () =>
        {
            try
            {
                while (!linked.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), linked.Token);
                        await client.ConnectAsync(linked.Token);
                        Log.Information("采集管理服务后台重连成功，远程管理写入已恢复");
                        return;
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "采集管理服务后台重连失败，5s 后重试");
                    }
                }
            }
            finally
            {
                linked.Dispose();
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            return;

        _shutdownCts.Cancel();
        if (_retryTask is not null)
        {
            try { await _retryTask.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { Log.Warning("远程重连任务停止超时"); }
        }
        if (_adminRetryTask is not null)
        {
            try { await _adminRetryTask.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { Log.Warning("远程管理重连任务停止超时"); }
        }
        _shutdownCts.Dispose();
    }
}
