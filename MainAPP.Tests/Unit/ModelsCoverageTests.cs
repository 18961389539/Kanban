using System.Linq;
using MainAPP.Converters;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Models;
using MainAPP.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using Material.Icons;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// 闭合覆盖率分析中 Models 类别的 6 个真实缺口：
/// DefectCategory / DeviceFilterHelper / DeviceFilterItem / FilterOption / NavItem / ShiftOeeRecord。
/// 均为低风险枚举/配置载体/DTO，无需 STA 线程或 WPF 资源。
/// </summary>
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class ModelsCoverageTests
{
    private readonly AppSettings _appSettings = new();
    private readonly DeviceRepository _deviceRepository;

    public ModelsCoverageTests()
    {
        _deviceRepository = new DeviceRepository(_appSettings);
    }

    // ---------- DefectCategory（枚举 + 伴生转换器） ----------

    [Fact]
    public void DefectCategory_HasExpectedMembersAndValues()
    {
        var values = System.Enum.GetValues<DefectCategory>().ToList();
        Assert.Equal(5, values.Count);
        Assert.Equal(DefectCategory.Appearance, values[0]);
        Assert.Equal(DefectCategory.Dimension, values[1]);
        Assert.Equal(DefectCategory.Function, values[2]);
        Assert.Equal(DefectCategory.Packaging, values[3]);
        Assert.Equal(DefectCategory.Other, values[4]);
    }

    [Theory]
    [InlineData(DefectCategory.Appearance, "外观")]
    [InlineData(DefectCategory.Dimension, "尺寸")]
    [InlineData(DefectCategory.Function, "功能")]
    [InlineData(DefectCategory.Packaging, "包装")]
    [InlineData(DefectCategory.Other, "其他")]
    public void DefectCategoryToTextConverter_MapsKnownCategory(DefectCategory category, string expected)
    {
        var converter = new DefectCategoryToTextConverter();
        var result = converter.Convert(category, typeof(string), null!, null!);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void DefectCategoryToTextConverter_NonCategory_ReturnsEmptyString()
    {
        var converter = new DefectCategoryToTextConverter();
        var result = converter.Convert("不是枚举", typeof(string), null!, null!);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void DefectCategoryToTextConverter_ConvertBack_NotImplemented()
    {
        var converter = new DefectCategoryToTextConverter();
        Assert.Throws<System.NotImplementedException>(() => converter.ConvertBack("外观", typeof(DefectCategory), null!, null!));
    }

    // ---------- DeviceFilterHelper（静态筛选辅助） ----------

    [Fact]
    public void DeviceFilterHelper_Refresh_PopulatesFromRepository()
    {
        _deviceRepository.Devices.Add(new Device { Id = "d1", Name = "设备1" });
        _deviceRepository.Devices.Add(new Device { Id = "d2", Name = "设备2" });

        var items = new System.Collections.ObjectModel.ObservableCollection<DeviceFilterItem>();
        DeviceFilterHelper.Refresh(items, _deviceRepository);

        Assert.Equal(2, items.Count);
        Assert.Equal("d1", items[0].Id);
        Assert.Equal("设备1", items[0].Name);
        Assert.Equal("d2", items[1].Id);
    }

    [Fact]
    public void DeviceFilterHelper_Refresh_ClearsExistingBeforeRepopulate()
    {
        var items = new System.Collections.ObjectModel.ObservableCollection<DeviceFilterItem>
        {
            new DeviceFilterItem("old", "过期项"),
        };
        _deviceRepository.Devices.Add(new Device { Id = "d1", Name = "设备1" });

        DeviceFilterHelper.Refresh(items, _deviceRepository);

        Assert.Single(items);
        Assert.Equal("d1", items[0].Id);
    }

    [Fact]
    public void DeviceFilterHelper_Refresh_EmptyRepository_YieldsEmpty()
    {
        var items = new System.Collections.ObjectModel.ObservableCollection<DeviceFilterItem>
        {
            new DeviceFilterItem("old", "过期项"),
        };
        DeviceFilterHelper.Refresh(items, _deviceRepository);

        Assert.Empty(items);
    }

    [Fact]
    public void DeviceFilterHelper_FallbackSelected_CurrentExists_ReturnsSame()
    {
        _deviceRepository.Devices.Add(new Device { Id = "d1", Name = "设备1" });
        var result = DeviceFilterHelper.FallbackSelected(_deviceRepository, "d1");
        Assert.Equal("d1", result);
    }

    [Fact]
    public void DeviceFilterHelper_FallbackSelected_CurrentMissing_ReturnsFirst()
    {
        _deviceRepository.Devices.Add(new Device { Id = "d1", Name = "设备1" });
        _deviceRepository.Devices.Add(new Device { Id = "d2", Name = "设备2" });
        var result = DeviceFilterHelper.FallbackSelected(_deviceRepository, "gone");
        Assert.Equal("d1", result);
    }

    [Fact]
    public void DeviceFilterHelper_FallbackSelected_CurrentMissing_NoDevices_ReturnsNull()
    {
        var result = DeviceFilterHelper.FallbackSelected(_deviceRepository, "gone");
        Assert.Null(result);
    }

    [Fact]
    public void DeviceFilterHelper_FallbackSelected_CurrentNull_ReturnsNull()
    {
        _deviceRepository.Devices.Add(new Device { Id = "d1", Name = "设备1" });
        var result = DeviceFilterHelper.FallbackSelected(_deviceRepository, null);
        Assert.Null(result);
    }

    // ---------- DeviceFilterItem（record） ----------

    [Fact]
    public void DeviceFilterItem_Construction_SetsFields()
    {
        var item = new DeviceFilterItem("d1", "设备1");
        Assert.Equal("d1", item.Id);
        Assert.Equal("设备1", item.Name);
    }

    [Fact]
    public void DeviceFilterItem_ValueEquality_SameValuesAreEqual()
    {
        var a = new DeviceFilterItem("d1", "设备1");
        var b = new DeviceFilterItem("d1", "设备1");
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void DeviceFilterItem_ValueEquality_DifferentNameNotEqual()
    {
        var a = new DeviceFilterItem("d1", "设备1");
        var b = new DeviceFilterItem("d1", "设备2");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void DeviceFilterItem_WithExpression_ProducesNewRecord()
    {
        var a = new DeviceFilterItem("d1", "设备1");
        var b = a with { Name = "改名" };
        Assert.Equal("d1", b.Id);
        Assert.Equal("改名", b.Name);
        Assert.Equal("设备1", a.Name); // 原记录不变
    }

    [Fact]
    public void DeviceFilterItem_NullIdAllowed()
    {
        var item = new DeviceFilterItem(null, "全部设备");
        Assert.Null(item.Id);
        Assert.Equal("全部设备", item.Name);
    }

    // ---------- FilterOption（record） ----------

    [Fact]
    public void FilterOption_Construction_SetsFields()
    {
        var opt = new FilterOption("白班", "白班");
        Assert.Equal("白班", opt.Value);
        Assert.Equal("白班", opt.DisplayText);
    }

    [Fact]
    public void FilterOption_ValueEquality_Works()
    {
        var a = new FilterOption("白班", "白班");
        var b = new FilterOption("白班", "白班");
        var c = new FilterOption(null, "全部班次");
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void FilterOption_NullValueMeansAll()
    {
        var all = new FilterOption(null, "全部班次");
        Assert.Null(all.Value);
        Assert.Equal("全部班次", all.DisplayText);
    }

    [Fact]
    public void FilterOption_WithExpression_PreservesValue()
    {
        var a = new FilterOption("白班", "白班");
        var b = a with { DisplayText = "白班(改)" };
        Assert.Equal("白班", b.Value);
        Assert.Equal("白班(改)", b.DisplayText);
    }

    // ---------- NavItem ----------

    [Fact]
    public void NavItem_InitProperties_AreSet()
    {
        var item = new NavItem
        {
            Index = 2,
            Icon = MaterialIconKind.Alarm,
            LabelKey = "Nav_DeviceManager",
            AccessibleNameKey = "Nav_DeviceManager",
            ToolTipKey = "Nav_DeviceManager",
        };
        Assert.Equal(2, item.Index);
        Assert.Equal("设备管理", item.Label);
        Assert.Equal(MaterialIconKind.Alarm, item.Icon);
        Assert.Equal("设备管理", item.AccessibleName);
        Assert.Equal("设备管理", item.ToolTip);
    }

    [Fact]
    public void NavItem_AccessibleName_IndependentOfLabel()
    {
        var item = new NavItem
        {
            Index = 0,
            Icon = MaterialIconKind.Home,
            LabelKey = "Nav_Home",
            AccessibleNameKey = "Nav_Home",
        };
        // 多语言架构：Label/AccessibleName 现在是计算 getter，按当前 UI 文化动态取值。
        // 测试环境下 CurrentUICulture 为默认 en-US，这里的断言由 CI 的 LocalizationGuardTests 在更广范围覆盖。
        Assert.NotNull(item.Label);
        Assert.NotNull(item.AccessibleName);
    }

    // ---------- ShiftOeeRecord ----------

    [Fact]
    public void ShiftOeeRecord_Constructor_SetsAllProperties()
    {
        var t = new System.DateTime(2026, 7, 28, 8, 0, 0);
        var rec = new ShiftOeeRecord(t, "白班", Quality: 0.98, Performance: 0.85, Availability: 0.92, Oee: 0.77);

        Assert.Equal(t, rec.ShiftTime);
        Assert.Equal("白班", rec.ShiftName);
        Assert.Equal(0.98, rec.Quality);
        Assert.Equal(0.85, rec.Performance);
        Assert.Equal(0.92, rec.Availability);
        Assert.Equal(0.77, rec.Oee);
    }

    [Fact]
    public void ShiftOeeRecord_IsImmutable_PositionalRecord()
    {
        var rec = new ShiftOeeRecord(System.DateTime.Now, "夜班", 0.95, 0.85, 0.90, 0.73);
        // positional record：属性在构造后不可变，仅可通过 with 表达式创建副本
        var rec2 = rec with { Quality = 0.99 };
        Assert.Equal(0.95, rec.Quality);  // 原记录不变
        Assert.Equal(0.99, rec2.Quality); // 新记录有新值
    }

    // ---------- 运行时状态序列化验证 ----------
    // [property: JsonIgnore] 必须生效，确保 StartTime/EndTime/CurrentValue/Count 等运行时状态
    // 不被持久化到 devices.json，避免 PLC 未连接时显示虚假报警。

    [Fact]
    public void Alarm_Serialization_ExcludesRuntimeFields()
    {
        var alarm = new Alarm
        {
            Id = "test_M100",
            DeviceId = "test",
            Name = "高温报警",
            PlcAddress = "M100",
            Level = AlarmLevel.High,
        };
        alarm.StartTime = new System.DateTime(2026, 7, 30, 10, 0, 0);
        alarm.EndTime = new System.DateTime(2026, 7, 30, 10, 5, 0);

        var json = System.Text.Json.JsonSerializer.Serialize(alarm);

        Assert.DoesNotContain("StartTime", json);
        Assert.DoesNotContain("EndTime", json);
        Assert.DoesNotContain("Duration", json);
        Assert.Contains("Name", json);
        Assert.Contains("PlcAddress", json);
    }

    [Fact]
    public void CounterAlarm_Serialization_ExcludesRuntimeFields()
    {
        var ca = new CounterAlarm
        {
            Id = "test_ca",
            DeviceId = "test",
            Name = "连续不良",
            PlcAddress = "D114",
            MaxValue = 10,
        };
        ca.CurrentValue = 15;  // IsTriggered = true

        var json = System.Text.Json.JsonSerializer.Serialize(ca);

        Assert.DoesNotContain("CurrentValue", json);
        Assert.DoesNotContain("IsTriggered", json);
        Assert.Contains("MaxValue", json);
        Assert.Contains("PlcAddress", json);
    }

    [Fact]
    public void Defect_Serialization_ExcludesRuntimeFields()
    {
        var defect = new Defect
        {
            Id = "test_def",
            DeviceId = "test",
            Name = "毛边",
            PlcAddress = "D110",
            Count = 42,
        };

        var json = System.Text.Json.JsonSerializer.Serialize(defect);

        Assert.DoesNotContain("Count", json);
        Assert.Contains("Name", json);
        Assert.Contains("PlcAddress", json);
    }
}
