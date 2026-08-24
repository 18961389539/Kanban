using System;
using System.IO;
using System.Linq;
using System.Text;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// 数据源配置 CSV 服务测试：覆盖多类型值项往返、空值项源、行级校验和追加/替换应用。
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
[Collection("LocalizationSensitive")]
public sealed class DataSourceCsvIOServiceTests : IDisposable
{
    private const string Header = "SourceName,SourceType,SourceEnabled,SourceDescription,TriggerAddress,TriggerValue,AckValue,ValueName,ValueDataType,ValuePlcAddress,ValueUnit,ValueEnabled,LimitMin,LimitMax,FloatLimitMin,FloatLimitMax,Hysteresis,ConfirmSeconds,ExpectedValueConfigured,ExpectedValue,FloatExpectedValue,BoolExpectedValue,StringExpectedValue,StringLength,EnumValuesJson";

    private readonly string _tempDirectory;
    private readonly FakeDialogService _dialog = new();
    private readonly DataSourceCsvIOService _service;

    public DataSourceCsvIOServiceTests()
    {
        Localization.Apply(AppLanguage.Zh);
        _tempDirectory = Path.Combine(Path.GetTempPath(), "kanban_datasourcecsv_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _service = new DataSourceCsvIOService(_dialog);
    }

    public void Dispose()
    {
        Localization.Apply(AppLanguage.Zh);
        try { if (Directory.Exists(_tempDirectory)) Directory.Delete(_tempDirectory, true); } catch { }
    }

    [Fact]
    public void ExportThenImport_RoundTrip_PreservesSourceAndAllValueTypes()
    {
        var device = new Device { Id = "dev1", Name = "设备1" };
        var source = new DataSource
        {
            DeviceId = device.Id,
            Name = "环境",
            Type = "温湿度",
            Enabled = false,
            Description = "测试源",
            TriggerAddress = "D100",
            TriggerValue = 3,
            AckValue = 4,
        };
        source.Values.Add(new DataSourceValue
        {
            Name = "整数",
            DataType = DataSourceValueType.Int32,
            PlcAddress = "D102",
            Unit = "pcs",
            LimitMin = -10,
            LimitMax = 100,
            Hysteresis = 2,
            ConfirmSeconds = 3,
            ExpectedValue = 12,
            EnumValues = { new DataSourceEnumValue { Value = 0, DisplayName = "待机" } },
        });
        source.Values.Add(new DataSourceValue
        {
            Name = "浮点",
            DataType = DataSourceValueType.Float32,
            PlcAddress = "D104",
            FloatLimitMin = -1.5f,
            FloatLimitMax = 3.25f,
            FloatExpectedValue = 0.5f,
        });
        source.Values.Add(new DataSourceValue
        {
            Name = "开关",
            DataType = DataSourceValueType.Bool,
            PlcAddress = "M100",
            BoolExpectedValue = true,
        });
        source.Values.Add(new DataSourceValue
        {
            Name = "文本",
            DataType = DataSourceValueType.String,
            PlcAddress = "D106",
            StringLength = 64,
            StringExpectedValue = string.Empty,
        });
        device.Sources.Add(source);

        var path = Path.Combine(_tempDirectory, "sources.csv");
        var summary = _service.ExportDataSourcesToPath(device, path);
        var result = _service.ParseAndValidate(path);

        Assert.Equal(1, summary.SourceCount);
        Assert.Equal(4, summary.ValueCount);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors));
        Assert.Single(result.Imported);
        Assert.Equal(4, result.ImportedValueCount);

        var importedSource = result.Imported[0];
        Assert.Equal("环境", importedSource.Name);
        Assert.Equal("温湿度", importedSource.Type);
        Assert.False(importedSource.Enabled);
        Assert.Equal("D100", importedSource.TriggerAddress);
        Assert.Equal(3, importedSource.TriggerValue);
        Assert.Equal(4, importedSource.AckValue);

        var intValue = importedSource.Values.Single(value => value.Name == "整数");
        Assert.Equal(-10, intValue.LimitMin);
        Assert.Equal(100, intValue.LimitMax);
        Assert.Equal(12, intValue.ExpectedValue);
        Assert.Equal("待机", intValue.EnumValues.Single().DisplayName);

        var floatValue = importedSource.Values.Single(value => value.Name == "浮点");
        Assert.Equal(-1.5f, floatValue.FloatLimitMin);
        Assert.Equal(3.25f, floatValue.FloatLimitMax);
        Assert.Equal(0.5f, floatValue.FloatExpectedValue);

        var boolValue = importedSource.Values.Single(value => value.Name == "开关");
        Assert.True(boolValue.BoolExpectedValue);

        var stringValue = importedSource.Values.Single(value => value.Name == "文本");
        Assert.Equal(64, stringValue.StringLength);
        Assert.NotNull(stringValue.StringExpectedValue);
        Assert.Equal(string.Empty, stringValue.StringExpectedValue);
    }

    [Fact]
    public void ExportThenImport_SourceWithoutValues_PreservesSourceRow()
    {
        var device = new Device { Id = "dev1", Name = "设备1" };
        device.Sources.Add(new DataSource { DeviceId = device.Id, Name = "空源" });
        var path = Path.Combine(_tempDirectory, "empty-source.csv");

        _service.ExportDataSourcesToPath(device, path);
        var result = _service.ParseAndValidate(path);

        Assert.False(result.HasErrors, string.Join("; ", result.Errors));
        Assert.Single(result.Imported);
        Assert.Empty(result.Imported[0].Values);
        Assert.Equal(0, result.ImportedValueCount);
    }

    [Fact]
    public void ParseAndValidate_InvalidAddressAndEnumJson_ReportsRowErrors()
    {
        var duplicateEnumJson = "[{\"Value\":0,\"DisplayName\":\"A\"},{\"Value\":0,\"DisplayName\":\"B\"}]";
        var path = WriteCsv(
            Header + "\n"
            + string.Join(",", new[] { "源", "Type", "True", "", "D100", "1", "2", "布尔", "Bool", "D101", "", "True", "", "", "", "", "", "", "False", "", "", "", "", "", "" }) + "\n"
            + string.Join(",", new[] { "源", "Type", "True", "", "D100", "1", "2", "枚举", "Int32", "D102", "", "True", "", "", "", "", "", "", "False", "", "", "", "", "", $"\"{duplicateEnumJson.Replace("\"", "\"\"")}\"" }) + "\n");

        var result = _service.ParseAndValidate(path);

        Assert.True(result.HasErrors);
        Assert.Empty(result.Imported);
        Assert.Contains(result.Errors, error => error.Contains("类型不匹配"));
        Assert.Contains(result.Errors, error => error.Contains("枚举值重复"));
    }

    [Fact]
    public void ApplyImportedDataSources_AppendMode_UpdatesMatchingValuesAndKeepsOthers()
    {
        var device = new Device { Id = "dev1", Name = "设备1" };
        var existingSource = new DataSource { DeviceId = device.Id, Name = "源", Type = "旧类型" };
        var existingValue = new DataSourceValue { Name = "值", PlcAddress = "D100", Unit = "旧单位" };
        existingSource.Values.Add(existingValue);
        existingSource.Values.Add(new DataSourceValue { Name = "保留", PlcAddress = "D102" });
        device.Sources.Add(existingSource);

        var importedSource = new DataSource { Name = "源", Type = "新类型", TriggerAddress = "D200" };
        importedSource.Values.Add(new DataSourceValue
        {
            Name = "值",
            DataType = DataSourceValueType.Float32,
            PlcAddress = "D104",
            Unit = "新单位",
            FloatLimitMin = 1,
            FloatLimitMax = 2,
        });
        importedSource.Values.Add(new DataSourceValue { Name = "新增", PlcAddress = "D106" });

        _service.ApplyImportedDataSources(device, [importedSource], replace: false);

        Assert.Single(device.Sources);
        Assert.Same(existingSource, device.Sources[0]);
        Assert.Equal("新类型", existingSource.Type);
        Assert.Equal("D200", existingSource.TriggerAddress);
        Assert.Equal(3, existingSource.Values.Count);
        Assert.Same(existingValue, existingSource.Values.First(value => value.Name == "值"));
        Assert.Equal(DataSourceValueType.Float32, existingValue.DataType);
        Assert.Equal("D104", existingValue.PlcAddress);
        Assert.Equal("新单位", existingValue.Unit);
        Assert.Contains(existingSource.Values, value => value.Name == "保留");
        Assert.Contains(existingSource.Values, value => value.Name == "新增");
        Assert.All(existingSource.Values, value => Assert.False(string.IsNullOrWhiteSpace(value.Id)));
    }

    [Fact]
    public void ApplyImportedDataSources_ReplaceMode_ClearsExistingAndInjectsDeviceIds()
    {
        var device = new Device { Id = "dev1", Name = "设备1" };
        device.Sources.Add(new DataSource { DeviceId = device.Id, Name = "旧源" });
        var importedSource = new DataSource { Name = "新源" };
        importedSource.Values.Add(new DataSourceValue { Name = "值", PlcAddress = "D100" });

        _service.ApplyImportedDataSources(device, [importedSource], replace: true);

        Assert.Single(device.Sources);
        Assert.Equal("新源", device.Sources[0].Name);
        Assert.Equal(device.Id, device.Sources[0].DeviceId);
        Assert.DoesNotContain(device.Sources, source => source.Name == "旧源");
        Assert.False(string.IsNullOrWhiteSpace(device.Sources[0].Id));
        Assert.False(string.IsNullOrWhiteSpace(device.Sources[0].Values[0].Id));
    }

    private string WriteCsv(string content)
    {
        var path = Path.Combine(_tempDirectory, Guid.NewGuid().ToString("N") + ".csv");
        File.WriteAllText(path, content, new UTF8Encoding(true));
        return path;
    }
}
