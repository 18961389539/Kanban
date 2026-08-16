using Kanban.Collector.Core.Services;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using MainAPP.Models;
using MainAPP.Resources;

namespace MainAPP.Services;

/// <summary>
/// 报警 CSV 导入/导出记录（用于 CsvHelper 序列化的 DTO）。
/// 仅包含用户可编辑的标量字段：Id/DeviceId 由导入逻辑注入，不在 CSV 中暴露。
/// Level 用英文枚举名（Low/Medium/High），Excel 输入友好且无编码问题。
/// </summary>
public sealed class AlarmCsvRecord
{
    public string Name { get; set; } = string.Empty;
    public string PlcAddress { get; set; } = string.Empty;
    public string Level { get; set; } = "Medium";
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// 报警 CSV 导入结果：包含成功导入的报警列表与校验失败的行错误。
/// 调用方据 Success/Errors 决定提示策略（全部成功→NotifySuccess；部分失败→NotifyWarning 含明细）。
/// </summary>
public sealed class AlarmCsvImportResult
{
    public List<Alarm> Imported { get; } = new();
    public List<string> Errors { get; } = new();

    public bool HasErrors => Errors.Count > 0;
}

/// <summary>
/// 报警 CSV 导入/导出服务：基于 CsvHelper，用于设备管理页报警 Tab 的批量编辑。
/// 导出：当前选中设备的全部报警 → CSV（4 列：Name, PlcAddress, Level, Description）。
/// 导入：CSV → 校验（PlcAddress 格式 + 级别枚举）→ 追加或替换当前设备报警列表。
/// Id/DeviceId 不在 CSV 中暴露：导入时 DeviceId 注入当前设备、Id 由 OnPlcAddressChanged 自动重生成。
/// </summary>
public class AlarmCsvIOService(
    IDialogService dialog,
    IPlcAddressCodecResolver? codecResolver = null,
    IPlcRuntimeProfileProvider? profileProvider = null)
{
    // 文件对话框过滤器（三语资源，与 DefectCsvIOService/CounterAlarmCsvIOService 同源 M310）
    private static string CsvFileFilter => Strings.M310;

    private static readonly CsvConfiguration CsvConfig = new(CultureInfo.InvariantCulture)
    {
        // 注释行以 # 开头（与现有 CSV 导出风格一致，便于嵌入 KPI 摘要等元信息）
        Comment = '#',
        HasHeaderRecord = true,
        // 缺失字段不抛异常，按 null 处理（容错老版 CSV 或手工编辑的缺列文件）
        MissingFieldFound = null,
        // 头部大小写不敏感，便于 Excel 手工保存时列名大小写不一致
        PrepareHeaderForMatch = args => args.Header.Trim().ToLowerInvariant(),
    };

    private readonly IDialogService _dialog = dialog;
    private readonly IPlcAddressCodecResolver? _codecResolver = codecResolver;
    private readonly IPlcRuntimeProfileProvider? _profileProvider = profileProvider;
    private readonly IPlcAddressCodec _fallbackCodec = new MitsubishiAddressCodec();

    private IPlcAddressCodec CurrentCodec =>
        _profileProvider?.Current.AddressCodec ?? _codecResolver?.Current ?? _fallbackCodec;

    /// <summary>
    /// 导出当前设备的全部报警到用户选择的 CSV 文件。
    /// 文件编码 UTF-8 with BOM（与 HistoryQuery 导出一致，避免 Excel 中文乱码）。
    /// 用户取消保存对话框则不写文件。
    /// </summary>
    /// <param name="device">当前选中设备，提供 DeviceId 与报警列表。</param>
    /// <returns>是否导出成功（用户取消或写盘失败返回 false）。</returns>
    public bool ExportAlarms(Device device)
    {
        var defaultFileName = $"报警_{device.Name}_{System.DateTime.Now:yyyyMMddHHmm}.csv";
        var path = _dialog.ShowSaveFileDialog(Strings.M222, defaultFileName, CsvFileFilter);
        if (string.IsNullOrEmpty(path)) return false;

        try
        {
            var records = device.Alarms
                .Select(a => new AlarmCsvRecord
                {
                    Name = a.Name,
                    PlcAddress = a.PlcAddress,
                    Level = a.Level.ToString(),
                    Description = a.Description,
                })
                .ToList();

            // UTF-8 with BOM：Excel 打开中文不乱码（与 HistoryQuery ExportAsync 一致）
            using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
            using var csv = new CsvWriter(writer, CsvConfig);
            csv.WriteRecords(records);

            _dialog.NotifySuccess(string.Format(Strings.F107, records.Count, Path.GetFileName(path)));
            return true;
        }
        catch (Exception ex)
        {
            _dialog.NotifyError(string.Format(Strings.F090, ex.Message));
            return false;
        }
    }

    /// <summary>
    /// 从用户选择的 CSV 文件导入报警，返回校验通过的报警列表（尚未注入到设备）。
    /// 调用方负责：二次确认（追加/替换）+ 实际写入 device.Alarms + 标记脏 + 持久化提示。
    /// 本方法只做读取、解析、校验，不修改设备状态，便于调用方在二次确认前预览结果。
    /// 注意：本方法调用 IDialogService.ShowOpenFileDialog（UI 线程）+ 文件读取（可后台），
    /// 若需异步执行，调用方应先用 PickImportPath() 在 UI 线程选文件，再 ParseAndValidate(path) 在后台。
    /// </summary>
    /// <returns>导入结果；用户取消返回 null。</returns>
    public AlarmCsvImportResult? ImportAlarms()
    {
        var path = _dialog.ShowOpenFileDialog(Strings.M223, CsvFileFilter);
        if (string.IsNullOrEmpty(path)) return null;

        return ParseAndValidate(path);
    }

    /// <summary>
    /// 仅弹出文件选择对话框，返回用户选择的路径（UI 线程调用）。
    /// 用户取消返回 null。供调用方拆分"选文件(UI)"与"解析校验(后台)"两阶段异步使用。
    /// </summary>
    public string? PickImportPath()
        => _dialog.ShowOpenFileDialog(Strings.M223, CsvFileFilter);

    /// <summary>
    /// 读取并校验指定路径的 CSV 文件，返回校验通过的报警列表与失败行错误。
    /// 纯 CPU/IO 操作，可在线程池执行（无 UI 依赖）。不修改设备状态。
    /// 文件读取或解析异常时通过返回 result.Errors 报告（不抛异常），便于后台调用。
    /// </summary>
    public AlarmCsvImportResult ParseAndValidate(string path)
    {
        var result = new AlarmCsvImportResult();
        try
        {
            using var reader = new StreamReader(path, Encoding.UTF8);
            using var csv = new CsvReader(reader, CsvConfig);
            var records = csv.GetRecords<AlarmCsvRecord>().ToList();

            if (records.Count == 0)
            {
                result.Errors.Add(Strings.F323);
                return result;
            }

            for (int i = 0; i < records.Count; i++)
            {
                var rec = records[i];
                var rowNum = i + 2; // CSV 行号：1 是表头，数据从第 2 行开始

                // 必填校验
                if (string.IsNullOrWhiteSpace(rec.Name))
                {
                    result.Errors.Add(string.Format(Strings.F184, rowNum));
                    continue;
                }
                if (string.IsNullOrWhiteSpace(rec.PlcAddress))
                {
                    result.Errors.Add(string.Format(Strings.F183, rowNum));
                    continue;
                }

                // PLC 地址格式校验：必须为 MBit 类型（与 DeviceManagerAlarmsTab 的 ValidationRule 一致）
                var parseResult = CurrentCodec.Parse(rec.PlcAddress.Trim());
                if (!parseResult.IsValid)
                {
                    result.Errors.Add(string.Format(Strings.F182, rowNum, rec.PlcAddress, parseResult.ErrorMessage));
                    continue;
                }
                if (parseResult.Type != PlcAddressType.MBit)
                {
                    result.Errors.Add(string.Format(Strings.F181, rowNum, rec.PlcAddress, parseResult.Type));
                    continue;
                }

                // 级别枚举解析（不区分大小写，容错 Excel 小写输入）
                if (!Enum.TryParse<AlarmLevel>(rec.Level, ignoreCase: true, out var level))
                {
                    result.Errors.Add(string.Format(Strings.F185, rowNum, rec.Level));
                    continue;
                }

                result.Imported.Add(new Alarm
                {
                    Name = rec.Name.Trim(),
                    PlcAddress = rec.PlcAddress.Trim(),
                    Level = level,
                    Description = rec.Description ?? string.Empty,
                    // DeviceId 由调用方在 ApplyImportedAlarms 中注入
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
    /// 将导入的报警列表应用到目标设备（追加或替换模式）。
    /// 注入 DeviceId 并重新触发 PlcAddress 赋值，使 OnPlcAddressChanged 生成确定性 Id（DeviceId_PlcAddress）。
    /// 同 PlcAddress 的报警去重：后导入的覆盖先导入的（与 CSV 内顺序一致）。
    /// 不调用 SaveAll：与 AddAlarm/RemoveAlarm 行为一致，统一由用户点保存按钮持久化。
    /// </summary>
    /// <param name="device">目标设备。</param>
    /// <param name="imported">校验通过的报警列表。</param>
    /// <param name="replace">true=清空现有报警后追加；false=保留现有、同 PlcAddress 覆盖。</param>
    public void ApplyImportedAlarms(Device device, List<Alarm> imported, bool replace)
    {
        if (replace)
        {
            // 清空时需清理采集服务中旧报警的内存状态（与 RemoveAlarm 一致）
            device.Alarms.Clear();
        }

        var codec = CurrentCodec;
        var existingByCanonicalAddress = new Dictionary<string, Alarm>(StringComparer.OrdinalIgnoreCase);
        foreach (var existing in device.Alarms)
        {
            var key = codec.CanonicalKey(existing.PlcAddress);
            if (!string.IsNullOrWhiteSpace(key))
                existingByCanonicalAddress[key] = existing;
        }

        // 追加模式：同 canonical PLC 地址覆盖现有项（CSV 内若重复，后行覆盖前行）
        foreach (var alarm in imported)
        {
            // 先注入 DeviceId，再重新赋值 PlcAddress 触发 OnPlcAddressChanged 生成确定性 Id
            // （Alarm 模型仅有 OnPlcAddressChanged，无 OnDeviceIdChanged，故需手动重触发）
            alarm.DeviceId = device.Id;
            var addr = alarm.PlcAddress;
            alarm.PlcAddress = string.Empty;
            alarm.PlcAddress = addr;

            var canonicalAddress = codec.CanonicalKey(alarm.PlcAddress);
            if (existingByCanonicalAddress.TryGetValue(canonicalAddress, out var existing))
            {
                // 覆盖现有项的可编辑字段（保留 Id 以续接历史数据）
                existing.Name = alarm.Name;
                existing.Level = alarm.Level;
                existing.Description = alarm.Description;
            }
            else
            {
                device.Alarms.Add(alarm);
                existingByCanonicalAddress[canonicalAddress] = alarm;
            }
        }
    }
}
