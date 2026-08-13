using Kanban.Collector.Core.Localization;
using Kanban.Contracts.Dtos;
using Kanban.Contracts.Enums;
using Kanban.Core.Data;
using Kanban.Core.Entities;
using Kanban.Core.Mapping;
using Kanban.Core.Models;
using Kanban.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Reflection;
using WorkOrderStatus = Kanban.Contracts.Enums.WorkOrderStatus;

namespace Kanban.Collector.Services;

/// <summary>
/// 配置同步处理器：Remote 模式下 MainAPP 的设备/工单管理写操作经 SignalR 转发到 Collector，
/// Collector 作为唯一写者落盘/落库，MainAPP 本地不再直接写文件/库，避免双进程写冲突。
/// 采集参数（settings.json 采集子集）同样经本类同步：解决"Remote 模式设置改了采集进程无感知"的配置分裂。
/// </summary>
public sealed class ConfigSyncHandler
{
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
        foreach (var d in devices)
        {
            if (d is null) throw new ArgumentException("设备列表包含 null 元素", nameof(devices));
            if (string.IsNullOrWhiteSpace(d.Id) || string.IsNullOrWhiteSpace(d.Name))
                throw new ArgumentException($"设备 Id/Name 不能为空（Id='{d.Id}' Name='{d.Name}'）", nameof(devices));
            foreach (var a in d.Alarms ?? []) EnsureEnumDefined(a.Level, nameof(a.Level));
            foreach (var x in d.Defects ?? [])
            {
                EnsureEnumDefined(x.Severity, nameof(x.Severity));
                EnsureEnumDefined(x.Category, nameof(x.Category));
            }
        }
        try
        {
            var entities = devices.Select(DeviceMapper.ToEntity).ToList();
            // 替换前记录旧设备 Id，替换后计算差集裁剪
            var oldIds = _deviceRepository.GetDevicesSnapshot().Select(d => d.Id).ToHashSet();
            _deviceRepository.ReplaceAll(entities);
            _deviceRepository.SaveAll();
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
        return Task.CompletedTask;
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
                if (w.Status == Kanban.Core.Entities.WorkOrderStatus.Running)
                {
                    runningByDevice.TryAdd(w.DeviceId, w);
                }
                else if (w.Status == Kanban.Core.Entities.WorkOrderStatus.Pending)
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
    public Task SaveCollectorSettingsAsync(CollectorSettingsDto dto)
    {
        try
        {
            // ① 草稿合并：新建 AppSettings 实例承载候选值（不动运行实例）。
            // 草稿是运行实例的**全字段拷贝**（含 Language/AppTitle/DataMode/UiScale 等非采集字段）——
            // 否则全量序列化落盘时非采集字段被默认值覆盖（Remote 每保存一次采集设置即重置
            // MainAPP 语言/主题/日报配置；详见 AppSettings.CreateDraft 注释）。
            var draft = _appSettings.CreateDraft();

            if (dto.PollingIntervalMs.HasValue) draft.PollingIntervalMs = dto.PollingIntervalMs.Value;
            if (dto.HistoryWriteIntervalScans.HasValue) draft.HistoryWriteIntervalScans = dto.HistoryWriteIntervalScans.Value;
            if (dto.PlcBatchReadMaxLength.HasValue) draft.PlcBatchReadMaxLength = dto.PlcBatchReadMaxLength.Value;
            if (dto.PlcBatchReadMaxGapSlots.HasValue) draft.PlcBatchReadMaxGapSlots = dto.PlcBatchReadMaxGapSlots.Value;

            if (dto.PlcBrand.HasValue)
            {
                if (!Enum.IsDefined(typeof(PlcBrand), dto.PlcBrand.Value))
                    throw new ArgumentOutOfRangeException(nameof(dto.PlcBrand), dto.PlcBrand.Value, "不支持的 PLC 品牌");
                draft.PlcConfig.Brand = (PlcBrand)dto.PlcBrand.Value;
            }
            if (!string.IsNullOrWhiteSpace(dto.PlcIpAddress)) draft.PlcConfig.IpAddress = dto.PlcIpAddress;
            if (dto.PlcPort.HasValue) draft.PlcConfig.Port = dto.PlcPort.Value;
            if (dto.PlcTimeoutMs.HasValue) draft.PlcConfig.TimeoutMs = dto.PlcTimeoutMs.Value;
            if (dto.Siemens is { } siemens)
            {
                if (!string.IsNullOrWhiteSpace(siemens.Model)) draft.PlcConfig.Siemens.Model = siemens.Model;
                if (siemens.Rack.HasValue) draft.PlcConfig.Siemens.Rack = siemens.Rack.Value;
                if (siemens.Slot.HasValue) draft.PlcConfig.Siemens.Slot = siemens.Slot.Value;
                if (siemens.DataFormat.HasValue)
                {
                    if (!Enum.IsDefined(typeof(PlcDataFormat), siemens.DataFormat.Value))
                        throw new ArgumentOutOfRangeException(nameof(dto.Siemens), siemens.DataFormat.Value, "不支持的 Siemens 数据格式");
                    draft.PlcConfig.Siemens.DataFormat = (PlcDataFormat)siemens.DataFormat.Value;
                }
                if (siemens.BatchInt32Limit.HasValue) draft.PlcConfig.Siemens.BatchInt32Limit = siemens.BatchInt32Limit.Value;
            }
            if (dto.ModbusTcp is { } modbus)
            {
                if (modbus.UnitId.HasValue) draft.PlcConfig.ModbusTcp.UnitId = modbus.UnitId.Value;
                if (modbus.AddressStartWithZero.HasValue) draft.PlcConfig.ModbusTcp.AddressStartWithZero = modbus.AddressStartWithZero.Value;
                if (modbus.RegisterFunction.HasValue) draft.PlcConfig.ModbusTcp.RegisterFunction = modbus.RegisterFunction.Value;
                if (modbus.BitFunction.HasValue) draft.PlcConfig.ModbusTcp.BitFunction = modbus.BitFunction.Value;
                if (modbus.DataFormat.HasValue)
                {
                    if (!Enum.IsDefined(typeof(PlcDataFormat), modbus.DataFormat.Value))
                        throw new ArgumentOutOfRangeException(nameof(dto.ModbusTcp), modbus.DataFormat.Value, "不支持的 Modbus 数据格式");
                    draft.PlcConfig.ModbusTcp.DataFormat = (PlcDataFormat)modbus.DataFormat.Value;
                }
                if (modbus.BatchInt32Limit.HasValue) draft.PlcConfig.ModbusTcp.BatchInt32Limit = modbus.BatchInt32Limit.Value;
            }
            if (dto.Omron is { } omron && omron.ReadSplits.HasValue)
                draft.PlcConfig.Omron.ReadSplits = omron.ReadSplits.Value;
            if (dto.Shifts is { Count: > 0 })
            {
                draft.Shifts = [.. dto.Shifts.Select(s => new ShiftConfig { Name = s.Name, StartTime = s.StartTime, EndTime = s.EndTime })];
            }

            // ② 完整校验：与启动期一致的口径（轮询间隔/班次/PLC 参数/批量读取上下限）
            var errors = draft.Validate();
            if (errors.Count > 0)
                throw new InvalidOperationException("采集设置校验失败：" + string.Join("；", errors));

            // ③ 先落盘（草稿全量序列化；失败则运行实例保持原状）
            AppSettings.WriteSettingsFile(draft);

            // ④ 后生效：把草稿值应用到运行实例（此刻磁盘已是新值，崩溃重启也不会回退）
            var plcSignatureBefore = _appSettings.PlcConfig.GetConfigurationSignature();
            _appSettings.PollingIntervalMs = draft.PollingIntervalMs;
            _appSettings.HistoryWriteIntervalScans = draft.HistoryWriteIntervalScans;
            _appSettings.PlcBatchReadMaxLength = draft.PlcBatchReadMaxLength;
            _appSettings.PlcBatchReadMaxGapSlots = draft.PlcBatchReadMaxGapSlots;
            _appSettings.PlcConfig = draft.PlcConfig.CreateSnapshot();
            // 班次集合锁内原地更新：与采集轮询线程/进度查询的锁内快照读取互斥，
            // 消除 Clear+Add 中间窗口被枚举导致的 InvalidOperationException（审查修复 2026-08-13）
            lock (_appSettings.ShiftsLock)
            {
                _appSettings.Shifts.Clear();
                foreach (var s in draft.Shifts) _appSettings.Shifts.Add(s);
            }
            var plcSignatureAfter = _appSettings.PlcConfig.GetConfigurationSignature();

            if (plcSignatureBefore != plcSignatureAfter)
            {
                // 连接参数变化：刷新运行时 Profile（驱动/编解码按新品牌重建），并断开当前连接，
                // 下次采集循环 EnsureConnected 会用新配置重新连接（含品牌变更时 SharedPlcDriverRouter 替换驱动）。
                // 服务延迟解析：避免在 AppSettings.Load 之前构造采集单例（见 Collector 启动顺序约束）。
                try
                {
                    _services.GetService<IPlcRuntimeProfileProvider>()?.Refresh(_appSettings.PlcConfig);
                    _services.GetService<IPlcConnectionManager>()?.Disconnect();
                }
                catch (Exception ex) when (ex is not (OutOfMemoryException or AppDomainUnloadedException or ThreadAbortException))
                {
                    _logger.LogWarning(ex, "PLC 配置变更热切换未完成，将在下次连接时应用新配置");
                }
            }

            _logger.LogInformation("采集设置已从 Remote 端同步：Polling={Poll}ms Shifts={ShiftCount} Plc={Brand}/{Ip}:{Port}",
                _appSettings.PollingIntervalMs, _appSettings.Shifts.Count,
                _appSettings.PlcConfig.Brand, _appSettings.PlcConfig.IpAddress, _appSettings.PlcConfig.Port);
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "采集设置同步失败");
            throw;
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

    /// <summary>界面语言枚举值（Collector 侧 settings.json 的 Language；屏端经 Hub 拉取，零配置）。</summary>
    public int GetLanguage() => (int)_appSettings.Language;

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
    /// </summary>
    public Task<RecipeApplyResultDto> ApplyRecipeAsync(string deviceId, string recipeId,
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

            var result = _recipeApplier.Apply(device, recipe, progress);
            _logger.LogInformation("配方下发 {Recipe} -> {Device}：{Result}{Detail}",
                recipe.Name, device.Name, result.Success ? "成功" : "失败",
                result.Success ? "" : $"（{result.Message}）");
            return Task.FromResult(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "配方下发失败 Device={DeviceId} Recipe={RecipeId}", deviceId, recipeId);
            throw;
        }
    }

    /// <summary>枚举值守卫（审查修复 2026-08-13）：拒绝未定义值，防止强转落库后下游 switch 崩溃。</summary>
    private static void EnsureEnumDefined<TEnum>(TEnum value, string paramName) where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
            throw new ArgumentOutOfRangeException(paramName, value, $"非法的枚举值 {value}（{typeof(TEnum).Name}）");
    }
}

