using System;
using System.IO;
using System.Linq;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Resources;
using MainAPP.Services;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// 四类配置 CSV 的多语言回归测试。
/// 语言来源与设置页一致：AppSettings.Language 经 Localization.Apply 后驱动资源输出。
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
[Collection("LocalizationSensitive")]
public sealed class CsvLocalizationTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "kanban_csvloc_" + Guid.NewGuid().ToString("N"));

    public CsvLocalizationTests()
    {
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        Localization.Apply(AppLanguage.Zh);
        try
        {
            if (Directory.Exists(_tempDirectory))
                Directory.Delete(_tempDirectory, recursive: true);
        }
        catch
        {
        }
    }

    [Theory]
    [InlineData(AppLanguage.Zh, "名称", "高", "严重", "外观", "整数", "开启")]
    [InlineData(AppLanguage.En, "Name", "High", "Critical", "Appearance", "Integer", "On")]
    [InlineData(AppLanguage.Ja, "名前", "高", "重大", "外観", "整数", "オン")]
    [InlineData(AppLanguage.PtBr, "Nome", "High", "Critical", "Appearance", "Integer", "On")]
    public void Export_UsesLanguageFromSettings(
        AppLanguage language,
        string alarmNameHeader,
        string alarmLevel,
        string severity,
        string category,
        string dataType,
        string enabled)
    {
        var settings = new AppSettings { Language = language };
        Localization.Apply(settings.Language);
        var device = CreateDevice();

        var alarmPath = Path.Combine(_tempDirectory, $"alarm_{(int)language}.csv");
        var defectPath = Path.Combine(_tempDirectory, $"defect_{(int)language}.csv");
        var counterAlarmPath = Path.Combine(_tempDirectory, $"counter_{(int)language}.csv");
        var dataSourcePath = Path.Combine(_tempDirectory, $"source_{(int)language}.csv");

        new AlarmCsvIOService(new FakeDialogService()).ExportAlarmsToPath(device, alarmPath);
        new DefectCsvIOService(new FakeDialogService()).ExportDefectsToPath(device, defectPath);
        new CounterAlarmCsvIOService(new FakeDialogService()).ExportCounterAlarmsToPath(device, counterAlarmPath);
        new DataSourceCsvIOService(new FakeDialogService()).ExportDataSourcesToPath(device, dataSourcePath);

        var alarmLine = File.ReadLines(alarmPath).First();
        var defectLine = File.ReadLines(defectPath).First();
        var counterLine = File.ReadLines(counterAlarmPath).First();
        var sourceLines = File.ReadAllLines(dataSourcePath);

        Assert.StartsWith($"{alarmNameHeader},", alarmLine);
        Assert.Contains(alarmLevel, File.ReadAllLines(alarmPath)[1]);
        Assert.Contains(severity, File.ReadAllLines(defectPath)[1]);
        Assert.Contains(category, File.ReadAllLines(defectPath)[1]);
        Assert.Contains(enabled, File.ReadAllLines(counterAlarmPath)[1]);
        Assert.Contains(dataType, sourceLines[1]);
        Assert.Contains(enabled, sourceLines[1]);

        // 使用另一种当前语言解析，验证导入按本地化别名工作，而不是依赖导出语言。
        Localization.Apply(AppLanguage.Zh);
        var alarmResult = new AlarmCsvIOService(new FakeDialogService()).ParseAndValidate(alarmPath);
        var defectResult = new DefectCsvIOService(new FakeDialogService()).ParseAndValidate(defectPath);
        var counterResult = new CounterAlarmCsvIOService(new FakeDialogService()).ParseAndValidate(counterAlarmPath);
        var sourceResult = new DataSourceCsvIOService(new FakeDialogService()).ParseAndValidate(dataSourcePath);

        Assert.False(alarmResult.HasErrors, string.Join("; ", alarmResult.Errors));
        Assert.False(defectResult.HasErrors, string.Join("; ", defectResult.Errors));
        Assert.False(counterResult.HasErrors, string.Join("; ", counterResult.Errors));
        Assert.False(sourceResult.HasErrors, string.Join("; ", sourceResult.Errors));
        Assert.Equal(AlarmLevel.High, alarmResult.Imported.Single().Level);
        Assert.Equal(DefectSeverity.Critical, defectResult.Imported.Single().Severity);
        Assert.Equal(DefectCategory.Appearance, defectResult.Imported.Single().Category);
        Assert.True(counterResult.Imported.Single().Enabled);
        Assert.Equal(DataSourceValueType.Int32, sourceResult.Imported.Single().Values.Single().DataType);
    }

    [Fact]
    public void DataSourceImport_AcceptsLocalizedTypeAndBooleanValues()
    {
        var settings = new AppSettings { Language = AppLanguage.Ja };
        Localization.Apply(settings.Language);
        var path = Path.Combine(_tempDirectory, "localized-source.csv");
        File.WriteAllText(
            path,
            "データソース名,データソースタイプ,データソース有効,データソースの説明,トリガーアドレス,トリガー値,応答値,値項目名,データ型,値項目PLCアドレス,値項目単位,値項目有効,整数下限,整数上限,浮動小数点下限,浮動小数点上限,ヒステリシス,確認遅延(秒),期待値設定済み,整数期待値,浮動小数点期待値,ブール期待値,文字列期待値,文字列長,列挙マッピング JSON\n"
                + string.Join(",", new[] { "源", "Type", "オン", "", "D100", "1", "2", "值", "整数", "D102", "", "オン", "", "", "", "", "", "", "オフ", "", "", "", "", "", "" }) + "\n",
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Localization.Apply(AppLanguage.Zh);
        var result = new DataSourceCsvIOService(new FakeDialogService()).ParseAndValidate(path);

        Assert.False(result.HasErrors, string.Join("; ", result.Errors));
        Assert.Single(result.Imported);
        Assert.True(result.Imported[0].Enabled);
        Assert.True(result.Imported[0].Values.Single().Enabled);
    }

    private static Device CreateDevice()
    {
        var device = new Device { Id = "dev1", Name = "设备1" };
        device.Alarms.Add(new Alarm
        {
            DeviceId = device.Id,
            Name = "报警",
            PlcAddress = "M100",
            Level = AlarmLevel.High,
        });
        device.Defects.Add(new Defect
        {
            DeviceId = device.Id,
            Name = "缺陷",
            PlcAddress = "D102",
            Severity = DefectSeverity.Critical,
            Category = DefectCategory.Appearance,
        });
        device.CounterAlarms.Add(new CounterAlarm
        {
            DeviceId = device.Id,
            Name = "计数报警",
            PlcAddress = "D104",
            MaxValue = 10,
            Enabled = true,
        });
        var source = new DataSource
        {
            DeviceId = device.Id,
            Name = "源",
            Type = "Type",
            Enabled = true,
        };
        source.Values.Add(new DataSourceValue
        {
            Name = "值",
            DataType = DataSourceValueType.Int32,
            PlcAddress = "D106",
            Enabled = true,
        });
        device.Sources.Add(source);
        return device;
    }
}
