using Kanban.Contracts.Dtos;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Mapping;
using Kanban.Collector.Core.Models;
using Xunit;
using AlarmLevel = Kanban.Collector.Core.Models.AlarmLevel;
using DefectSeverity = Kanban.Collector.Core.Models.DefectSeverity;
using DefectCategory = Kanban.Collector.Core.Models.DefectCategory;

namespace MainAPP.Tests.Unit;

/// <summary>
/// 单源映射测试（Kanban.Collector.Core.Mapping）：Device/WorkOrder ↔ DTO 全字段往返。
/// 这是"字段新增只改一处"的单源约定——往返断言保证：
/// - ToDto→ToEntity 不丢字段（漏映射会破坏往返）
/// - 嵌套集合（Alarms/Defects/CounterAlarms）逐字段保持
/// 此前映射类 0% 覆盖（依赖集成测试间接），本文件直接锁住。
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class DeviceWorkOrderMapperTests
{
    // ──────────── DeviceMapper ────────────

    [Fact]
    public void DeviceMapper_RoundTrip_PreservesAllFields()
    {
        var device = new Device
        {
            Id = "dev-001",
            Name = "注塑机-1",
            OkCountAddress = "D100",
            NgCountAddress = "D101",
            StatusCountAddress = "D102",
            ProductionResetAddress = "D103",
            RecipeName = "配方A",
            RecipeValue = 42,
            RecipeAddress = "D104",
            TargetCycle = 3600,
        };
        device.Alarms.Add(new Alarm { Id = "A1", DeviceId = "dev-001", Name = "过温", PlcAddress = "M100", Description = "温度超限", Level = AlarmLevel.High });
        device.Defects.Add(new Defect { Id = "F1", DeviceId = "dev-001", Name = "划痕", PlcAddress = "D200", Severity = DefectSeverity.Major, Category = DefectCategory.Appearance });
        device.CounterAlarms.Add(new CounterAlarm { Id = "C1", DeviceId = "dev-001", Name = "计数超限", PlcAddress = "D300", MaxValue = 100, Enabled = true, Description = "产量上限", Unit = "件" });

        var dto = DeviceMapper.ToDto(device);
        var roundTripped = DeviceMapper.ToEntity(dto);

        Assert.Equal(device.Id, roundTripped.Id);
        Assert.Equal(device.Name, roundTripped.Name);
        Assert.Equal(device.OkCountAddress, roundTripped.OkCountAddress);
        Assert.Equal(device.NgCountAddress, roundTripped.NgCountAddress);
        Assert.Equal(device.StatusCountAddress, roundTripped.StatusCountAddress);
        Assert.Equal(device.ProductionResetAddress, roundTripped.ProductionResetAddress);
        Assert.Equal(device.RecipeName, roundTripped.RecipeName);
        Assert.Equal(device.RecipeValue, roundTripped.RecipeValue);
        Assert.Equal(device.RecipeAddress, roundTripped.RecipeAddress);
        Assert.Equal(device.TargetCycle, roundTripped.TargetCycle);

        // 嵌套集合逐字段
        Assert.Single(roundTripped.Alarms);
        Assert.Equal("过温", roundTripped.Alarms[0].Name);
        Assert.Equal(AlarmLevel.High, roundTripped.Alarms[0].Level);
        Assert.Equal("M100", roundTripped.Alarms[0].PlcAddress);

        Assert.Single(roundTripped.Defects);
        Assert.Equal(DefectSeverity.Major, roundTripped.Defects[0].Severity);
        Assert.Equal(DefectCategory.Appearance, roundTripped.Defects[0].Category);

        Assert.Single(roundTripped.CounterAlarms);
        Assert.Equal(100, roundTripped.CounterAlarms[0].MaxValue);
        Assert.True(roundTripped.CounterAlarms[0].Enabled);
        Assert.Equal("件", roundTripped.CounterAlarms[0].Unit);
    }

    [Fact]
    public void DeviceMapper_ToDtos_PreservesCountAndOrder()
    {
        var devices = new[] { new Device { Id = "a" }, new Device { Id = "b" } };
        var dtos = DeviceMapper.ToDtos(devices);
        Assert.Equal(2, dtos.Count);
        Assert.Equal("a", dtos[0].Id);
    }

    [Fact]
    public void DeviceMapper_RoundTrip_PreservesDataSources()
    {
        var device = new Device { Id = "dev-source", Name = "数据源测试机" };
        var source = new DataSource
        {
            Id = "source-1",
            DeviceId = device.Id,
            Name = "环境源",
            Type = "环境",
            Enabled = false,
            Description = "Remote 数据源",
            TriggerAddress = "D510",
            TriggerValue = 7,
            AckValue = 8,
        };
        source.Values.Add(new DataSourceValue
        {
            Id = "value-int",
            Name = "整数",
            DataType = DataSourceValueType.Int32,
            PlcAddress = "D500",
            Enabled = true,
            LimitMin = 1,
            LimitMax = 9,
            Hysteresis = 2,
            ConfirmSeconds = 3,
            ExpectedValue = 5,
        });
        source.Values.Add(new DataSourceValue
        {
            Id = "value-float",
            Name = "浮点",
            DataType = DataSourceValueType.Float32,
            PlcAddress = "D520",
            FloatLimitMin = 1.5f,
            FloatLimitMax = 9.5f,
            FloatExpectedValue = 5.5f,
        });
        source.Values.Add(new DataSourceValue
        {
            Id = "value-bool",
            Name = "布尔",
            DataType = DataSourceValueType.Bool,
            PlcAddress = "M10",
            BoolExpectedValue = true,
        });
        source.Values.Add(new DataSourceValue
        {
            Id = "value-string",
            Name = "文本",
            DataType = DataSourceValueType.String,
            StringLength = 16,
            PlcAddress = "D540",
            StringExpectedValue = "READY",
        });
        source.Values[0].EnumValues.Add(new DataSourceEnumValue { Value = 5, DisplayName = "正常" });
        device.Sources.Add(source);

        var roundTripped = DeviceMapper.ToEntity(DeviceMapper.ToDto(device));

        var resultSource = Assert.Single(roundTripped.Sources);
        Assert.Equal(source.Id, resultSource.Id);
        Assert.Equal(source.DeviceId, resultSource.DeviceId);
        Assert.Equal(source.Name, resultSource.Name);
        Assert.False(resultSource.Enabled);
        Assert.Equal(source.TriggerAddress, resultSource.TriggerAddress);
        Assert.Equal(source.TriggerValue, resultSource.TriggerValue);
        Assert.Equal(source.AckValue, resultSource.AckValue);
        Assert.Equal(4, resultSource.Values.Count);
        Assert.Equal(DataSourceValueType.Int32, resultSource.Values[0].DataType);
        Assert.Equal(5, resultSource.Values[0].ExpectedValue);
        Assert.Equal("正常", Assert.Single(resultSource.Values[0].EnumValues).DisplayName);
        Assert.Equal(DataSourceValueType.Float32, resultSource.Values[1].DataType);
        Assert.Equal(5.5f, resultSource.Values[1].FloatExpectedValue);
        Assert.Equal(DataSourceValueType.Bool, resultSource.Values[2].DataType);
        Assert.True(resultSource.Values[2].BoolExpectedValue);
        Assert.Equal(DataSourceValueType.String, resultSource.Values[3].DataType);
        Assert.Equal("READY", resultSource.Values[3].StringExpectedValue);
    }

    // ──────────── WorkOrderMapper ────────────

    [Fact]
    public void WorkOrderMapper_RoundTrip_PreservesAllFields()
    {
        var workOrder = new WorkOrder
        {
            Id = 7,
            OrderNo = "WO-20260801-001",
            ProductCode = "P-100",
            ProductName = "外壳",
            DeviceId = "dev-001",
            DeviceName = "注塑机-1",
            TargetQuantity = 500,
            PlannedStart = new DateTime(2026, 8, 1, 8, 0, 0),
            PlannedEnd = new DateTime(2026, 8, 1, 20, 0, 0),
            Status = WorkOrderStatus.Running,
            CompletedOkCount = 123,
            CompletedNgCount = 4,
            Remark = "加急",
            CreatedAt = new DateTime(2026, 8, 1, 7, 30, 0),
            UpdatedAt = new DateTime(2026, 8, 1, 8, 5, 0),
        };

        var dto = WorkOrderMapper.ToDto(workOrder);
        var roundTripped = WorkOrderMapper.ToEntity(dto);

        Assert.Equal(workOrder.Id, roundTripped.Id);
        Assert.Equal(workOrder.OrderNo, roundTripped.OrderNo);
        Assert.Equal(workOrder.ProductCode, roundTripped.ProductCode);
        Assert.Equal(workOrder.ProductName, roundTripped.ProductName);
        Assert.Equal(workOrder.DeviceId, roundTripped.DeviceId);
        Assert.Equal(workOrder.DeviceName, roundTripped.DeviceName);
        Assert.Equal(workOrder.TargetQuantity, roundTripped.TargetQuantity);
        Assert.Equal(workOrder.PlannedStart, roundTripped.PlannedStart);
        Assert.Equal(workOrder.PlannedEnd, roundTripped.PlannedEnd);
        Assert.Equal(WorkOrderStatus.Running, roundTripped.Status);
        Assert.Equal(workOrder.CompletedOkCount, roundTripped.CompletedOkCount);
        Assert.Equal(workOrder.CompletedNgCount, roundTripped.CompletedNgCount);
        Assert.Equal(workOrder.Remark, roundTripped.Remark);
        Assert.Equal(workOrder.CreatedAt, roundTripped.CreatedAt);
        Assert.Equal(workOrder.UpdatedAt, roundTripped.UpdatedAt);
    }

    [Fact]
    public void WorkOrderMapper_DtoToEntity_StatusEnumCast()
    {
        var dto = new WorkOrderDto
        {
            OrderNo = "WO-1", ProductCode = "P-1", ProductName = "产品1",
            DeviceId = "dev-1", DeviceName = "设备1",
            Status = Kanban.Contracts.Enums.WorkOrderStatus.Pending,
        };
        var entity = WorkOrderMapper.ToEntity(dto);
        Assert.Equal(Kanban.Collector.Core.Entities.WorkOrderStatus.Pending, entity.Status);
    }
}
