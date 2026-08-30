using Kanban.Collector.Core.Localization;
using Kanban.Collector.Hubs;
using Kanban.Contracts.Abstractions;
using Kanban.Contracts.Dtos;
using Kanban.Contracts.Enums;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Mapping;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.SignalR;
using System.Reflection;
using WorkOrderStatus = Kanban.Contracts.Enums.WorkOrderStatus;
using ContractDataSourceValueType = Kanban.Contracts.Enums.DataSourceValueType;

namespace Kanban.Collector.Services;

/// <summary>
/// 配置同步处理器：Remote 模式下 MainAPP 的设备/工单管理写操作经 SignalR 转发到 Collector，
/// Collector 作为唯一写者落盘/落库，MainAPP 本地不再直接写文件/库，避免双进程写冲突。
/// 采集参数（settings.json 采集子集）同样经本类同步：解决"Remote 模式设置改了采集进程无感知"的配置分裂。
/// </summary>
public sealed class ConfigSyncHandler
{
    private readonly object _deviceConfigLock = new();
    private readonly DeviceRepository _deviceRepository;
    private readonly WorkOrderRepository _workOrderRepository;
    private readonly SnapshotAggregator _snapshotAggregator;
    private readonly AppSettings _appSettings;
    private readonly IServiceProvider _services;
    private readonly ILogger<ConfigSyncHandler> _logger;
    private readonly IRecipeStore _recipeStore;
    private readonly RecipeApplier _recipeApplier;

    public ConfigSyncHandler(
        DeviceRepository deviceRepository,
        WorkOrderRepository workOrderRepository,
        SnapshotAggregator snapshotAggregator,
        AppSettings appSettings,
        IServiceProvider services,
        ILogger<ConfigSyncHandler> logger,
        IRecipeStore recipeStore,
        RecipeApplier recipeApplier)
    {
        _deviceRepository = deviceRepository;
        _workOrderRepository = workOrderRepository;
        _snapshotAggregator = snapshotAggregator;
        _appSettings = appSettings;
        _services = services;
        _logger = logger;
        _recipeStore = recipeStore;
        _recipeApplier = recipeApplier;
    }

    /// <summary>整体替换设备配置并落盘（对齐 DeviceRepository.ReplaceAll + SaveAll 语义）。
    /// 被删除的设备同步从快照聚合器裁剪并广播 tombstone，展示端据此移除（否则屏端永远残留"死设备"）。</summary>
    public Task SaveDevicesAsync(IReadOnlyList<DeviceConfigDto> devices)
    {
        // 入参校验（审查修复 2026-08-13）：此前零校验——非法枚举值强转落库、null 集合 NRE 变 500。
        ArgumentNullException.ThrowIfNull(devices);
        var deviceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deviceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in devices)
        {
            if (d is null) throw new ArgumentException("设备列表包含 null 元素", nameof(devices));
            if (string.IsNullOrWhiteSpace(d.Id) || string.IsNullOrWhiteSpace(d.Name))
                throw new ArgumentException($"设备 Id/Name 不能为空（Id='{d.Id}' Name='{d.Name}'）", nameof(devices));
            if (!deviceIds.Add(d.Id))
                throw new ArgumentException($"设备 Id 重复：{d.Id}", nameof(devices));
            if (!deviceNames.Add(d.Name.Trim()))
                throw new ArgumentException($"设备名称重复：{d.Name}", nameof(devices));
            if (d.TargetCycle <= 0)
                throw new ArgumentOutOfRangeException(nameof(d.TargetCycle), d.TargetCycle,
                    $"设备「{d.Name}」目标周期必须大于 0");
            foreach (var a in d.Alarms ?? [])
                if (a is not null) EnsureEnumDefined(a.Level, nameof(a.Level));
            foreach (var x in d.Defects ?? [])
            {
                if (x is null) continue;
                EnsureEnumDefined(x.Severity, nameof(x.Severity));
                EnsureEnumDefined(x.Category, nameof(x.Category));
            }
            ValidateDeviceAddresses(d);
            ValidateDataSources(d);
        }
        ValidateSameDevicePrimaryAddresses(devices);
        ValidateCrossDeviceAddresses(devices);
        lock (_deviceConfigLock)
        {
            try
            {
                var entities = devices.Select(DeviceMapper.ToEntity).ToList();
                // 替换前记录旧设备 Id，替换后计算差集裁剪
                var oldIds = _deviceRepository.GetDevicesSnapshot().Select(d => d.Id).ToHashSet();
                _deviceRepository.ReplaceAllAndSave(entities);
                foreach (var removedId in oldIds.Except(entities.Select(e => e.Id)))
                {
                    _snapshotAggregator.RemoveDevice(removedId);
                    _logger.LogInformation("设备已删除并从快照流裁剪：{DeviceId}", removedId);
                }
                _logger.LogInformation("Remote 设备配置同步完成：{Count} 台", entities.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Remote 设备配置同步失败");
                throw;
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// 从 Collector 自有的 devices.json.bak 恢复配置。
    /// 返回 null 表示没有备份，空列表表示合法的空配置；恢复后立即由 Collector 落盘，
    /// 因此 Remote 屏端不需要再次保存，也不会读取自己的本地备份。
    /// </summary>
    public Task<IReadOnlyList<DeviceConfigDto>?> RollbackDevicesAsync()
    {
        lock (_deviceConfigLock)
        {
            var backupPath = _deviceRepository.FilePath + ".bak";
            if (!File.Exists(backupPath))
                return Task.FromResult<IReadOnlyList<DeviceConfigDto>?>(null);

            try
            {
                var json = File.ReadAllText(backupPath);
                var restored = _deviceRepository.ImportFromJson(json)
                    ?? throw new InvalidDataException("设备备份为空或格式无效");
                var oldIds = _deviceRepository.GetDevicesSnapshot().Select(d => d.Id).ToHashSet();
                _deviceRepository.ReplaceAllAndSave(restored);

                foreach (var removedId in oldIds.Except(restored.Select(d => d.Id)))
                {
                    _snapshotAggregator.RemoveDevice(removedId);
                    _logger.LogInformation("回滚后设备已从快照流裁剪：{DeviceId}", removedId);
                }

                var result = DeviceMapper.ToDtos(_deviceRepository.GetDevicesSnapshot()).ToList();
                _logger.LogInformation("Remote 设备配置回滚完成：{Count} 台", result.Count);
                return Task.FromResult<IReadOnlyList<DeviceConfigDto>?>(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Remote 设备配置回滚失败");
                throw;
            }
        }
    }

    /// <summary>查询 Collector 自有的设备备份是否存在，供 Remote 回滚按钮显示真实状态。</summary>
    public Task<bool> HasDeviceBackupAsync()
    {
        lock (_deviceConfigLock)
            return Task.FromResult(File.Exists(_deviceRepository.FilePath + ".bak"));
    }

    /// <summary>返回当前设备配置快照（屏端 Remote 模式零配置：设备列表从此拉取，不再依赖本地 devices.json）。</summary>
    public Task<IReadOnlyList<DeviceConfigDto>> GetDevicesAsync()
    {
        try
        {
            var devices = _deviceRepository.GetDevicesSnapshot();
            return Task.FromResult<IReadOnlyList<DeviceConfigDto>>(DeviceMapper.ToDtos(devices).ToList());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Remote 设备配置读取失败");
            throw;
        }
    }

    /// <summary>新增/更新工单并落库，返回带 Id 的落库结果（对齐 WorkOrderRepository.Upsert 语义）。</summary>
    public Task<WorkOrderDto> UpsertWorkOrderAsync(WorkOrderDto dto)
    {
        // 入参校验（审查修复 2026-08-13）：非法 Status 强转落库、负 Id 走漂移分支、空 OrderNo/DeviceId 落库后 UI NRE。
        ArgumentNullException.ThrowIfNull(dto);
        if (dto.Id < 0) throw new ArgumentOutOfRangeException(nameof(dto), dto.Id, "工单 Id 不能为负");
        if (string.IsNullOrWhiteSpace(dto.OrderNo) || string.IsNullOrWhiteSpace(dto.DeviceId))
            throw new ArgumentException($"工单 OrderNo/DeviceId 不能为空（OrderNo='{dto.OrderNo}' DeviceId='{dto.DeviceId}'）", nameof(dto));
        EnsureEnumDefined(dto.Status, nameof(dto.Status));
        try
        {
            var entity = WorkOrderMapper.ToEntity(dto);
            var saved = _workOrderRepository.Upsert(entity);
            _logger.LogInformation("Remote 工单落库 Id={Id} OrderNo={OrderNo} Status={Status}",
                saved.Id, saved.OrderNo, saved.Status);
            return Task.FromResult(WorkOrderMapper.ToDto(saved));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Remote 工单落库失败 OrderNo={OrderNo}", dto.OrderNo);
            throw;
        }
    }

    /// <summary>删除工单（对齐 WorkOrderRepository.Delete 语义）。</summary>
    public Task DeleteWorkOrderAsync(int workOrderId)
    {
        if (workOrderId <= 0)
            throw new ArgumentOutOfRangeException(nameof(workOrderId), workOrderId, "工单 Id 必须为正数");
        try
        {
            _workOrderRepository.Delete(workOrderId);
            _logger.LogInformation("Remote 工单删除 Id={Id}", workOrderId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Remote 工单删除失败 Id={Id}", workOrderId);
            throw;
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// 查询设备当前工单（Running 优先，无则回退最新 Pending；无工单返回 null）。
    /// 供展示端（WPF/WASM）顶栏工单条/工单卡使用。
    /// </summary>
    public Task<WorkOrderDto?> GetCurrentWorkOrderAsync(string deviceId)
    {
        try
        {
            var running = _workOrderRepository.GetRunningByDevice(deviceId);
            var workOrder = running ?? _workOrderRepository.GetLatestPendingByDevice(deviceId);
            return Task.FromResult(workOrder is null ? null : WorkOrderMapper.ToDto(workOrder));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Remote 当前工单查询失败 Device={DeviceId}", deviceId);
            throw;
        }
    }

    /// <summary>
    /// 全部设备的当前工单快照（低频元数据推送用，MetaPublisher 每 5s 调用一次）。
    /// Running 优先、回退最新 Pending；无工单设备 WorkOrder=null。
    /// 单次遍历内存集合完成全部设备聚合（O(工单数)），避免逐设备线性扫描（O(设备数×工单数)）。
    /// </summary>
    public long WorkOrderChangeVersion => _workOrderRepository.ChangeVersion;

    public IReadOnlyList<DeviceWorkOrderDto>? GetWorkOrderSnapshot()
    {
        try
        {
            var devices = _deviceRepository.GetDevicesSnapshot();
            if (devices.Count == 0) return [];

            var snapshot = _workOrderRepository.GetSnapshot(); // 锁内拷贝一次
            // Running：同设备最多 1 条（由业务保证），直接按设备建索引
            var runningByDevice = new Dictionary<string, WorkOrder>();
            // Pending：按设备取 PlannedStart 最早的（对齐 GetLatestPendingByDevice 语义）
            var pendingByDevice = new Dictionary<string, WorkOrder>();
            foreach (var w in snapshot)
            {
                if (w.Status == Kanban.Collector.Core.Entities.WorkOrderStatus.Running)
                {
                    runningByDevice.TryAdd(w.DeviceId, w);
                }
                else if (w.Status == Kanban.Collector.Core.Entities.WorkOrderStatus.Pending)
                {
                    if (!pendingByDevice.TryGetValue(w.DeviceId, out var current) || w.PlannedStart < current.PlannedStart)
                        pendingByDevice[w.DeviceId] = w;
                }
            }

            var result = new List<DeviceWorkOrderDto>(devices.Count);
            foreach (var device in devices)
            {
                var workOrder = runningByDevice.TryGetValue(device.Id, out var running)
                    ? running
                    : pendingByDevice.TryGetValue(device.Id, out var pending) ? pending : null;
                result.Add(new DeviceWorkOrderDto
                {
                    DeviceId = device.Id,
                    WorkOrder = workOrder is null ? null : WorkOrderMapper.ToDto(workOrder),
                });
            }
            return result;
        }
        catch (Exception ex)
        {
            // 返回 null 表示构建失败（区别于"无工单"的空列表）——MetaPublisher 据此跳过本次推送，
            // 客户端保留上次数据而非收到"暂无工单"假空态；下次 5s 自动重试
            _logger.LogError(ex, "工单快照构建失败");
            return null;
        }
    }

    /// <summary>
    /// 采集设置同步（Remote 模式 MainAPP 设置页保存时调用）：把采集相关参数合并进 Collector 的
    /// AppSettings 实例（**热生效**——轮询循环每次迭代读 PollingIntervalMs，班次每次切换读 Shifts），
    /// 随后整体落盘 Collector 侧 settings.json。只更新 DTO 中非空字段（部分更新语义）。
    ///
    /// 事务式更新：先在**草稿实例**上合并并执行完整 <see cref="AppSettings.Validate"/>，
    /// 校验通过后先原子落盘草稿，再把值应用到运行实例——磁盘写入失败或校验失败时
    /// 运行实例与 PLC 驱动均保持原状（不再出现"内存已变、磁盘未变"的分裂状态）。
    /// </summary>
    public async Task SaveCollectorSettingsAsync(CollectorSettingsDto dto)
    {
        try
        {
            // ① 草稿合并：新建 AppSettings 实例承载候选值（不动运行实例）。
            // 草稿是运行实例的**全字段拷贝**（含 Language/AppTitle/DataMode/UiScale 等非采集字段）——
            // 否则全量序列化落盘时非采集字段被默认值覆盖（Remote 每保存一次采集设置即重置
            // MainAPP 语言/主题/日报配置；详见 AppSettings.CreateDraft 注释）。
            var draft = _appSettings.CreateDraft();

            CollectorSettingsMapper.ApplyPatch(dto, draft);

            if (dto.Shifts is { Count: > 0 })
            {
                // 复用与本地编辑一致的 1440 分钟全覆盖校验（审查修复 2026-08-15）：
                // 此前只走 draft.Validate()（仅名称非空/起止不等），重叠或留空隙的班次可经 Remote 写入，
                // 导致 ShiftContext.DetectChange 首个匹配命中、产量归属错乱。
                var shiftError = ShiftValidator.Validate(draft.Shifts);
                if (shiftError != null)
                    throw new InvalidOperationException("班次配置校验失败：" + shiftError);
            }

            // ② 完整校验：与启动期一致的口径（轮询间隔/班次/PLC 参数/批量读取上下限）
            var errors = draft.Validate();
            if (errors.Count > 0)
                throw new InvalidOperationException("采集设置校验失败：" + string.Join("；", errors));

            // ③ 先落盘（草稿全量序列化；失败则运行实例保持原状）
            AppSettings.WriteSettingsFile(draft);

            // ④ 后生效：把草稿值应用到运行实例（此刻磁盘已是新值，崩溃重启也不会回退）
            var plcSignatureBefore = _appSettings.PlcConfig.GetConfigurationSignature();
            var profileSignaturesBefore = _appSettings.CreateConnectionProfilesSnapshot()
                .ToDictionary(profile => profile.Id, profile => profile.Config.GetConfigurationSignature(), StringComparer.OrdinalIgnoreCase);
            _appSettings.PollingIntervalMs = draft.PollingIntervalMs;
            _appSettings.HistoryWriteIntervalScans = draft.HistoryWriteIntervalScans;
            _appSettings.PlcBatchReadMaxLength = draft.PlcBatchReadMaxLength;
            _appSettings.PlcBatchReadMaxGapSlots = draft.PlcBatchReadMaxGapSlots;
            _appSettings.LanguageCode = draft.EffectiveLanguageCode;
            _appSettings.PlcConfig = draft.PlcConfig.CreateSnapshot();
            _appSettings.ConnectionProfiles = draft.CreateConnectionProfilesSnapshot();
            _appSettings.EnsureConnectionProfiles();
            // 班次集合锁内原地更新：与采集轮询线程/进度查询的锁内快照读取互斥，
            // 消除 Clear+Add 中间窗口被枚举导致的 InvalidOperationException（审查修复 2026-08-13）
            lock (_appSettings.ShiftsLock)
            {
                _appSettings.Shifts.Clear();
                foreach (var s in draft.Shifts) _appSettings.Shifts.Add(s);
            }
            var plcSignatureAfter = _appSettings.PlcConfig.GetConfigurationSignature();
            var profileSignaturesAfter = _appSettings.CreateConnectionProfilesSnapshot()
                .ToDictionary(profile => profile.Id, profile => profile.Config.GetConfigurationSignature(), StringComparer.OrdinalIgnoreCase);
            var changedProfileIds = profileSignaturesBefore
                .Where(pair => !profileSignaturesAfter.TryGetValue(pair.Key, out var signature)
                               || !string.Equals(pair.Value, signature, StringComparison.Ordinal))
                .Select(pair => pair.Key)
                .Concat(profileSignaturesAfter.Keys.Except(profileSignaturesBefore.Keys, StringComparer.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (changedProfileIds.Count > 0)
            {
                // 连接档案变化：只刷新受影响的 keyed session；删除的档案由 manager 释放，
                // 新增的档案只创建未连接的 session。服务延迟解析，避免在 AppSettings.Load
                // 之前构造采集单例（见 Collector 启动顺序约束）。
                try
                {
                    var runtimeSessions = _services.GetService<IPlcRuntimeSessionManager>();
                    if (runtimeSessions is not null)
                    {
                        runtimeSessions.RefreshFromSettings();
                    }
                    else if (plcSignatureBefore != plcSignatureAfter)
                    {
                        _services.GetService<IPlcRuntimeProfileProvider>()?.Refresh(_appSettings.PlcConfig);
                        _services.GetService<IPlcConnectionManager>()?.Disconnect();
                    }
                }
                catch (Exception ex) when (ex is not (OutOfMemoryException or AppDomainUnloadedException or ThreadAbortException))
                {
                    _logger.LogWarning(ex, "PLC 配置变更热切换未完成，将在下次连接时应用新配置");
                }
            }

            var languageCode = _appSettings.EffectiveLanguageCode;
            ConnectionStatusMessages.ApplyLanguage(languageCode);
            ValidationMessages.ApplyLanguage(languageCode);
            RecipeValidationMessages.ApplyLanguage(languageCode);
            await BroadcastLocalizationChangedAsync(languageCode);

            _logger.LogInformation("采集设置已从 Remote 端同步：Polling={Poll}ms Shifts={ShiftCount} Plc={Brand}/{Ip}:{Port}",
                _appSettings.PollingIntervalMs, _appSettings.Shifts.Count,
                _appSettings.PlcConfig.Brand, _appSettings.PlcConfig.IpAddress, _appSettings.PlcConfig.Port);
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "采集设置同步失败");
            throw;
        }
    }

    private async Task BroadcastLocalizationChangedAsync(string languageCode)
    {
        var hubContext = _services.GetService<IHubContext<KanbanHub, IKanbanHubClient>>();
        if (hubContext is null)
            return;

        try
        {
            var overrides = LocalizationOverrideStore.Snapshot()
                .Select(entry => new LocalizationOverrideDto
                {
                    Resource = entry.Resource,
                    Key = entry.Key,
                    CultureName = entry.CultureName,
                    Value = entry.Value,
                })
                .ToList();
            await hubContext.Clients.All.OnLocalizationChanged(new LocalizationChangedDto
            {
                LanguageCode = languageCode,
                Overrides = overrides,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "本地化变化广播失败，不影响设置保存");
        }
    }

    /// <summary>服务端版本（程序集信息版本，供客户端升级兼容性校验与展示）。</summary>
    public string GetServerVersion()
        => System.Reflection.Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? "unknown";

    /// <summary>看板标题（Collector 侧 settings.json 的 AppTitle；屏端经 Hub 拉取，零配置）。</summary>
    public string GetTitle() => string.IsNullOrWhiteSpace(_appSettings.AppTitle) ? "生产看板" : _appSettings.AppTitle;

    /// <summary>旧版界面语言枚举值；由有效文化代码映射，兼容旧版屏端。</summary>
    public int GetLanguage() => (int)AppSettings.LegacyLanguage(_appSettings.EffectiveLanguageCode);

    /// <summary>界面语言文化代码；新屏端通过此值支持 CSV 中动态增加的语言。</summary>
    public string GetLanguageCode() => _appSettings.EffectiveLanguageCode;

    /// <summary>整体替换配方并落盘（对齐 RecipeStore.ReplaceAll + SaveAll 语义）。</summary>
    public Task SaveRecipesAsync(List<RecipeDto> recipes)
    {
        // 入参校验（审查修复 2026-08-13）：此前走 ReplaceAll 零校验，非法配方（空名/地址不可解析/同机型重名）
        // 可绕过 RecipeStore.LoadAll 的校验直接落库——补上 LoadAll 同口径的 RecipeValidator 校验。
        ArgumentNullException.ThrowIfNull(recipes);
        if (recipes.Any(r => r is null)) throw new ArgumentException("配方列表包含 null 元素", nameof(recipes));
        var entities = recipes.Select(RecipeMapper.ToEntity).ToList();
        var accumulated = _recipeStore.Recipes.ToList();
        var errors = new List<string>();
        foreach (var entity in entities)
        {
            errors.AddRange(RecipeValidator.Validate(entity, accumulated));
            accumulated.Add(entity);
        }
        if (errors.Count > 0)
            throw new InvalidOperationException("配方校验失败：" + string.Join("；", errors));
        try
        {
            _recipeStore.ReplaceAll(entities);
            _recipeStore.SaveAll();
            _logger.LogInformation("Remote 配方同步完成：{Count} 条", entities.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Remote 配方同步失败");
            throw;
        }
        return Task.CompletedTask;
    }

    /// <summary>返回全部配方（Remote 模式 MainAPP 配方管理页从此拉取）。</summary>
    public Task<IReadOnlyList<RecipeDto>> GetRecipesAsync()
    {
        try
        {
            var recipes = _recipeStore.Recipes.ToList();
            return Task.FromResult<IReadOnlyList<RecipeDto>>(RecipeMapper.ToDtos(recipes).ToList());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Remote 配方读取失败");
            throw;
        }
    }

    /// <summary>
    /// 下发配方到指定设备（Remote 模式）：由持有 PLC 连接的 Collector 执行写 PLC + 读回校验 + 回滚。
    /// 设备/配方不存在时抛异常（客户端按 HubException 提示）。
    /// <paramref name="progress"/> 透传给 <see cref="RecipeApplier"/>，Hub 层包装为客户端进度推送。
    /// 执行体经 Task.Run 脱离 Hub 请求线程：RecipeApplier.Apply 是同步 PLC IO（写+读回 N 项，单次最多 5s 超时），
    /// 直接跑在 Hub 线程上会阻塞该连接上的订阅流与其他 Invoke。
    /// </summary>
    public async Task<RecipeApplyResultDto> ApplyRecipeAsync(string deviceId, string recipeId,
        Action<RecipeApplyProgressDto>? progress = null)
    {
        try
        {
            var device = _deviceRepository.GetDeviceById(deviceId);
            if (device is null)
                throw new InvalidOperationException(string.Format(RecipeValidationMessages.RecipeDeviceNotFound, deviceId));
            var recipe = _recipeStore.GetById(recipeId);
            if (recipe is null)
                throw new InvalidOperationException(string.Format(RecipeValidationMessages.RecipeNotFound, recipeId));

            var result = await Task.Run(() => _recipeApplier.Apply(device, recipe, progress)).ConfigureAwait(false);
            _logger.LogInformation("配方下发 {Recipe} -> {Device}：{Result}{Detail}",
                recipe.Name, device.Name, result.Success ? "成功" : "失败",
                result.Success ? "" : $"（{result.Message}）");
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "配方下发失败 Device={DeviceId} Recipe={RecipeId}", deviceId, recipeId);
            throw;
        }
    }

    /// <summary>枚举值守卫（审查修复 2026-08-13）：拒绝未定义值，防止强转落库后下游 switch 崩溃。</summary>
    private void ValidateDeviceAddresses(DeviceConfigDto device)
    {
        EnsureAddressType(device.OkCountAddress, PlcAddressType.DWord,
            $"设备「{device.Name}」OK 计数地址", required: true);
        EnsureAddressType(device.NgCountAddress, PlcAddressType.DWord,
            $"设备「{device.Name}」NG 计数地址", required: true);
        EnsureAddressType(device.StatusCountAddress, PlcAddressType.DWord,
            $"设备「{device.Name}」状态地址", required: true);
        EnsureAddressType(device.ProductionResetAddress, PlcAddressType.DWord,
            $"设备「{device.Name}」产量复位地址", required: true);
        EnsureAddressType(device.RecipeAddress, PlcAddressType.DWord,
            $"设备「{device.Name}」配方地址");

        var alarmNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var alarmIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var alarm in device.Alarms ?? [])
        {
            if (alarm is null) throw new ArgumentException($"设备「{device.Name}」报警列表包含 null 元素");
            ValidateChildIdentity(device, alarm.Id, alarm.DeviceId, alarm.Name, "报警", alarmIds, alarmNames);
            EnsureAddressType(alarm.PlcAddress, PlcAddressType.MBit,
                $"设备「{device.Name}」报警「{alarm.Name}」地址");
        }

        var defectNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var defectIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var defect in device.Defects ?? [])
        {
            if (defect is null) throw new ArgumentException($"设备「{device.Name}」缺陷列表包含 null 元素");
            ValidateChildIdentity(device, defect.Id, defect.DeviceId, defect.Name, "缺陷", defectIds, defectNames);
            EnsureAddressType(defect.PlcAddress, PlcAddressType.DWord,
                $"设备「{device.Name}」缺陷「{defect.Name}」地址");
        }

        var counterAlarmNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var counterAlarmIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var counterAlarm in device.CounterAlarms ?? [])
        {
            if (counterAlarm is null) throw new ArgumentException($"设备「{device.Name}」计数报警列表包含 null 元素");
            ValidateChildIdentity(device, counterAlarm.Id, counterAlarm.DeviceId, counterAlarm.Name, "计数报警", counterAlarmIds, counterAlarmNames);
            EnsureAddressType(counterAlarm.PlcAddress, PlcAddressType.DWord,
                $"设备「{device.Name}」计数报警「{counterAlarm.Name}」地址");
        }
    }

    private static void ValidateChildIdentity(
        DeviceConfigDto device,
        string id,
        string deviceId,
        string name,
        string kind,
        HashSet<string> ids,
        HashSet<string> names)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            throw new ArgumentException($"设备「{device.Name}」{kind} Id/Name 不能为空");
        if (!string.IsNullOrWhiteSpace(deviceId)
            && !string.Equals(deviceId, device.Id, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"设备「{device.Name}」{kind}「{name}」DeviceId 不匹配");
        if (!ids.Add(id))
            throw new ArgumentException($"设备「{device.Name}」{kind} Id 重复：{id}");
        if (!names.Add(name.Trim()))
            throw new ArgumentException($"设备「{device.Name}」{kind} 名称重复：{name}");
    }

    private void ValidateCrossDeviceAddresses(IReadOnlyList<DeviceConfigDto> devices)
    {
        var codec = GetAddressCodec();
        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in devices)
        foreach (var address in EnumerateAddresses(device))
        {
            if (string.IsNullOrWhiteSpace(address)) continue;
            var key = codec.CanonicalKey(address);
            if (string.IsNullOrWhiteSpace(key)) continue;
            if (owners.TryGetValue(key, out var owner)
                && !string.Equals(owner, device.Id, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"设备「{device.Name}」地址「{key}」与设备 Id「{owner}」冲突");
            owners[key] = device.Id;
        }
    }

    private void ValidateSameDevicePrimaryAddresses(IReadOnlyList<DeviceConfigDto> devices)
    {
        var codec = GetAddressCodec();
        foreach (var device in devices)
        {
            var addresses = new[]
            {
                device.OkCountAddress,
                device.NgCountAddress,
                device.StatusCountAddress,
                device.ProductionResetAddress,
                device.RecipeAddress,
            };
            var duplicate = addresses
                .Where(address => !string.IsNullOrWhiteSpace(address))
                .GroupBy(address => codec.CanonicalKey(address!), StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() > 1);
            if (duplicate != null)
                throw new ArgumentException($"设备「{device.Name}」主地址重复：{duplicate.Key}");
        }
    }

    private static IEnumerable<string?> EnumerateAddresses(DeviceConfigDto device)
    {
        yield return device.OkCountAddress;
        yield return device.NgCountAddress;
        yield return device.StatusCountAddress;
        yield return device.ProductionResetAddress;
        yield return device.RecipeAddress;
        foreach (var alarm in device.Alarms ?? []) yield return alarm?.PlcAddress;
        foreach (var defect in device.Defects ?? []) yield return defect?.PlcAddress;
        foreach (var counterAlarm in device.CounterAlarms ?? []) yield return counterAlarm?.PlcAddress;
        foreach (var source in device.Sources ?? [])
        {
            if (source is null) continue;
            yield return source.TriggerAddress;
            foreach (var value in source.Values ?? []) yield return value?.PlcAddress;
        }
    }

    private void ValidateDataSources(DeviceConfigDto device)
    {
        var codec = GetAddressCodec();
        var sourceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sourceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in device.Sources ?? [])
        {
            if (source is null)
                throw new ArgumentException($"设备「{device.Name}」数据源列表包含 null 元素");
            if (string.IsNullOrWhiteSpace(source.Id) || string.IsNullOrWhiteSpace(source.Name))
                throw new ArgumentException($"设备「{device.Name}」数据源 Id/Name 不能为空");
            if (!string.IsNullOrWhiteSpace(source.DeviceId)
                && !string.Equals(source.DeviceId, device.Id, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"设备「{device.Name}」数据源「{source.Name}」DeviceId 不匹配");
            if (!sourceIds.Add(source.Id))
                throw new ArgumentException($"设备「{device.Name}」数据源 Id 重复：{source.Id}");
            if (!sourceNames.Add(source.Name.Trim()))
                throw new ArgumentException($"设备「{device.Name}」数据源名称重复：{source.Name}");
            if (source.Values is null || source.Values.Count == 0)
                throw new ArgumentException($"设备「{device.Name}」数据源「{source.Name}」至少需要一个值项");
            // SN 类型源必须配置触发地址：SN 事件按「触发命中 → 读 SN → 写回执」采集，
            // 无触发地址的源每轮无条件读值（数据监控可见）但不会产生追溯事件，静默失效。
            // 与 PlcScanPipeline.ScanSources 的 source.HasTrigger 约束保持一致（审查修复 2026-08-30）。
            if (string.Equals(source.Type, SnEventConventions.SourceType, StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(source.TriggerAddress))
                throw new ArgumentException($"设备「{device.Name}」数据源「{source.Name}」为 SN 类型，必须配置触发地址（TriggerAddress）才能采集序列号事件");
            if (!string.IsNullOrWhiteSpace(source.TriggerAddress))
            {
                EnsureAddressType(source.TriggerAddress, PlcAddressType.DWord,
                    $"设备「{device.Name}」数据源「{source.Name}」触发地址");
                if (source.TriggerValue == source.AckValue)
                    throw new ArgumentException($"设备「{device.Name}」数据源「{source.Name}」触发值与回执值不能相同");
            }

            var valueIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var valueNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var valueAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in source.Values)
            {
                if (value is null)
                    throw new ArgumentException($"数据源「{source.Name}」值项列表包含 null 元素");
                if (string.IsNullOrWhiteSpace(value.Id) || string.IsNullOrWhiteSpace(value.Name))
                    throw new ArgumentException($"数据源「{source.Name}」值项 Id/Name 不能为空");
                if (!valueIds.Add(value.Id))
                    throw new ArgumentException($"数据源「{source.Name}」值项 Id 重复：{value.Id}");
                if (!valueNames.Add(value.Name.Trim()))
                    throw new ArgumentException($"数据源「{source.Name}」值项名称重复：{value.Name}");
                if (!Enum.IsDefined(typeof(ContractDataSourceValueType), value.DataType))
                    throw new ArgumentOutOfRangeException(nameof(value.DataType), value.DataType,
                        $"数据源「{source.Name}」值项「{value.Name}」DataType 非法");
                if (string.IsNullOrWhiteSpace(value.PlcAddress))
                    throw new ArgumentException($"数据源「{source.Name}」值项「{value.Name}」未配置采集地址");
                var expectedType = value.DataType == ContractDataSourceValueType.Bool
                    ? PlcAddressType.MBit
                    : PlcAddressType.DWord;
                EnsureAddressType(value.PlcAddress, expectedType,
                    $"数据源「{source.Name}」值项「{value.Name}」采集地址");
                var valueAddressKey = codec.CanonicalKey(value.PlcAddress);
                if (!valueAddresses.Add(valueAddressKey))
                    throw new ArgumentException($"数据源「{source.Name}」值项「{value.Name}」采集地址重复：{valueAddressKey}");
                if (!string.IsNullOrWhiteSpace(source.TriggerAddress)
                    && string.Equals(codec.CanonicalKey(source.TriggerAddress), valueAddressKey, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException($"数据源「{source.Name}」值项「{value.Name}」采集地址不能与触发地址相同");
                if (value.StringLength <= 0)
                    throw new ArgumentOutOfRangeException(nameof(value.StringLength), value.StringLength,
                        $"数据源「{source.Name}」值项「{value.Name}」字符串长度必须大于 0");
                if (value.StringLength > 1024)
                    throw new ArgumentOutOfRangeException(nameof(value.StringLength), value.StringLength,
                        $"数据源「{source.Name}」值项「{value.Name}」字符串长度不能超过 1024");
                if (value.ConfirmSeconds < 0 || value.Hysteresis < 0)
                    throw new ArgumentOutOfRangeException(nameof(value.ConfirmSeconds),
                        $"数据源「{source.Name}」值项「{value.Name}」确认时间和滞回不能为负");
                if (value.DataType == ContractDataSourceValueType.Float32
                    && (float.IsNaN(value.FloatLimitMin) || float.IsNaN(value.FloatLimitMax)
                        || float.IsInfinity(value.FloatLimitMin) || float.IsInfinity(value.FloatLimitMax)))
                    throw new ArgumentException($"数据源「{source.Name}」值项「{value.Name}」浮点限值无效");
                var hasLimits = HasConfiguredLimits(value);
                var limitsOrdered = value.DataType == ContractDataSourceValueType.Float32
                    ? value.FloatLimitMax > value.FloatLimitMin
                    : value.LimitMax > value.LimitMin;
                if (hasLimits && !limitsOrdered)
                    throw new ArgumentException($"数据源「{source.Name}」值项「{value.Name}」上限必须大于下限");
                if (HasConfiguredLimits(value) && HasConfiguredExpectedValue(value))
                    throw new ArgumentException($"数据源「{source.Name}」值项「{value.Name}」不能同时配置上下限和预期值");
                var enumValues = value.EnumValues ?? [];
                if (enumValues.Any(e => e is null || string.IsNullOrWhiteSpace(e.DisplayName)))
                    throw new ArgumentException($"数据源「{source.Name}」值项「{value.Name}」枚举映射显示名不能为空");
                if (enumValues.GroupBy(e => e.Value).Any(g => g.Count() > 1))
                    throw new ArgumentException($"数据源「{source.Name}」值项「{value.Name}」枚举映射值重复");
            }
        }
    }

    private static bool HasConfiguredLimits(DataSourceValueConfigDto value)
        => value.DataType == ContractDataSourceValueType.Float32
            ? value.FloatLimitMin != 0 || value.FloatLimitMax != 0
            : value.LimitMin != 0 || value.LimitMax != 0;

    private static bool HasConfiguredExpectedValue(DataSourceValueConfigDto value)
        => value.ExpectedValue.HasValue || value.FloatExpectedValue.HasValue
            || value.BoolExpectedValue.HasValue || value.StringExpectedValue is not null;

    private IPlcAddressCodec GetAddressCodec()
        => _services.GetService<IPlcRuntimeProfileProvider>()?.Current?.AddressCodec
            ?? new MitsubishiAddressCodec();

    private void EnsureAddressType(string? address, PlcAddressType expectedType, string description, bool required = false)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            if (required) throw new ArgumentException($"{description}不能为空");
            return;
        }

        var codec = GetAddressCodec();
        PlcAddressParseResult parsed;
        try
        {
            parsed = codec.Parse(address);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            throw new ArgumentException($"{description}格式无效（需要{expectedType}）：{address}", ex);
        }
        if (!parsed.IsValid || parsed.Type != expectedType)
            throw new ArgumentException($"{description}格式无效（需要{expectedType}）：{address}");
    }

    private static void EnsureEnumDefined<TEnum>(TEnum value, string paramName) where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
            throw new ArgumentOutOfRangeException(paramName, value, $"非法的枚举值 {value}（{typeof(TEnum).Name}）");
    }
}

