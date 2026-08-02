using Kanban.Core.Services;
using MainAPP.Services;
using Xunit;

namespace MainAPP.Tests.Unit;


[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class PlcOpResultTests
{
    [Fact]
    public void Record_CarriesStatusMessageAndOptionalReadValue()
    {
        var r = new PlcOpResult(PlcOpStatus.Success, "ok", 42);
        Assert.Equal(PlcOpStatus.Success, r.Status);
        Assert.Equal("ok", r.Message);
        Assert.Equal(42, r.ReadValue);
    }

    [Fact]
    public void Record_ReadValueDefaultsToNull()
    {
        var r = new PlcOpResult(PlcOpStatus.Warning, "warn");
        Assert.Null(r.ReadValue);
    }

    [Theory]
    [InlineData(PlcOpStatus.Success)]
    [InlineData(PlcOpStatus.Info)]
    [InlineData(PlcOpStatus.Warning)]
    [InlineData(PlcOpStatus.Error)]
    [InlineData(PlcOpStatus.Cancelled)]
    public void Status_EnumHasExpectedValues(PlcOpStatus status)
        => Assert.True(System.Enum.IsDefined(typeof(PlcOpStatus), status));
}
