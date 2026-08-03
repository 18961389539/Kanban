using Kanban.Contracts.Dtos;
using Kanban.Contracts.Enums;
using Kanban.Core.Data;
using Kanban.Core.Entities;
using Kanban.Core.Mapping;
using Kanban.Core.Models;
using Kanban.Core.Services;
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
    private readonly ILogger<ConfigSyncHandler> _logger;

    public ConfigSyncHandler(
        DeviceRepository deviceRepository,
        WorkOrderRepository workOrderRepository,
        SnapshotAggregator snapshotAggregator,
        AppSettings appSettings,
        ILogger<ConfigSyncHandler> logger)
    {
        _deviceRepository = deviceRepository;
        _workOrderRepository = workOrderRepository;
        _snapshotAggregator = snapshotAggregator;
        _appSettings = appSettings;
        _logger = logger;
    }

    /// <summary>整体替换设备配置并落盘（对齐 DeviceRepository.ReplaceAll + SaveAll 语义）。
    /// 被删除的设备同步从快照聚合器裁剪并广播 tombstone，展示端据此移除（否则屏端永远残留"死设备"）。</summary>
    public Task SaveDevicesAsync(IReadOnlyList<DeviceConfigDto> devices)
    {
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
    /// </summary>
    public Task SaveCollectorSettingsAsync(CollectorSettingsDto dto)
    {
        try
        {
            if (dto.PollingIntervalMs.HasValue) _appSettings.PollingIntervalMs = dto.PollingIntervalMs.Value;
            if (dto.HistoryWriteIntervalScans.HasValue) _appSettings.HistoryWriteIntervalScans = dto.HistoryWriteIntervalScans.Value;
            if (dto.PlcBatchReadMaxLength.HasValue) _appSettings.PlcBatchReadMaxLength = dto.PlcBatchReadMaxLength.Value;
            if (dto.PlcBatchReadMaxGapSlots.HasValue) _appSettings.PlcBatchReadMaxGapSlots = dto.PlcBatchReadMaxGapSlots.Value;
            if (dto.PlcBrand.HasValue) _appSettings.PlcConfig.Brand = (PlcBrand)dto.PlcBrand.Value;
            if (!string.IsNullOrWhiteSpace(dto.PlcIpAddress)) _appSettings.PlcConfig.IpAddress = dto.PlcIpAddress;
            if (dto.PlcPort.HasValue) _appSettings.PlcConfig.Port = dto.PlcPort.Value;
            if (dto.PlcTimeoutMs.HasValue) _appSettings.PlcConfig.TimeoutMs = dto.PlcTimeoutMs.Value;
            if (dto.Shifts is { Count: > 0 })
            {
                _appSettings.Shifts.Clear();
                foreach (var s in dto.Shifts)
                {
                    _appSettings.Shifts.Add(new ShiftConfig { Name = s.Name, StartTime = s.StartTime, EndTime = s.EndTime });
                }
            }
            _appSettings.Save(); // 落 Collector 侧 settings.json（合并后全量序列化，UI 字段保留 Collector 已有值）
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
}

