using Kanban.Collector.Core.Services;
using Kanban.Collector.Core.Models;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using MainAPP.Resources;

namespace MainAPP.Services;

/// <summary>
/// 计数报警 CSV 导入/导出记录（用于 CsvHelper 序列化的 DTO）。
/// 仅包含用户可编辑的标量字段：Id/DeviceId 由导入逻辑注入，不在 CSV 中暴露。
/// Enabled 导出为当前界面语言；导入同时接受四种界面语言、True/False 和 1/0。
/// </summary>
public sealed class CounterAlarmCsvRecord
{
    public string Name { get; set; } = string.Empty;
    public string PlcAddress { get; set; } = string.Empty;
    public int MaxValue { get; set; }
    public string Enabled { get; set; } = "True";
    public string Unit { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? NameEn { get; set; }
    public string? NameJa { get; set; }
    public string? NamePt { get; set; }
}

internal sealed class CounterAlarmCsvRecordMap : ClassMap<CounterAlarmCsvRecord>
{
    public CounterAlarmCsvRecordMap()
    {
        Map(record => record.Name).Name(CsvLocalization.HeaderAliases("Csv_CounterAlarm_Name", nameof(CounterAlarmCsvRecord.Name)));
        Map(record => record.PlcAddress).Name(CsvLocalization.HeaderAliases("Csv_CounterAlarm_PlcAddress", nameof(CounterAlarmCsvRecord.PlcAddress)));
        Map(record => record.MaxValue).Name(CsvLocalization.HeaderAliases("Csv_CounterAlarm_MaxValue", nameof(CounterAlarmCsvRecord.MaxValue)));
        Map(record => record.Enabled).Name(CsvLocalization.HeaderAliases("Csv_CounterAlarm_Enabled", nameof(CounterAlarmCsvRecord.Enabled)));
        Map(record => record.Unit).Name(CsvLocalization.HeaderAliases("Csv_CounterAlarm_Unit", nameof(CounterAlarmCsvRecord.Unit)));
        Map(record => record.Description).Name(CsvLocalization.HeaderAliases("Csv_CounterAlarm_Description", nameof(CounterAlarmCsvRecord.Description)));
        Map(record => record.NameEn).Name(CsvLocalization.HeaderAliases("Csv_CounterAlarm_NameEn", nameof(CounterAlarmCsvRecord.NameEn))).Optional();
        Map(record => record.NameJa).Name(CsvLocalization.HeaderAliases("Csv_CounterAlarm_NameJa", nameof(CounterAlarmCsvRecord.NameJa))).Optional();
        Map(record => record.NamePt).Name(CsvLocalization.HeaderAliases("Csv_CounterAlarm_NamePt", nameof(CounterAlarmCsvRecord.NamePt))).Optional();
    }
}

/// <summary>
/// 计数报警 CSV 导入结果：包含成功导入的计数报警列表与校验失败的行错误。
/// </summary>
public sealed class CounterAlarmCsvImportResult
{
    public List<CounterAlarm> Imported { get; } = new();
    public List<string> Errors { get; } = new();

    public bool HasErrors => Errors.Count > 0;
}

/// <summary>
/// 计数报警 CSV 导入/导出服务：基于 CsvHelper，用于设备管理页计数报警 Tab 的批量编辑。
/// 导出：当前选中设备的全部计数报警 → CSV（6 列：Name, PlcAddress, MaxValue, Enabled, Unit, Description）。
/// 导入：CSV → 校验（PlcAddress 格式 + MaxValue 整数 + Enabled 布尔）→ 追加或替换当前设备计数报警列表。
/// Id/DeviceId 不在 CSV 中暴露：导入时 DeviceId 注入当前设备、Id 自动生成。
/// </summary>
public class CounterAlarmCsvIOService(
    IDialogService dialog,
    IPlcAddressCodecResolver? codecResolver = null,
    IPlcRuntimeProfileProvider? profileProvider = null)
{
    private static readonly CsvConfiguration CsvConfig = new(CultureInfo.InvariantCulture)
    {
        Comment = '#',
        HasHeaderRecord = true,
        MissingFieldFound = null,
        PrepareHeaderForMatch = args => args.Header.Trim().ToLowerInvariant(),
    };

    private readonly IDialogService _dialog = dialog;
    private readonly IPlcAddressCodecResolver? _codecResolver = codecResolver;
    private readonly IPlcRuntimeProfileProvider? _profileProvider = profileProvider;
    private readonly IPlcAddressCodec _fallbackCodec = new MitsubishiAddressCodec();

    private IPlcAddressCodec CurrentCodec =>
        _profileProvider?.Current.AddressCodec ?? _codecResolver?.Current ?? _fallbackCodec;

    /// <summary>
    /// 导出当前设备的全部计数报警到用户选择的 CSV 文件。
    /// 文件编码 UTF-8 with BOM（避免 Excel 中文乱码）。
    /// </summary>
    public bool ExportCounterAlarms(Device device)
    {
        var path = PickExportPath(device);
        if (string.IsNullOrEmpty(path)) return false;

        try
        {
            var count = ExportCounterAlarmsToPath(device, path);
            _dialog.NotifySuccess(string.Format(Strings.F304, count, Path.GetFileName(path)));
            return true;
        }
        catch (Exception ex)
        {
            _dialog.NotifyError(string.Format(Strings.F090, ex.Message));
            return false;
        }
    }

    /// <summary>仅在 UI 线程弹出导出文件对话框。</summary>
    public string? PickExportPath(Device device)
    {
        var defaultFileName = string.Format(
            CultureInfo.CurrentCulture,
            Strings.Csv_CounterAlarm_FileName,
            device.Name,
            System.DateTime.Now);
        return _dialog.ShowSaveFileDialog(Strings.M313, defaultFileName, Strings.M310);
    }

    /// <summary>将计数报警写入指定路径；只执行数据转换和文件 IO，可在线程池执行。</summary>
    public int ExportCounterAlarmsToPath(Device device, string path)
    {
        var records = device.CounterAlarms
            .Select(c => new CounterAlarmCsvRecord
            {
                Name = c.Name,
                PlcAddress = c.PlcAddress,
                MaxValue = c.MaxValue,
                Enabled = CsvLocalization.BooleanText(c.Enabled),
                Unit = c.Unit,
                Description = c.Description,
                NameEn = c.NameEn,
                NameJa = c.NameJa,
                NamePt = c.NamePt,
            })
            .ToList();

        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        using var csv = new CsvWriter(writer, CsvConfig);
        csv.Context.RegisterClassMap<CounterAlarmCsvRecordMap>();
        csv.WriteRecords(records);
        return records.Count;
    }

    /// <summary>仅弹出文件选择对话框，返回用户选择的路径（UI 线程调用）。</summary>
    public string? PickImportPath()
        => _dialog.ShowOpenFileDialog(Strings.M314, Strings.M310);

    /// <summary>
    /// 读取并校验指定路径的 CSV 文件，返回校验通过的计数报警列表与失败行错误。
    /// 纯 CPU/IO 操作，可在线程池执行（无 UI 依赖）。不修改设备状态。
    /// </summary>
    public CounterAlarmCsvImportResult ParseAndValidate(string path)
    {
        var result = new CounterAlarmCsvImportResult();
        try
        {
            using var reader = new StreamReader(path, Encoding.UTF8);
            using var csv = new CsvReader(reader, CsvConfig);
            csv.Context.RegisterClassMap<CounterAlarmCsvRecordMap>();
            var records = csv.GetRecords<CounterAlarmCsvRecord>().ToList();

            if (records.Count == 0)
            {
                result.Errors.Add(Strings.F314);
                return result;
            }

            for (int i = 0; i < records.Count; i++)
            {
                var rec = records[i];
                var rowNum = i + 2;

                if (string.IsNullOrWhiteSpace(rec.Name))
                {
                    result.Errors.Add(string.Format(Strings.F310, rowNum));
                    continue;
                }
                if (string.IsNullOrWhiteSpace(rec.PlcAddress))
                {
                    result.Errors.Add(string.Format(Strings.F183, rowNum));
                    continue;
                }

                // PLC 地址格式校验：必须为 DWord 类型（计数报警地址是数值存储区，与 DeviceManagerCounterAlarmsTab ValidationRule 一致）
                var parseResult = CurrentCodec.Parse(rec.PlcAddress.Trim());
                if (!parseResult.IsValid)
                {
                    result.Errors.Add(string.Format(Strings.F182, rowNum, rec.PlcAddress, parseResult.ErrorMessage));
                    continue;
                }
                if (parseResult.Type != PlcAddressType.DWord)
                {
                    result.Errors.Add(string.Format(Strings.F313, rowNum, rec.PlcAddress, parseResult.Type));
                    continue;
                }

                // 接受当前语言、其它内置语言，以及旧版 True/False 文本。
                if (!CsvLocalization.TryParseBoolean(rec.Enabled, defaultValue: false, out var enabled))
                {
                    result.Errors.Add(string.Format(Strings.F312, rowNum, rec.Enabled));
                    continue;
                }

                result.Imported.Add(new CounterAlarm
                {
                    Name = rec.Name.Trim(),
                    PlcAddress = rec.PlcAddress.Trim(),
                    MaxValue = rec.MaxValue,
                    Enabled = enabled,
                    Unit = rec.Unit ?? string.Empty,
                    Description = rec.Description ?? string.Empty,
                    NameEn = string.IsNullOrWhiteSpace(rec.NameEn) ? null : rec.NameEn.Trim(),
                    NameJa = string.IsNullOrWhiteSpace(rec.NameJa) ? null : rec.NameJa.Trim(),
                    NamePt = string.IsNullOrWhiteSpace(rec.NamePt) ? null : rec.NamePt.Trim(),
                });
            }
        }
        catch (Exception ex)
        {
            result.Errors.Add(string.Format(Strings.F134, ex.Message));
        }

        return result;
    }

    /// <summary>
    /// 将导入的计数报警列表应用到目标设备（追加或替换模式）。
    /// 同 PlcAddress 的计数报警去重：后导入的覆盖先导入的。
    /// 不调用 SaveAll：统一由用户点保存按钮持久化。
    /// </summary>
    public void ApplyImportedCounterAlarms(Device device, List<CounterAlarm> imported, bool replace)
    {
        if (replace)
        {
            device.CounterAlarms.Clear();
        }

        var codec = CurrentCodec;
        var existingByCanonicalAddress = new Dictionary<string, CounterAlarm>(StringComparer.OrdinalIgnoreCase);
        foreach (var existing in device.CounterAlarms)
        {
            var key = codec.CanonicalKey(existing.PlcAddress);
            if (!string.IsNullOrWhiteSpace(key))
                existingByCanonicalAddress[key] = existing;
        }

        foreach (var alarm in imported)
        {
            alarm.DeviceId = device.Id;

            var canonicalAddress = codec.CanonicalKey(alarm.PlcAddress);
            if (existingByCanonicalAddress.TryGetValue(canonicalAddress, out var existing))
            {
                existing.Name = alarm.Name;
                existing.MaxValue = alarm.MaxValue;
                existing.Enabled = alarm.Enabled;
                existing.Unit = alarm.Unit;
                existing.Description = alarm.Description;
                existing.NameEn = alarm.NameEn;
                existing.NameJa = alarm.NameJa;
                existing.NamePt = alarm.NamePt;
            }
            else
            {
                device.CounterAlarms.Add(alarm);
                existingByCanonicalAddress[canonicalAddress] = alarm;
            }
        }
    }
}
