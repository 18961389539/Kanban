using System;
using System.IO;
using System.Linq;
using System.Text;
using Kanban.Collector.Core.Models;
using MainAPP.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// AlarmCsvIOService 单元测试：覆盖往返一致性、字段校验、重复地址处理、空文件、追加/替换应用模式。
/// 使用 FakeDialogService 注入文件路径，所有 CSV 文件写入临时目录，测试结束清理。
/// </summary>
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class AlarmCsvIOServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FakeDialogService _dialog;
    private readonly AlarmCsvIOService _service;

    public AlarmCsvIOServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "kanban_alarmcsv_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dialog = new FakeDialogService();
        _service = new AlarmCsvIOService(_dialog);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { /* best-effort */ }
    }

    // ───────────── 往返一致性 ─────────────

    [Fact]
    public void ExportThenImport_RoundTrip_PreservesAllFields()
    {
        var device = new Device { Id = "dev1", Name = "测试设备" };
        device.Alarms.Add(new Alarm
        {
            DeviceId = device.Id,
            Name = "门未关",
            PlcAddress = "M100",
            Level = AlarmLevel.High,
            Description = "安全门未关闭",
        });
        device.Alarms.Add(new Alarm
        {
            DeviceId = device.Id,
            Name = "气压低",
            PlcAddress = "M101",
            Level = AlarmLevel.Medium,
            Description = "气压低于阈值",
        });

        var exportPath = Path.Combine(_tempDir, "alarms.csv");
        _dialog.SaveFilePath = exportPath;
        var exported = _service.ExportAlarms(device);

        Assert.True(exported);
        Assert.True(File.Exists(exportPath));
        Assert.Contains(_dialog.Success, s => s.Contains("已导出 2 条报警"));

        // 重置 dialog 状态，模拟导入流程
        _dialog.Success.Clear();
        _dialog.OpenFilePath = exportPath;
        var result = _service.ParseAndValidate(exportPath);

        Assert.False(result.HasErrors);
        Assert.Equal(2, result.Imported.Count);

        // 逐字段比对（Id/DeviceId 不在 CSV 暴露，由 ApplyImportedAlarms 注入后另行验证）
        var first = result.Imported[0];
        Assert.Equal("门未关", first.Name);
        Assert.Equal("M100", first.PlcAddress);
        Assert.Equal(AlarmLevel.High, first.Level);
        Assert.Equal("安全门未关闭", first.Description);

        var second = result.Imported[1];
        Assert.Equal("气压低", second.Name);
        Assert.Equal("M101", second.PlcAddress);
        Assert.Equal(AlarmLevel.Medium, second.Level);
        Assert.Equal("气压低于阈值", second.Description);
    }

    [Fact]
    public void Export_EmptyAlarms_StillWritesHeaderOnlyFile()
    {
        var device = new Device { Id = "dev1", Name = "空设备" };
        var exportPath = Path.Combine(_tempDir, "empty.csv");
        _dialog.SaveFilePath = exportPath;

        var exported = _service.ExportAlarms(device);

        Assert.True(exported);
        Assert.True(File.Exists(exportPath));
        // 文件首行为表头，无数据行
        var lines = File.ReadAllLines(exportPath);
        Assert.True(lines.Length >= 1);
        Assert.Contains("Name", lines[0]);
        Assert.Contains(_dialog.Success, s => s.Contains("已导出 0 条报警"));
    }

    [Fact]
    public void Export_UserCancels_ReturnsFalse()
    {
        var device = new Device { Id = "dev1", Name = "设备" };
        device.Alarms.Add(new Alarm { Name = "a", PlcAddress = "M1", DeviceId = "dev1" });
        _dialog.SaveFilePath = null; // 用户取消

        var exported = _service.ExportAlarms(device);

        Assert.False(exported);
        Assert.Empty(_dialog.Success);
        Assert.Empty(_dialog.Error);
    }

    // ───────────── 校验 ─────────────

    [Fact]
    public void ParseAndValidate_EmptyName_ReportsError()
    {
        var path = WriteCsv("Name,PlcAddress,Level,Description\n,M100,High,desc");
        var result = _service.ParseAndValidate(path);

        Assert.True(result.HasErrors);
        Assert.Empty(result.Imported);
        Assert.Contains(result.Errors, e => e.Contains("第 2 行") && e.Contains("报警名称为空"));
    }

    [Fact]
    public void ParseAndValidate_EmptyPlcAddress_ReportsError()
    {
        var path = WriteCsv("Name,PlcAddress,Level,Description\n报警1,,High,desc");
        var result = _service.ParseAndValidate(path);

        Assert.True(result.HasErrors);
        Assert.Empty(result.Imported);
        Assert.Contains(result.Errors, e => e.Contains("第 2 行") && e.Contains("PLC 地址为空"));
    }

    [Fact]
    public void ParseAndValidate_InvalidPlcAddress_ReportsError()
    {
        // D100 是字地址，报警必须是 M 位地址
        var path = WriteCsv("Name,PlcAddress,Level,Description\n报警1,D100,High,desc");
        var result = _service.ParseAndValidate(path);

        Assert.True(result.HasErrors);
        Assert.Empty(result.Imported);
        Assert.Contains(result.Errors, e => e.Contains("第 2 行") && e.Contains("应为 M 位类型"));
    }

    [Fact]
    public void ParseAndValidate_UnparseableAddress_ReportsError()
    {
        var path = WriteCsv("Name,PlcAddress,Level,Description\n报警1,XYZ,High,desc");
        var result = _service.ParseAndValidate(path);

        Assert.True(result.HasErrors);
        Assert.Empty(result.Imported);
        Assert.Contains(result.Errors, e => e.Contains("第 2 行") && e.Contains("无效"));
    }

    [Fact]
    public void ParseAndValidate_InvalidLevel_ReportsError()
    {
        var path = WriteCsv("Name,PlcAddress,Level,Description\n报警1,M100,Critical,desc");
        var result = _service.ParseAndValidate(path);

        Assert.True(result.HasErrors);
        Assert.Empty(result.Imported);
        Assert.Contains(result.Errors, e => e.Contains("第 2 行") && e.Contains("报警级别") && e.Contains("Critical"));
    }

    [Fact]
    public void ParseAndValidate_LevelCaseInsensitive_ParsesSuccessfully()
    {
        // Excel 用户可能输入小写级别，应容错解析
        var path = WriteCsv("Name,PlcAddress,Level,Description\n报警1,M100,high,desc\n报警2,M101,LOW,desc2");
        var result = _service.ParseAndValidate(path);

        Assert.False(result.HasErrors);
        Assert.Equal(2, result.Imported.Count);
        Assert.Equal(AlarmLevel.High, result.Imported[0].Level);
        Assert.Equal(AlarmLevel.Low, result.Imported[1].Level);
    }

    [Fact]
    public void ParseAndValidate_PartialFailures_ImportsValidRecords()
    {
        // 第 2 行合法、第 3 行地址非法、第 4 行合法：应导入 2 条，错误 1 条
        var csv = "Name,PlcAddress,Level,Description\n"
                + "合法报警,M100,High,ok\n"
                + "非法报警,D200,High,bad\n"
                + "合法报警2,M101,Medium,ok2\n";
        var path = WriteCsv(csv);
        var result = _service.ParseAndValidate(path);

        Assert.True(result.HasErrors);
        Assert.Equal(2, result.Imported.Count);
        Assert.Single(result.Errors);
        Assert.Contains(result.Errors, e => e.Contains("第 3 行"));
        Assert.Equal("合法报警", result.Imported[0].Name);
        Assert.Equal("合法报警2", result.Imported[1].Name);
    }

    [Fact]
    public void ParseAndValidate_HeaderCaseInsensitive_ParsesSuccessfully()
    {
        // Excel 手工保存可能列名大小写不一致（如全小写），CsvConfig.PrepareHeaderForMatch 已配置容错
        var path = WriteCsv("name,plcaddress,level,description\n报警1,M100,High,desc");
        var result = _service.ParseAndValidate(path);

        Assert.False(result.HasErrors);
        Assert.Single(result.Imported);
        Assert.Equal("报警1", result.Imported[0].Name);
    }

    // ───────────── 空文件 ─────────────

    [Fact]
    public void ParseAndValidate_EmptyDataFile_ReportsNoDataError()
    {
        var path = WriteCsv("Name,PlcAddress,Level,Description\n");
        var result = _service.ParseAndValidate(path);

        Assert.True(result.HasErrors);
        Assert.Empty(result.Imported);
        Assert.Contains(result.Errors, e => e.Contains("没有报警数据"));
    }

    [Fact]
    public void ParseAndValidate_NonExistentFile_ReportsReadError()
    {
        var path = Path.Combine(_tempDir, "does_not_exist.csv");
        var result = _service.ParseAndValidate(path);

        Assert.True(result.HasErrors);
        Assert.Empty(result.Imported);
        Assert.Contains(result.Errors, e => e.Contains("文件读取或解析失败"));
    }

    // ───────────── 重复地址处理 ─────────────

    [Fact]
    public void ParseAndValidate_DuplicatePlcAddressInCsv_ImportsBothRecords()
    {
        // CSV 内重复地址：ParseAndValidate 仅做校验不去做重，两条都进入 Imported
        // 重由 ApplyImportedAlarms 在应用阶段处理（后导入覆盖先导入）
        var path = WriteCsv("Name,PlcAddress,Level,Description\n"
                          + "报警A,M100,High,first\n"
                          + "报警B,M100,Medium,second\n");
        var result = _service.ParseAndValidate(path);

        Assert.False(result.HasErrors);
        Assert.Equal(2, result.Imported.Count);
    }

    [Fact]
    public void ApplyImportedAlarms_ReplaceMode_ClearsExistingAlarms()
    {
        var device = new Device { Id = "dev1", Name = "设备1" };
        device.Alarms.Add(new Alarm
        {
            DeviceId = device.Id,
            Name = "旧报警",
            PlcAddress = "M200",
            Level = AlarmLevel.Low,
        });

        var imported = new List<Alarm>
        {
            new() { Name = "新报警1", PlcAddress = "M100", Level = AlarmLevel.High },
            new() { Name = "新报警2", PlcAddress = "M101", Level = AlarmLevel.Medium },
        };

        _service.ApplyImportedAlarms(device, imported, replace: true);

        Assert.Equal(2, device.Alarms.Count);
        Assert.DoesNotContain(device.Alarms, a => a.PlcAddress == "M200");
        Assert.All(device.Alarms, a => Assert.Equal("dev1", a.DeviceId));
        Assert.All(device.Alarms, a => Assert.Equal($"dev1_{a.PlcAddress}", a.Id));
    }

    [Fact]
    public void ApplyImportedAlarms_AppendMode_KeepsExistingAndOverwritesSameAddress()
    {
        var device = new Device { Id = "dev1", Name = "设备1" };
        var existingAlarm = new Alarm
        {
            DeviceId = device.Id,
            Name = "原M100报警",
            PlcAddress = "M100",
            Level = AlarmLevel.Low,
            Description = "原描述",
        };
        device.Alarms.Add(existingAlarm);
        device.Alarms.Add(new Alarm
        {
            DeviceId = device.Id,
            Name = "保留报警",
            PlcAddress = "M200",
            Level = AlarmLevel.Low,
        });

        var imported = new List<Alarm>
        {
            // M100 覆盖现有项的字段（Id 保留以续接历史数据）
            new() { Name = "新M100报警", PlcAddress = "M100", Level = AlarmLevel.High, Description = "新描述" },
            // M101 是新地址，追加
            new() { Name = "新M101报警", PlcAddress = "M101", Level = AlarmLevel.Medium },
        };

        _service.ApplyImportedAlarms(device, imported, replace: false);

        Assert.Equal(3, device.Alarms.Count);
        // M100 字段被覆盖但 Id 保留
        var m100 = device.Alarms.First(a => a.PlcAddress == "M100");
        Assert.Equal("新M100报警", m100.Name);
        Assert.Equal(AlarmLevel.High, m100.Level);
        Assert.Equal("新描述", m100.Description);
        Assert.Same(existingAlarm, m100); // 同一对象引用（覆盖而非新增）
        // M200 保留
        Assert.Contains(device.Alarms, a => a.PlcAddress == "M200" && a.Name == "保留报警");
        // M101 新增
        Assert.Contains(device.Alarms, a => a.PlcAddress == "M101" && a.Name == "新M101报警");
    }

    [Fact]
    public void ApplyImportedAlarms_DuplicateAddressInImported_LastWinsInAppendMode()
    {
        var device = new Device { Id = "dev1", Name = "设备1" };
        // CSV 内两行同 PlcAddress=M100，最后一行应覆盖第一行（在 AppendToExisting 阶段）
        var imported = new List<Alarm>
        {
            new() { Name = "first", PlcAddress = "M100", Level = AlarmLevel.Low },
            new() { Name = "second", PlcAddress = "M100", Level = AlarmLevel.High },
        };

        _service.ApplyImportedAlarms(device, imported, replace: false);

        Assert.Single(device.Alarms);
        Assert.Equal("second", device.Alarms[0].Name);
        Assert.Equal(AlarmLevel.High, device.Alarms[0].Level);
    }

    [Fact]
    public void ApplyImportedAlarms_SiemensAliases_UseCanonicalAddress()
    {
        var settings = new AppSettings
        {
            PlcConfig = new PlcConfig { Brand = PlcBrand.Siemens },
        };
        var codecResolver = new PlcAddressCodecResolver(settings);
        var service = new AlarmCsvIOService(_dialog, codecResolver);
        var device = new Device { Id = "dev1", Name = "设备1" };
        var existingAlarm = new Alarm
        {
            DeviceId = device.Id,
            Name = "原报警",
            PlcAddress = "DB1.100",
            Level = AlarmLevel.Low,
        };
        device.Alarms.Add(existingAlarm);

        service.ApplyImportedAlarms(device, [new Alarm
        {
            Name = "新报警",
            PlcAddress = "DB1.DBD100",
            Level = AlarmLevel.High,
        }], replace: false);

        Assert.Single(device.Alarms);
        Assert.Same(existingAlarm, device.Alarms[0]);
        Assert.Equal("新报警", existingAlarm.Name);
        Assert.Equal(AlarmLevel.High, existingAlarm.Level);
    }

    [Fact]
    public void ApplyImportedAlarms_GeneratesDeterministicId()
    {
        var device = new Device { Id = "dev-xyz", Name = "设备" };
        var imported = new List<Alarm>
        {
            new() { Name = "a", PlcAddress = "M100", Level = AlarmLevel.High },
        };

        _service.ApplyImportedAlarms(device, imported, replace: true);

        Assert.Single(device.Alarms);
        Assert.Equal("dev-xyz_M100", device.Alarms[0].Id);
        Assert.Equal("dev-xyz", device.Alarms[0].DeviceId);
    }

    // ───────────── PickImportPath ─────────────

    [Fact]
    public void PickImportPath_UserCancels_ReturnsNull()
    {
        _dialog.OpenFilePath = null;
        var path = _service.PickImportPath();
        Assert.Null(path);
    }

    [Fact]
    public void PickImportPath_ReturnsSelectedPath()
    {
        var expected = Path.Combine(_tempDir, "selected.csv");
        _dialog.OpenFilePath = expected;
        var path = _service.PickImportPath();
        Assert.Equal(expected, path);
    }

    // ───────────── 辅助方法 ─────────────

    /// <summary>写入 UTF-8 BOM 的 CSV 文件（与 ExportAlarms 编码一致），便于 ParseAndValidate 读取。</summary>
    private string WriteCsv(string content)
    {
        var path = Path.Combine(_tempDir, "test_" + Guid.NewGuid().ToString("N") + ".csv");
        File.WriteAllText(path, content, new UTF8Encoding(true));
        return path;
    }
}
