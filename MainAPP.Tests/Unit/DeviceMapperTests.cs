using Kanban.Collector.Core.Mapping;
using Kanban.Collector.Core.Models;
using Xunit;

namespace MainAPP.Tests.Unit;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public sealed class DeviceMapperTests
{
    [Fact]
    public void ToDtoAndToEntity_PreservesConnectionProfileId()
    {
        var source = new Device
        {
            Id = "device-1",
            Name = "设备 1",
            ConnectionProfileId = "line-2",
        };

        var dto = DeviceMapper.ToDto(source);
        var restored = DeviceMapper.ToEntity(dto);

        Assert.Equal("line-2", dto.ConnectionProfileId);
        Assert.Equal("line-2", restored.ConnectionProfileId);
    }
}