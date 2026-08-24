using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using MainAPP.Resources;
using MainAPP.ViewModels;
using Xunit;

using CoreDataSourceValueType = Kanban.Collector.Core.Models.DataSourceValueType;

namespace MainAPP.Tests.Unit;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public sealed class DataSourceMonitoringViewModelTests : IDisposable
{
    private readonly DeviceRepository _deviceRepository = new(new AppSettings());

    public void Dispose()
    {
        _deviceRepository.ReplaceAll([]);
    }

    [Fact]
    public void Refresh_IncludesOnlyEnabledSourcesAndValues()
    {
        var device = CreateDevice("dev-1", "一号设备");
        device.Sources.Add(CreateSource("src-enabled", "环境", CreateValue("v-enabled", "温度", valid: true)));
        device.Sources.Add(new DataSource
        {
            Id = "src-disabled",
            Name = "停用源",
            Enabled = false,
        });
        var sourceWithDisabledValue = CreateSource("src-values", "能耗",
            CreateValue("v-disabled", "功率", valid: true, enabled: false));
        device.Sources.Add(sourceWithDisabledValue);
        _deviceRepository.ReplaceAll([device]);

        using var viewModel = CreateViewModel();
        viewModel.RefreshCommand.Execute(null);

        var row = Assert.Single(viewModel.Rows);
        Assert.Equal("温度", row.ValueName);
        Assert.Equal(1, viewModel.TotalValues);
    }

    [Fact]
    public void Refresh_ReusesRowsForStableValueIdentity()
    {
        var device = CreateDevice("dev-1", "一号设备");
        device.Sources.Add(CreateSource("src-1", "环境", CreateValue("v-1", "温度", valid: true)));
        _deviceRepository.ReplaceAll([device]);

        using var viewModel = CreateViewModel();
        viewModel.RefreshCommand.Execute(null);
        var firstRow = Assert.Single(viewModel.Rows);

        device.Sources[0].Values[0].SetRuntimeValue(
            new DataSourceRuntimeValue(CoreDataSourceValueType.Int32, Int32Value: 84),
            DateTime.Now);
        viewModel.RefreshCommand.Execute(null);

        Assert.Same(firstRow, Assert.Single(viewModel.Rows));
        Assert.Equal("84", firstRow.CurrentValueText);
    }

    [Fact]
    public void Refresh_ClassifiesStatuses_AndKpisIgnoreFilters()
    {
        var device = CreateDevice("dev-1", "一号设备");
        device.Sources.Add(CreateSource("src-1", "环境",
            CreateValue("v-normal", "温度", valid: true),
            CreateAlarmValue("v-alarm", "湿度"),
            CreateValue("v-invalid", "压力", valid: false)));
        _deviceRepository.ReplaceAll([device]);

        using var viewModel = CreateViewModel();
        viewModel.RefreshCommand.Execute(null);

        Assert.Equal(3, viewModel.TotalValues);
        Assert.Equal(1, viewModel.NormalValues);
        Assert.Equal(1, viewModel.AlarmValues);
        Assert.Equal(1, viewModel.ReadFailedValues);
        Assert.Equal(3, viewModel.Rows.Count);
        Assert.Equal(2, viewModel.ExceptionValues);
        Assert.Equal(2, viewModel.ExceptionRows.Count);
        Assert.Equal(DataSourceMonitorStatus.ReadFailed, viewModel.ExceptionRows[0].Status);
        Assert.Contains(viewModel.Rows, row => row.Status == DataSourceMonitorStatus.Normal);
        Assert.Contains(viewModel.Rows, row => row.Status == DataSourceMonitorStatus.Alarm);
        Assert.Contains(viewModel.Rows, row => row.Status == DataSourceMonitorStatus.ReadFailed);

        viewModel.SelectedStateKey = "Alarm";
        Assert.Single(viewModel.Rows);
        Assert.Equal(DataSourceMonitorStatus.Alarm, viewModel.Rows[0].Status);
        Assert.Equal(3, viewModel.TotalValues);
    }

    [Fact]
    public void ExceptionSelection_PersistsWhenFiltersHideSelectedRow()
    {
        var device = CreateDevice("dev-1", "一号设备");
        device.Sources.Add(CreateSource("src-1", "环境",
            CreateValue("v-normal", "温度", valid: true),
            CreateValue("v-failed", "压力", valid: false)));
        _deviceRepository.ReplaceAll([device]);

        using var viewModel = CreateViewModel();
        viewModel.RefreshCommand.Execute(null);
        var exceptionRow = Assert.Single(viewModel.ExceptionRows);
        viewModel.SelectedRow = exceptionRow;

        viewModel.SearchText = "温度";

        Assert.Single(viewModel.Rows);
        Assert.Equal("温度", viewModel.Rows[0].ValueName);
        Assert.Same(exceptionRow, viewModel.SelectedRow);
    }

    [Fact]
    public void Filters_MatchDeviceNamesSourceNamesValueNamesAndAddresses()
    {
        var first = CreateDevice("dev-1", "冲压一号");
        first.Sources.Add(CreateSource("src-1", "环境",
            CreateValue("v-1", "温度", valid: true, address: "D300")));
        var second = CreateDevice("dev-2", "注塑二号");
        second.Sources.Add(CreateSource("src-2", "能耗",
            CreateValue("v-2", "电流", valid: true, address: "D400")));
        _deviceRepository.ReplaceAll([first, second]);

        using var viewModel = CreateViewModel();
        viewModel.RefreshCommand.Execute(null);

        viewModel.SearchText = "注塑二号";
        Assert.Single(viewModel.Rows);
        Assert.Equal("电流", viewModel.Rows[0].ValueName);

        viewModel.SearchText = "环境";
        Assert.Single(viewModel.Rows);
        Assert.Equal("温度", viewModel.Rows[0].ValueName);

        viewModel.SearchText = "D400";
        Assert.Single(viewModel.Rows);
        Assert.Equal("注塑二号", viewModel.Rows[0].DeviceName);

        viewModel.SearchText = string.Empty;
        viewModel.SelectedDeviceId = "dev-1";
        Assert.Single(viewModel.Rows);
        Assert.Equal("冲压一号", viewModel.Rows[0].DeviceName);
    }

    [Fact]
    public void QuickFilters_UpdateRowsAndClearFiltersRestoresAllRows()
    {
        var device = CreateDevice("dev-1", "一号设备");
        device.Sources.Add(CreateSource("src-1", "环境",
            CreateValue("v-normal", "温度", valid: true),
            CreateAlarmValue("v-alarm", "湿度"),
            CreateValue("v-invalid", "压力", valid: false)));
        _deviceRepository.ReplaceAll([device]);

        using var viewModel = CreateViewModel();
        viewModel.RefreshCommand.Execute(null);

        viewModel.SelectStateCommand.Execute("Alarm");
        Assert.Single(viewModel.Rows);
        Assert.Equal(1, viewModel.ShowingValues);

        viewModel.ShowExceptionsCommand.Execute(null);
        Assert.Equal(2, viewModel.Rows.Count);
        Assert.Equal("Exceptions", viewModel.SelectedStateKey);
        Assert.Equal(2, viewModel.ShowingValues);

        viewModel.ClearFiltersCommand.Execute(null);
        Assert.Equal("All", viewModel.SelectedStateKey);
        Assert.Null(viewModel.SelectedDeviceId);
        Assert.Equal(string.Empty, viewModel.SearchText);
        Assert.Equal(3, viewModel.Rows.Count);
        Assert.Equal(3, viewModel.ShowingValues);
        Assert.False(viewModel.HasActiveFilters);
    }

    [Fact]
    public void ShowingSummaryAndEmptyStateDistinguishFilterMissFromNoConfiguration()
    {
        using var emptyViewModel = CreateViewModel();
        emptyViewModel.RefreshCommand.Execute(null);

        Assert.False(emptyViewModel.HasConfiguredValues);
        Assert.Equal(Strings.Dsm_Empty, emptyViewModel.EmptyStateTitle);
        Assert.Equal(Strings.Dsm_EmptyHint, emptyViewModel.EmptyStateHint);

        var device = CreateDevice("dev-1", "一号设备");
        device.Sources.Add(CreateSource("src-1", "环境", CreateValue("v-1", "温度", valid: true)));
        _deviceRepository.ReplaceAll([device]);

        using var viewModel = CreateViewModel();
        viewModel.RefreshCommand.Execute(null);
        viewModel.SearchText = "不存在";

        Assert.True(viewModel.HasConfiguredValues);
        Assert.False(viewModel.HasRows);
        Assert.Equal(Strings.Dsm_NoMatch, viewModel.EmptyStateTitle);
        Assert.Equal(Strings.Dsm_NoMatchHint, viewModel.EmptyStateHint);
        Assert.Equal(string.Format(Strings.Dsm_ShowingSummary, 0, 1), viewModel.ShowingSummaryText);
    }

    [Fact]
    public void RowsExposeFreshnessAndReadOnlyDetailFields()
    {
        var device = CreateDevice("dev-1", "一号设备");
        var source = CreateSource("src-1", "环境", CreateValue(
            "v-stale",
            "温度",
            valid: true,
            updatedAt: DateTime.Now.AddSeconds(-10)));
        source.TriggerAddress = "M200";
        source.TriggerValue = 7;
        source.AckValue = 8;
        source.TriggerAddress = string.Empty;
        source.Values[0].LimitMin = 0;
        source.Values[0].LimitMax = 100;
        source.Values.Add(new DataSourceValue
        {
            Id = "v-unsampled",
            Name = "压力",
            DataType = CoreDataSourceValueType.Int32,
            PlcAddress = "D301",
        });
        var triggerSource = CreateSource("src-trigger", "触发源", CreateValue("v-trigger", "触发值", valid: true));
        triggerSource.TriggerAddress = "M200";
        triggerSource.TriggerValue = 7;
        triggerSource.AckValue = 8;
        device.Sources.Add(triggerSource);
        device.Sources.Add(source);
        _deviceRepository.ReplaceAll([device]);

        using var viewModel = CreateViewModel();
        viewModel.RefreshCommand.Execute(null);

        var staleRow = Assert.Single(viewModel.Rows, row => row.ValueId == "v-stale");
        Assert.True(staleRow.IsSampled);
        Assert.True(staleRow.IsStale);
        Assert.Equal(Strings.Dsm_Stale, staleRow.FreshnessText);
        Assert.Equal("0 ~ 100", staleRow.CriteriaText);

        var triggerRow = Assert.Single(viewModel.Rows, row => row.ValueId == "v-trigger");
        Assert.Equal("M200", triggerRow.TriggerAddressText);
        Assert.Equal("7", triggerRow.TriggerValueText);
        Assert.Equal("8", triggerRow.AckValueText);

        var unsampledRow = Assert.Single(viewModel.Rows, row => row.ValueId == "v-unsampled");
        Assert.False(unsampledRow.IsSampled);
        Assert.False(unsampledRow.IsStale);
        Assert.Equal(Strings.Dsm_NotSampled, unsampledRow.CurrentValueText);
        Assert.Equal(Strings.Dsm_NotSampled, unsampledRow.FreshnessText);

        viewModel.SelectedRow = triggerRow;
        Assert.True(viewModel.HasSelectedRow);
        Assert.Equal("触发值", viewModel.SelectedRow!.ValueName);
        Assert.Equal("触发采集", viewModel.SelectedRow.TriggerModeText);
    }

    [Fact]
    public void RowsFormatValuesAccordingToDataType()
    {
        var device = CreateDevice("dev-1", "一号设备");
        var source = CreateSource("src-1", "状态");

        var integerValue = CreateValue("v-int", "模式", valid: true);
        integerValue.EnumValues.Add(new DataSourceEnumValue { Value = 42, DisplayName = "运行" });
        source.Values.Add(integerValue);

        source.Values.Add(CreateTypedValue(
            "v-float",
            "温度",
            CoreDataSourceValueType.Float32,
            new DataSourceRuntimeValue(CoreDataSourceValueType.Float32, Float32Value: 12.34567f)));
        source.Values.Add(CreateTypedValue(
            "v-true",
            "启用",
            CoreDataSourceValueType.Bool,
            new DataSourceRuntimeValue(CoreDataSourceValueType.Bool, BoolValue: true)));
        source.Values.Add(CreateTypedValue(
            "v-false",
            "运行中",
            CoreDataSourceValueType.Bool,
            new DataSourceRuntimeValue(CoreDataSourceValueType.Bool, BoolValue: false)));
        source.Values.Add(CreateTypedValue(
            "v-string",
            "批次",
            CoreDataSourceValueType.String,
            new DataSourceRuntimeValue(CoreDataSourceValueType.String, StringValue: "BATCH-20260819-012345")));
        source.Values.Add(CreateTypedValue(
            "v-empty",
            "备注",
            CoreDataSourceValueType.String,
            new DataSourceRuntimeValue(CoreDataSourceValueType.String, StringValue: string.Empty)));
        source.Values.Add(new DataSourceValue
        {
            Id = "v-unsampled-bool",
            Name = "未采样开关",
            DataType = CoreDataSourceValueType.Bool,
            PlcAddress = "D309",
        });
        device.Sources.Add(source);
        _deviceRepository.ReplaceAll([device]);

        using var viewModel = CreateViewModel();
        viewModel.RefreshCommand.Execute(null);

        var integerRow = Assert.Single(viewModel.Rows, row => row.ValueId == "v-int");
        Assert.True(integerRow.IsNumeric);
        Assert.False(integerRow.IsFloat);
        Assert.Equal("运行", integerRow.CurrentValueText);

        var floatRow = Assert.Single(viewModel.Rows, row => row.ValueId == "v-float");
        Assert.True(floatRow.IsNumeric);
        Assert.True(floatRow.IsFloat);
        Assert.Equal(12.34567f.ToString("0.###", System.Globalization.CultureInfo.CurrentCulture), floatRow.CurrentValueText);

        var trueRow = Assert.Single(viewModel.Rows, row => row.ValueId == "v-true");
        Assert.True(trueRow.IsBoolean);
        Assert.True(trueRow.HasCurrentValue);
        Assert.Equal(Strings.Dsm_BoolTrue, trueRow.BooleanDisplayText);
        Assert.Equal(Strings.Dsm_BoolTrue, trueRow.CurrentValueText);

        var falseRow = Assert.Single(viewModel.Rows, row => row.ValueId == "v-false");
        Assert.True(falseRow.IsBoolean);
        Assert.True(falseRow.HasCurrentValue);
        Assert.Equal(Strings.Dsm_BoolFalse, falseRow.BooleanDisplayText);
        Assert.Equal(Strings.Dsm_BoolFalse, falseRow.CurrentValueText);
        Assert.NotEqual(DataSourceMonitorStatus.ReadFailed, falseRow.Status);

        var stringRow = Assert.Single(viewModel.Rows, row => row.ValueId == "v-string");
        Assert.True(stringRow.IsString);
        Assert.Equal("BATCH-20260819-012345", stringRow.CurrentValueText);

        var emptyStringRow = Assert.Single(viewModel.Rows, row => row.ValueId == "v-empty");
        Assert.Equal("-", emptyStringRow.CurrentValueText);

        var unsampledBoolRow = Assert.Single(viewModel.Rows, row => row.ValueId == "v-unsampled-bool");
        Assert.True(unsampledBoolRow.IsBoolean);
        Assert.False(unsampledBoolRow.HasCurrentValue);
        Assert.Equal(Strings.Dsm_NotSampled, unsampledBoolRow.CurrentValueText);
    }

    [Fact]
    public void DisplayMode_CanSwitchBetweenTableCardsAndTrendWithoutChangingRowsOrSelection()
    {
        var device = CreateDevice("dev-1", "一号设备");
        device.Sources.Add(CreateSource("src-1", "环境",
            CreateValue("v-normal", "温度", valid: true),
            CreateAlarmValue("v-alarm", "湿度"),
            CreateValue("v-invalid", "压力", valid: false)));
        _deviceRepository.ReplaceAll([device]);

        using var viewModel = CreateViewModel();
        viewModel.RefreshCommand.Execute(null);
        var selectedRow = viewModel.Rows[0];
        viewModel.SelectedRow = selectedRow;

        viewModel.IsCompactCardsView = true;
        Assert.Equal(DataSourceMonitorDisplayMode.CompactCards, viewModel.SelectedDisplayMode);
        Assert.Same(selectedRow, viewModel.SelectedRow);
        Assert.Equal(3, viewModel.Rows.Count);

        viewModel.IsTrendView = true;
        Assert.Equal(DataSourceMonitorDisplayMode.Trend, viewModel.SelectedDisplayMode);
        Assert.Equal(3, viewModel.TrendRows.Count);
        Assert.Same(selectedRow, viewModel.SelectedRow);

        viewModel.IsTableView = true;
        Assert.Equal(DataSourceMonitorDisplayMode.Table, viewModel.SelectedDisplayMode);
        Assert.Same(selectedRow, viewModel.SelectedRow);
    }

    [Fact]
    public void TrendView_TracksNumericSamplesWithoutConvertingTextValues()
    {
        var device = CreateDevice("dev-1", "一号设备");
        var source = CreateSource("src-1", "环境");
        var value = CreateValue("v-trend", "温度", valid: true);
        value.DataType = CoreDataSourceValueType.Float32;
        var firstTimestamp = new DateTime(2026, 8, 18, 12, 0, 0);
        value.SetRuntimeValue(
            new DataSourceRuntimeValue(CoreDataSourceValueType.Float32, Float32Value: 12.5f),
            firstTimestamp);
        source.Values.Clear();
        source.Values.Add(value);
        device.Sources.Add(source);
        _deviceRepository.ReplaceAll([device]);

        using var viewModel = CreateViewModel();
        viewModel.RefreshCommand.Execute(null);
        viewModel.SelectedRow = viewModel.Rows[0];
        viewModel.IsTrendView = true;

        Assert.Equal(DataSourceMonitorDisplayMode.Trend, viewModel.SelectedDisplayMode);
        Assert.Equal(12.5, viewModel.SelectedRow!.NumericValue);
        var trendSeries = Assert.IsType<OxyPlot.Series.LineSeries>(viewModel.TrendChart.Series[0]);
        Assert.Single(trendSeries.Points);

        value.SetRuntimeValue(
            new DataSourceRuntimeValue(CoreDataSourceValueType.Float32, Float32Value: 15.75f),
            firstTimestamp.AddMinutes(1));
        viewModel.RefreshCommand.Execute(null);

        Assert.Equal(2, trendSeries.Points.Count);
        Assert.True(viewModel.HasTrendChartData);

        value.SetRuntimeValue(
            new DataSourceRuntimeValue(CoreDataSourceValueType.Float32, IsValid: false),
            firstTimestamp.AddMinutes(2));
        viewModel.RefreshCommand.Execute(null);

        Assert.Equal(3, trendSeries.Points.Count);
        Assert.True(double.IsNaN(trendSeries.Points[2].Y));
        Assert.True(viewModel.HasTrendChartData);
        Assert.True(viewModel.HasTrendDataGap);

        value.SetRuntimeValue(
            new DataSourceRuntimeValue(CoreDataSourceValueType.Float32, Float32Value: 16.25f),
            firstTimestamp.AddMinutes(3));
        viewModel.RefreshCommand.Execute(null);

        Assert.Equal(4, trendSeries.Points.Count);
        Assert.Equal(16.25, trendSeries.Points[3].Y);
    }

    [Fact]
    public void TrendView_DisplaysConfiguredLimits_AndClearsThemForUnboundedValue()
    {
        var device = CreateDevice("dev-1", "一号设备");
        var limitedValue = CreateValue("v-limited", "温度", valid: true);
        limitedValue.LimitMin = 0;
        limitedValue.LimitMax = 100;
        var normalValue = CreateValue("v-normal", "压力", valid: true);
        device.Sources.Add(CreateSource("src-1", "环境", limitedValue, normalValue));
        _deviceRepository.ReplaceAll([device]);

        using var viewModel = CreateViewModel();
        viewModel.RefreshCommand.Execute(null);
        viewModel.SelectedRow = Assert.Single(viewModel.Rows, row => row.ValueId == "v-limited");
        viewModel.IsTrendView = true;

        var lowerLimitSeries = Assert.IsType<OxyPlot.Series.LineSeries>(viewModel.TrendChart.Series[2]);
        var upperLimitSeries = Assert.IsType<OxyPlot.Series.LineSeries>(viewModel.TrendChart.Series[3]);
        Assert.True(lowerLimitSeries.IsVisible);
        Assert.True(upperLimitSeries.IsVisible);
        Assert.Equal(0, lowerLimitSeries.Points[0].Y);
        Assert.Equal(100, upperLimitSeries.Points[0].Y);
        Assert.Equal($"{Strings.Dsm_TrendLowerLimit} 0", lowerLimitSeries.Title);
        Assert.Equal($"{Strings.Dsm_TrendUpperLimit} 100", upperLimitSeries.Title);
        Assert.True(viewModel.TrendChart.IsLegendVisible);

        viewModel.SelectedRow = Assert.Single(viewModel.Rows, row => row.ValueId == "v-normal");

        Assert.False(lowerLimitSeries.IsVisible);
        Assert.False(upperLimitSeries.IsVisible);
        Assert.Empty(lowerLimitSeries.Points);
        Assert.Empty(upperLimitSeries.Points);
        Assert.False(viewModel.TrendChart.IsLegendVisible);
    }

    [Fact]
    public void TrendView_CropsSessionBufferToConfiguredPointWindow_AndResetsOnPageExit()
    {
        var device = CreateDevice("dev-1", "一号设备");
        var source = CreateSource("src-1", "环境");
        var value = CreateValue("v-trend", "温度", valid: true);
        value.DataType = CoreDataSourceValueType.Float32;
        source.Values.Clear();
        source.Values.Add(value);
        device.Sources.Add(source);
        _deviceRepository.ReplaceAll([device]);

        using var viewModel = CreateViewModel();
        var firstTimestamp = new DateTime(2026, 8, 18, 12, 0, 0);
        for (var index = 0; index <= 600; index++)
        {
            value.SetRuntimeValue(
                new DataSourceRuntimeValue(CoreDataSourceValueType.Float32, Float32Value: index),
                firstTimestamp.AddSeconds(index));
            viewModel.RefreshCommand.Execute(null);
        }

        viewModel.SelectedRow = viewModel.Rows[0];
        viewModel.IsTrendView = true;
        var trendSeries = Assert.IsType<OxyPlot.Series.LineSeries>(viewModel.TrendChart.Series[0]);
        Assert.Equal(600, trendSeries.Points.Count);
        Assert.Equal(1, trendSeries.Points[0].Y);
        Assert.Equal(600, trendSeries.Points[^1].Y);

        viewModel.OnPageExit();

        Assert.Empty(trendSeries.Points);
        Assert.False(viewModel.HasTrendChartData);
        Assert.False(viewModel.HasTrendDataGap);

        value.SetRuntimeValue(
            new DataSourceRuntimeValue(CoreDataSourceValueType.Float32, IsValid: false),
            firstTimestamp.AddMinutes(11));
        viewModel.RefreshCommand.Execute(null);

        Assert.False(viewModel.HasTrendChartData);
        Assert.True(viewModel.HasTrendDataGap);
        Assert.Single(trendSeries.Points);
        Assert.True(double.IsNaN(trendSeries.Points[0].Y));
    }

    private DataSourceMonitoringViewModel CreateViewModel() => new(_deviceRepository);

    private static Device CreateDevice(string id, string name) => new()
    {
        Id = id,
        Name = name,
    };

    private static DataSource CreateSource(string id, string name, params DataSourceValue[] values)
    {
        var source = new DataSource
        {
            Id = id,
            Name = name,
            Type = "PLC",
            TriggerAddress = "M100",
        };
        foreach (var value in values)
            source.Values.Add(value);
        return source;
    }

    private static DataSourceValue CreateValue(
        string id,
        string name,
        bool valid,
        string address = "D300",
        bool enabled = true,
        DateTime? updatedAt = null)
    {
        var value = new DataSourceValue
        {
            Id = id,
            Name = name,
            DataType = CoreDataSourceValueType.Int32,
            PlcAddress = address,
            Enabled = enabled,
        };
        value.SetRuntimeValue(new DataSourceRuntimeValue(
            CoreDataSourceValueType.Int32,
            Int32Value: 42,
            IsValid: valid),
            updatedAt ?? new DateTime(2026, 8, 18, 12, 0, 0));
        return value;
    }

    private static DataSourceValue CreateAlarmValue(string id, string name)
    {
        var value = CreateValue(id, name, valid: true);
        value.LimitMin = 0;
        value.LimitMax = 10;
        return value;
    }

    private static DataSourceValue CreateTypedValue(
        string id,
        string name,
        CoreDataSourceValueType dataType,
        DataSourceRuntimeValue runtimeValue)
    {
        var value = new DataSourceValue
        {
            Id = id,
            Name = name,
            DataType = dataType,
            PlcAddress = "D300",
        };
        value.SetRuntimeValue(runtimeValue, new DateTime(2026, 8, 18, 12, 0, 0));
        return value;
    }
}