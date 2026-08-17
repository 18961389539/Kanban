using Xunit;

namespace PlcSimulator.Tests.Unit;

/// <summary>
/// ScenarioConfig 单元测试：覆盖 Get 回退语义与 6 个内置预设的合法性不变量。
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class ScenarioConfigTests
{
    [Fact]
    public void Get_Null_ReturnsNormal()
    {
        var s = ScenarioConfig.Get(null);
        Assert.Equal("normal", s.Name);
    }

    [Fact]
    public void Get_UnknownName_ReturnsNormal()
    {
        var s = ScenarioConfig.Get("definitely-not-a-scenario");
        Assert.Equal("normal", s.Name);
    }

    [Fact]
    public void Get_KnownName_ReturnsThatPreset()
    {
        var s = ScenarioConfig.Get("stress");
        Assert.Equal("stress", s.Name);
    }

    [Fact]
    public void Get_KnownName_IsCaseInsensitive()
    {
        var s = ScenarioConfig.Get("STRESS");
        Assert.Equal("stress", s.Name);
    }

    [Fact]
    public void AllPresets_AreValid()
    {
        foreach (var (name, preset) in ScenarioConfig.Presets)
        {
            var errors = preset.Validate();
            Assert.True(errors.Count == 0, $"场景 {name} 校验失败：{string.Join("; ", errors)}");
        }
    }
}
