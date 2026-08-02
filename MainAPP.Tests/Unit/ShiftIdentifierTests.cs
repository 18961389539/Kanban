using System;
using MainAPP.Models;
using MainAPP.Services;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// ShiftIdentifier 契约测试。
/// 验证 record 值相等性、GetHashCode、ToString 磁盘格式及 From(ShiftConfig) 工厂行为。
/// </summary>
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class ShiftIdentifierTests
{
    private static readonly TimeSpan Start = new(8, 0, 0);
    private static readonly TimeSpan End = new(20, 0, 0);

    // ──────────── Equals 值相等性 ────────────

    [Fact]
    public void Equals_SameComponents_AreEqual()
    {
        var a = new ShiftIdentifier("白班", Start, End);
        var b = new ShiftIdentifier("白班", Start, End);
        Assert.True(a.Equals(b));
    }

    [Theory]
    [InlineData("夜班", 8, 0, 20, 0, 0)]   // Name 不同
    [InlineData("白班", 7, 0, 20, 0, 0)]   // StartTime 不同
    [InlineData("白班", 8, 0, 21, 0, 0)]   // EndTime 不同
    public void Equals_AnyComponentDifferent_NotEqual(string name, int sh, int sm, int eh, int em, int ss)
    {
        var a = new ShiftIdentifier("白班", Start, End);
        var b = new ShiftIdentifier(name, new TimeSpan(sh, sm, ss), new TimeSpan(eh, em, 0));
        Assert.False(a.Equals(b));
    }

    // ──────────── GetHashCode 相等性 ────────────

    [Fact]
    public void GetHashCode_EqualInstances_SameHash()
    {
        var a = new ShiftIdentifier("白班", Start, End);
        var b = new ShiftIdentifier("白班", Start, End);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    // ──────────── ToString 格式 ────────────

    [Fact]
    public void ToString_Returns_PipeSeparatedFormat()
    {
        // "Name|StartTime|EndTime"，TimeSpan 默认 ToString 形如 "08:00:00"
        var id = new ShiftIdentifier("白班", Start, End);
        Assert.Equal("白班|08:00:00|20:00:00", id.ToString());
    }

    // ──────────── From(ShiftConfig) ────────────

    [Fact]
    public void From_ValidConfig_ConstructsIdentifier()
    {
        var config = new ShiftConfig
        {
            Name = "早班",
            StartTime = new TimeSpan(6, 0, 0),
            EndTime = new TimeSpan(14, 0, 0)
        };

        var id = ShiftIdentifier.From(config);

        Assert.NotNull(id);
        Assert.Equal("早班", id!.Name);
        Assert.Equal(new TimeSpan(6, 0, 0), id.StartTime);
        Assert.Equal(new TimeSpan(14, 0, 0), id.EndTime);
    }

    [Fact]
    public void From_NullConfig_ReturnsNull()
    {
        Assert.Null(ShiftIdentifier.From(null));
    }

    // ──────────── record == / != 运算符 ────────────

    [Fact]
    public void OpEquality_EqualInstances_ReturnsTrue()
    {
        var a = new ShiftIdentifier("白班", Start, End);
        var b = new ShiftIdentifier("白班", Start, End);
        Assert.True(a == b);
    }

    [Fact]
    public void OpInequality_DifferentInstances_ReturnsTrue()
    {
        var a = new ShiftIdentifier("白班", Start, End);
        var b = new ShiftIdentifier("夜班", Start, End);
        Assert.True(a != b);
    }
}
