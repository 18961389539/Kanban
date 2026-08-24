using Kanban.Contracts.Dtos;
using Xunit;

namespace PlcSimulator.Tests.Unit;

/// <summary>
/// DetectAddressConflicts 单元测试：地址冲突检测（含大小写不敏感）。
/// 该方法为 internal，通过 PlcSimulator 的 InternalsVisibleTo 暴露给测试程序集。
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class DetectAddressConflictsTests
{
    [Fact]
    public void NoConflict_ReturnsEmpty()
    {
        var devices = new List<DeviceConfig>
        {
            new() { Name = "A", OkCountAddress = "D100", NgCountAddress = "D102" },
            new() { Name = "B", OkCountAddress = "D200", NgCountAddress = "D202" },
        };
        Assert.Empty(Program.DetectAddressConflicts(devices));
    }

    [Fact]
    public void SharedAddress_ReturnsConflict()
    {
        var devices = new List<DeviceConfig>
        {
            new() { Name = "A", OkCountAddress = "D100" },
            new() { Name = "B", OkCountAddress = "D100" },
        };
        var conflicts = Program.DetectAddressConflicts(devices);
        Assert.Single(conflicts);
        Assert.Contains("D100", conflicts[0]);
    }

    [Fact]
    public void SharedAddress_AcrossFieldTypes_IsCaseInsensitive()
    {
        var devices = new List<DeviceConfig>
        {
            new() { Name = "A", OkCountAddress = "d100" },
            new() { Name = "B", NgCountAddress = "D100" },
        };
        Assert.Single(Program.DetectAddressConflicts(devices));
    }

    [Fact]
    public void SourceAddresses_AreCheckedAcrossDevices_ButNotWithinOneDevice()
    {
        var devices = new List<DeviceConfig>
        {
            new()
            {
                Id = "A",
                Name = "A",
                Sources = new List<DataSourceConfigDto>
                {
                    new()
                    {
                        Name = "触发源",
                        TriggerAddress = "D510",
                        Values = new List<DataSourceValueConfigDto>
                        {
                            new() { Name = "温度", PlcAddress = "D500" },
                        },
                    },
                    new()
                    {
                        Name = "重复值源",
                        Values = new List<DataSourceValueConfigDto>
                        {
                            new() { Name = "同址值", PlcAddress = "D500" },
                        },
                    },
                },
            },
            new()
            {
                Id = "B",
                Name = "B",
                Sources = new List<DataSourceConfigDto>
                {
                    new()
                    {
                        Name = "另一触发源",
                        TriggerAddress = "D510",
                        Values = new List<DataSourceValueConfigDto>
                        {
                            new() { Name = "另一值", PlcAddress = "D500" },
                        },
                    },
                },
            },
        };

        var conflicts = Program.DetectAddressConflicts(devices);

        Assert.Equal(2, conflicts.Count);
        Assert.Contains(conflicts, conflict => conflict.Contains("D500"));
        Assert.Contains(conflicts, conflict => conflict.Contains("D510"));
    }
}
