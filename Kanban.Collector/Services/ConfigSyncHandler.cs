using Kanban.Contracts.Dtos;
using Kanban.Contracts.Enums;
using Kanban.Core.Data;
using Kanban.Core.Entities;
using Kanban.Core.Models;
using Kanban.Core.Entities;
using Kanban.Core.Models;
using Microsoft.Extensions.Logging;
using AlarmLevel = Kanban.Contracts.Enums.AlarmLevel;
using DefectSeverity = Kanban.Contracts.Enums.DefectSeverity;
using DefectCategory = Kanban.Contracts.Enums.DefectCategory;
using WorkOrderStatus = Kanban.Contracts.Enums.WorkOrderStatus;

namespace Kanban.Collector.Services;

/// <summary>
/// 配置同步处理器：Remote 模式下 MainAPP 的设备/工单管理写操作经 SignalR 转发到 Collector，
/// Collector 作为唯一写者落盘/落库，MainAPP 本地不再直接写文件/库，避免双进程写冲突。
/// </summary>
public sealed class ConfigSyncHandler
{
    private readonly DeviceRepository _deviceRepository;
    private readonly WorkOrderRepository _workOrderRepository;
    private readonly SnapshotAggregator _snapshotAggregator;
    private readonly ILogger<ConfigSyncHandler> _logger;

    public ConfigSyncHandler(
        DeviceRepository deviceRepository,
        WorkOrderRepository workOrderRepository,
        SnapshotAggregator snapshotAggregator,
        ILogger<ConfigSyncHandler> logger)
    {
        _deviceRepository = deviceRepository;
        _workOrderRepository = workOrderRepository;
        _snapshotAggregator = snapshotAggregator;
        _logger = logger;
    }

    /// <summary>整体替换设备配置并落盘（对齐 DeviceRepository.ReplaceAll + SaveAll 语义）。
    /// 被删除的设备同步从快照聚合器裁剪并广播 tombstone，展示端据此移除（否则屏端永远残留"死设备"）。</summary>
    public Task SaveDevicesAsync(IReadOnlyList<DeviceConfigDto> devices)
    {
        try
        {
            var entities = devices.Select(ToDevice).ToList();
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
            return Task.FromResult<IReadOnlyList<DeviceConfigDto>>(devices.Select(ToDto).ToList());
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
            var entity = ToWorkOrder(dto);
            var saved = _workOrderRepository.Upsert(entity);
            _logger.LogInformation("Remote 工单落库 Id={Id} OrderNo={OrderNo} Status={Status}",
                saved.Id, saved.OrderNo, saved.Status);
            return Task.FromResult(ToDto(saved));
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
            return Task.FromResult(workOrder is null ? null : ToDto(workOrder));
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
    public IReadOnlyList<DeviceWorkOrderDto> GetWorkOrderSnapshot()
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
                    WorkOrder = workOrder is null ? null : ToDto(workOrder),
                });
            }
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "工单快照构建失败");
            return [];
        }
    }

    // ──────────── DTO → 实体 ────────────

    private static Device ToDevice(DeviceConfigDto dto)
    {
        var device = new Device
        {
            Id = dto.Id,
            Name = dto.Name,
            OkCountAddress = dto.OkCountAddress,
            NgCountAddress = dto.NgCountAddress,
            StatusCountAddress = dto.StatusCountAddress,
            ProductionResetAddress = dto.ProductionResetAddress,
            RecipeName = dto.RecipeName,
            RecipeValue = dto.RecipeValue,
            RecipeAddress = dto.RecipeAddress,
            TargetCycle = dto.TargetCycle,
        };
        foreach (var a in dto.Alarms)
        {
            device.Alarms.Add(new Alarm
            {
                Id = a.Id,
                DeviceId = a.DeviceId,
                Name = a.Name,
                PlcAddress = a.PlcAddress,
                Description = a.Description,
                Level = (Kanban.Core.Models.AlarmLevel)a.Level,
            });
        }
        foreach (var d in dto.Defects)
        {
            device.Defects.Add(new Defect
            {
                Id = d.Id,
                DeviceId = d.DeviceId,
                Name = d.Name,
                PlcAddress = d.PlcAddress,
                Severity = (Kanban.Core.Models.DefectSeverity)d.Severity,
                Category = (Kanban.Core.Models.DefectCategory)d.Category,
            });
        }
        foreach (var c in dto.CountAlarms)
        {
            device.CountAlarms.Add(new CountAlarm
            {
                Id = c.Id,
                DeviceId = c.DeviceId,
                Name = c.Name,
                PlcAddress = c.PlcAddress,
                MaxValue = c.MaxValue,
                Enabled = c.Enabled,
                Description = c.Description,
                Unit = c.Unit,
            });
        }
        return device;
    }

    private static WorkOrder ToWorkOrder(WorkOrderDto dto) => new()
    {
        Id = dto.Id,
        OrderNo = dto.OrderNo,
        ProductCode = dto.ProductCode,
        ProductName = dto.ProductName,
        DeviceId = dto.DeviceId,
        DeviceName = dto.DeviceName,
        TargetQuantity = dto.TargetQuantity,
        PlannedStart = dto.PlannedStart,
        PlannedEnd = dto.PlannedEnd,
        Status = (Kanban.Core.Entities.WorkOrderStatus)dto.Status,
        CompletedOkCount = dto.CompletedOkCount,
        CompletedNgCount = dto.CompletedNgCount,
        Remark = dto.Remark,
        CreatedAt = dto.CreatedAt,
        UpdatedAt = dto.UpdatedAt,
    };

    // ──────────── 实体 → DTO ────────────

    private static WorkOrderDto ToDto(WorkOrder e) => new()
    {
        Id = e.Id,
        OrderNo = e.OrderNo,
        ProductCode = e.ProductCode,
        ProductName = e.ProductName,
        DeviceId = e.DeviceId,
        DeviceName = e.DeviceName,
        TargetQuantity = e.TargetQuantity,
        PlannedStart = e.PlannedStart,
        PlannedEnd = e.PlannedEnd,
        Status = (WorkOrderStatus)e.Status,
        CompletedOkCount = e.CompletedOkCount,
        CompletedNgCount = e.CompletedNgCount,
        Remark = e.Remark,
        CreatedAt = e.CreatedAt,
        UpdatedAt = e.UpdatedAt,
    };

    // ──────────── 实体 → DTO ────────────

    private static DeviceConfigDto ToDto(Device device) => new()
    {
        Id = device.Id,
        Name = device.Name,
        OkCountAddress = device.OkCountAddress,
        NgCountAddress = device.NgCountAddress,
        StatusCountAddress = device.StatusCountAddress,
        ProductionResetAddress = device.ProductionResetAddress,
        RecipeName = device.RecipeName,
        RecipeValue = device.RecipeValue,
        RecipeAddress = device.RecipeAddress,
        TargetCycle = device.TargetCycle,
        Alarms = device.Alarms.Select(a => new AlarmConfigDto
        {
            Id = a.Id,
            DeviceId = a.DeviceId,
            Name = a.Name,
            PlcAddress = a.PlcAddress,
            Description = a.Description,
            Level = (AlarmLevel)a.Level,
        }).ToList(),
        Defects = device.Defects.Select(d => new DefectConfigDto
        {
            Id = d.Id,
            DeviceId = d.DeviceId,
            Name = d.Name,
            PlcAddress = d.PlcAddress,
            Severity = (DefectSeverity)d.Severity,
            Category = (DefectCategory)d.Category,
        }).ToList(),
        CountAlarms = device.CountAlarms.Select(c => new CountAlarmConfigDto
        {
            Id = c.Id,
            DeviceId = c.DeviceId,
            Name = c.Name,
            PlcAddress = c.PlcAddress,
            MaxValue = c.MaxValue,
            Enabled = c.Enabled,
            Description = c.Description,
            Unit = c.Unit,
        }).ToList(),
    };
}
