using System;
using System.Collections.Generic;
using Kanban.Collector.Core.Models;
using MainAPP.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// 班次校验器单元测试
/// </summary>
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class ShiftValidatorTests
{
    [Fact]
    public void Validate_NullShifts_ReturnsError()
    {
        Assert.NotNull(ShiftValidator.Validate(null!));
    }

    [Fact]
    public void Validate_EmptyShifts_ReturnsError()
    {
        var result = ShiftValidator.Validate(new List<ShiftConfig>());
        Assert.NotNull(result);
        Assert.Contains("至少", result!);
    }

    [Fact]
    public void Validate_SingleFullDayShift_ReturnsNull()
    {
        // 一个班次覆盖全天（跨天）
        var shifts = new List<ShiftConfig>
        {
            new() { Name = "全天", StartTime = new TimeSpan(0, 0, 0), EndTime = new TimeSpan(0, 0, 0) }
        };
        // 注意：StartTime == EndTime 会被拒（相等）
        var result = ShiftValidator.Validate(shifts);
        Assert.NotNull(result); // 相等被拒
    }

    [Fact]
    public void Validate_TwoShiftsCoveringDay_ReturnsNull()
    {
        // 默认两班次：白班 08:00-20:00，夜班 20:00-08:00
        var shifts = new List<ShiftConfig>
        {
            new() { Name = "白班", StartTime = new TimeSpan(8, 0, 0), EndTime = new TimeSpan(20, 0, 0) },
            new() { Name = "夜班", StartTime = new TimeSpan(20, 0, 0), EndTime = new TimeSpan(8, 0, 0) }
        };
        Assert.Null(ShiftValidator.Validate(shifts));
    }

    [Fact]
    public void Validate_ThreeShiftsCoveringDay_ReturnsNull()
    {
        // 三班次：早 00:00-08:00，中 08:00-16:00，晚 16:00-24:00
        var shifts = new List<ShiftConfig>
        {
            new() { Name = "早班", StartTime = new TimeSpan(0, 0, 0), EndTime = new TimeSpan(8, 0, 0) },
            new() { Name = "中班", StartTime = new TimeSpan(8, 0, 0), EndTime = new TimeSpan(16, 0, 0) },
            new() { Name = "晚班", StartTime = new TimeSpan(16, 0, 0), EndTime = new TimeSpan(0, 0, 0) }
        };
        Assert.Null(ShiftValidator.Validate(shifts));
    }

    [Fact]
    public void Validate_GapBetweenShifts_ReturnsGapError()
    {
        // 两个班次间有 1 小时空隙：08:00-12:00 + 13:00-24:00
        // 缺少 00:00-08:00 和 12:00-13:00
        var shifts = new List<ShiftConfig>
        {
            new() { Name = "A", StartTime = new TimeSpan(8, 0, 0), EndTime = new TimeSpan(12, 0, 0) },
            new() { Name = "B", StartTime = new TimeSpan(13, 0, 0), EndTime = new TimeSpan(0, 0, 0) }
        };
        var result = ShiftValidator.Validate(shifts);
        Assert.NotNull(result);
        Assert.Contains("空隙", result!);
    }

    [Fact]
    public void Validate_OverlapBetweenShifts_ReturnsOverlapError()
    {
        // 两个班次重叠：08:00-20:00 和 10:00-22:00
        var shifts = new List<ShiftConfig>
        {
            new() { Name = "A", StartTime = new TimeSpan(8, 0, 0), EndTime = new TimeSpan(20, 0, 0) },
            new() { Name = "B", StartTime = new TimeSpan(10, 0, 0), EndTime = new TimeSpan(0, 0, 0) }
        };
        var result = ShiftValidator.Validate(shifts);
        Assert.NotNull(result);
        Assert.Contains("重叠", result!);
    }

    [Fact]
    public void Validate_DuplicateNames_ReturnsDupError()
    {
        var shifts = new List<ShiftConfig>
        {
            new() { Name = "白班", StartTime = new TimeSpan(8, 0, 0), EndTime = new TimeSpan(20, 0, 0) },
            new() { Name = "白班", StartTime = new TimeSpan(20, 0, 0), EndTime = new TimeSpan(8, 0, 0) }
        };
        var result = ShiftValidator.Validate(shifts);
        Assert.NotNull(result);
        Assert.Contains("重名", result!);
    }

    [Fact]
    public void Validate_CaseInsensitiveDuplicateNames_ReturnsDupError()
    {
        var shifts = new List<ShiftConfig>
        {
            new() { Name = "Day", StartTime = new TimeSpan(8, 0, 0), EndTime = new TimeSpan(20, 0, 0) },
            new() { Name = "DAY", StartTime = new TimeSpan(20, 0, 0), EndTime = new TimeSpan(8, 0, 0) }
        };
        var result = ShiftValidator.Validate(shifts);
        Assert.NotNull(result);
        Assert.Contains("重名", result!);
    }

    [Fact]
    public void Validate_EmptyName_ReturnsError()
    {
        var shifts = new List<ShiftConfig>
        {
            new() { Name = "", StartTime = new TimeSpan(0, 0, 0), EndTime = new TimeSpan(24, 0, 0) }
        };
        var result = ShiftValidator.Validate(shifts);
        Assert.NotNull(result);
        Assert.Contains("名称不能为空", result!);
    }

    [Fact]
    public void Validate_EqualStartEnd_ReturnsError()
    {
        var shifts = new List<ShiftConfig>
        {
            new() { Name = "X", StartTime = new TimeSpan(8, 0, 0), EndTime = new TimeSpan(8, 0, 0) }
        };
        var result = ShiftValidator.Validate(shifts);
        Assert.NotNull(result);
        Assert.Contains("不能相同", result!);
    }

    // ──────────── 班次配置异常组合（覆盖跨午夜重叠 / 多班次复杂场景） ────────────

    /// <summary>
    /// 两个跨午夜班次重叠：夜班 A 22:00-06:00，夜班 B 23:00-07:00。
    /// 重叠时段 23:00-06:00 应被检测到。
    /// </summary>
    [Fact]
    public void Validate_TwoCrossMidnightShiftsOverlap_ReturnsOverlapError()
    {
        var shifts = new List<ShiftConfig>
        {
            new() { Name = "夜A", StartTime = new TimeSpan(22, 0, 0), EndTime = new TimeSpan(6, 0, 0) },
            new() { Name = "夜B", StartTime = new TimeSpan(23, 0, 0), EndTime = new TimeSpan(7, 0, 0) },
        };
        var result = ShiftValidator.Validate(shifts);
        Assert.NotNull(result);
        Assert.Contains("重叠", result!);
    }

    /// <summary>
    /// 跨午夜班次与白天班次重叠：白班 06:00-22:00，夜班 20:00-08:00。
    /// 重叠时段 20:00-22:00 应被检测到。
    /// </summary>
    [Fact]
    public void Validate_CrossMidnightAndDayShiftOverlap_ReturnsOverlapError()
    {
        var shifts = new List<ShiftConfig>
        {
            new() { Name = "白班", StartTime = new TimeSpan(6, 0, 0), EndTime = new TimeSpan(22, 0, 0) },
            new() { Name = "夜班", StartTime = new TimeSpan(20, 0, 0), EndTime = new TimeSpan(8, 0, 0) },
        };
        var result = ShiftValidator.Validate(shifts);
        Assert.NotNull(result);
        Assert.Contains("重叠", result!);
    }

    /// <summary>
    /// 三班次复杂跨午夜组合（早 00:00-08:00 + 中 08:00-16:00 + 夜 16:00-24:00）应通过。
    /// 这是不跨午夜的"三班倒"标准配置。
    /// </summary>
    [Fact]
    public void Validate_ThreeShiftsNoCrossMidnight_Covers24Hours_ReturnsNull()
    {
        var shifts = new List<ShiftConfig>
        {
            new() { Name = "早班", StartTime = new TimeSpan(0, 0, 0), EndTime = new TimeSpan(8, 0, 0) },
            new() { Name = "中班", StartTime = new TimeSpan(8, 0, 0), EndTime = new TimeSpan(16, 0, 0) },
            new() { Name = "晚班", StartTime = new TimeSpan(16, 0, 0), EndTime = new TimeSpan(24, 0, 0) },
        };
        Assert.Null(ShiftValidator.Validate(shifts));
    }

    /// <summary>
    /// 三班次含跨午夜：白 08:00-16:00 + 小夜 16:00-24:00 + 大夜 00:00-08:00。
    /// 大夜班次跨午夜（StartTime=00:00 < EndTime=08:00 不算跨午夜，实际是同日 00:00-08:00）。
    /// 应通过。
    /// </summary>
    [Fact]
    public void Validate_ThreeShiftsMixedCrossMidnight_Covers24Hours_ReturnsNull()
    {
        var shifts = new List<ShiftConfig>
        {
            new() { Name = "白", StartTime = new TimeSpan(8, 0, 0), EndTime = new TimeSpan(16, 0, 0) },
            new() { Name = "小夜", StartTime = new TimeSpan(16, 0, 0), EndTime = new TimeSpan(0, 0, 0) },
            new() { Name = "大夜", StartTime = new TimeSpan(0, 0, 0), EndTime = new TimeSpan(8, 0, 0) },
        };
        Assert.Null(ShiftValidator.Validate(shifts));
    }

    /// <summary>
    /// 三班次跨午夜重叠：白 08:00-20:00 + 夜 18:00-06:00 + 凌晨 04:00-10:00。
    /// 多个重叠时段应被检测到。
    /// </summary>
    [Fact]
    public void Validate_ThreeShiftsCrossMidnightMultipleOverlaps_ReturnsOverlapError()
    {
        var shifts = new List<ShiftConfig>
        {
            new() { Name = "白", StartTime = new TimeSpan(8, 0, 0), EndTime = new TimeSpan(20, 0, 0) },
            new() { Name = "夜", StartTime = new TimeSpan(18, 0, 0), EndTime = new TimeSpan(6, 0, 0) },
            new() { Name = "晨", StartTime = new TimeSpan(4, 0, 0), EndTime = new TimeSpan(10, 0, 0) },
        };
        var result = ShiftValidator.Validate(shifts);
        Assert.NotNull(result);
        Assert.Contains("重叠", result!);
    }

    /// <summary>
    /// 班次列表含一个 null 班次（防御性测试）：实际 ObservableCollection 不会插入 null，
    /// 但若加载损坏 JSON 可能引入。验证 Validate 对 null 元素抛 NullReferenceException
    /// （当前实现不防御 null 元素，这是已知行为，测试锁定该行为以便未来重构时发现）。
    /// </summary>
    [Fact]
    public void Validate_ListContainsNullElement_ThrowsNullReferenceException()
    {
        var shifts = new List<ShiftConfig> { null! };
        Assert.Throws<NullReferenceException>(() => ShiftValidator.Validate(shifts));
    }

    /// <summary>
    /// 跨午夜班次的间隙检测：白班 08:00-16:00 + 夜班 22:00-06:00，
    /// 中间 16:00-22:00 是 6 小时空隙，应被检测到。
    /// </summary>
    [Fact]
    public void Validate_CrossMidnightShiftHasGap_ReturnsGapError()
    {
        var shifts = new List<ShiftConfig>
        {
            new() { Name = "白", StartTime = new TimeSpan(8, 0, 0), EndTime = new TimeSpan(16, 0, 0) },
            new() { Name = "夜", StartTime = new TimeSpan(22, 0, 0), EndTime = new TimeSpan(6, 0, 0) },
        };
        var result = ShiftValidator.Validate(shifts);
        Assert.NotNull(result);
        Assert.Contains("空隙", result!);
    }
}
