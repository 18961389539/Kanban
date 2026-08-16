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
/// 缺陷 CSV 导入/导出记录（用于 CsvHelper 序列化的 DTO）。
/// 仅包含用户可编辑的标量字段：Id/DeviceId 由导入逻辑注入，不在 CSV 中暴露。
/// Severity 用英文枚举名（Minor/Major/Critical），Excel 输入友好且无编码问题。
/// Category 用英文枚举名（Appearance/Dimension/Function/Packaging/Other）。
/// </summary>
public sealed class DefectCsvRecord
{
    public string Name { get; set; } = string.Empty;
    public string PlcAddress { get; set; } = string.Empty;
    public string Severity { get; set; } = "Major";
    public string Category { get; set; } = "Other";
}

/// <summary>
/// 缺陷 CSV 导入结果：包含成功导入的缺陷列表与校验失败的行错误。
/// </summary>
public sealed class DefectCsvImportResult
{
    public List<Defect> Imported { get; } = new();
    public List<string> Errors { get; } = new();

    public bool HasErrors => Errors.Count > 0;
}

/// <summary>
/// 缺陷 CSV 导入/导出服务：基于 CsvHelper，用于设备管理页缺陷 Tab 的批量编辑。
/// 导出：当前选中设备的全部缺陷 → CSV（4 列：Name, PlcAddress, Severity, Category）。
/// 导入：CSV → 校验（PlcAddress 格式 + Severity/Category 枚举）→ 追加或替换当前设备缺陷列表。
/// Id/DeviceId 不在 CSV 中暴露：导入时 DeviceId 注入当前设备、Id 自动生成。
/// </summary>
public class DefectCsvIOService(
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
    /// 导出当前设备的全部缺陷到用户选择的 CSV 文件。
    /// 文件编码 UTF-8 with BOM（避免 Excel 中文乱码）。
    /// </summary>
    public bool ExportDefects(Device device)
    {
        var defaultFileName = $"defects_{device.Name}_{System.DateTime.Now:yyyyMMddHHmm}.csv";
        var path = _dialog.ShowSaveFileDialog(Strings.M311, defaultFileName, Strings.M310);
        if (string.IsNullOrEmpty(path)) return false;

        try
        {
            var records = device.Defects
                .Select(d => new DefectCsvRecord
                {
                    Name = d.Name,
                    PlcAddress = d.PlcAddress,
                    Severity = d.Severity.ToString(),
                    Category = d.Category.ToString(),
                })
                .ToList();

            using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
            using var csv = new CsvWriter(writer, CsvConfig);
            csv.WriteRecords(records);

            _dialog.NotifySuccess(string.Format(Strings.F293, records.Count, Path.GetFileName(path)));
            return true;
        }
        catch (Exception ex)
        {
            _dialog.NotifyError(string.Format(Strings.F090, ex.Message));
            return false;
        }
    }

    /// <summary>仅弹出文件选择对话框，返回用户选择的路径（UI 线程调用）。</summary>
    public string? PickImportPath()
        => _dialog.ShowOpenFileDialog(Strings.M312, Strings.M310);

    /// <summary>
    /// 读取并校验指定路径的 CSV 文件，返回校验通过的缺陷列表与失败行错误。
    /// 纯 CPU/IO 操作，可在线程池执行（无 UI 依赖）。不修改设备状态。
    /// </summary>
    public DefectCsvImportResult ParseAndValidate(string path)
    {
        var result = new DefectCsvImportResult();
        try
        {
            using var reader = new StreamReader(path, Encoding.UTF8);
            using var csv = new CsvReader(reader, CsvConfig);
            var records = csv.GetRecords<DefectCsvRecord>().ToList();

            if (records.Count == 0)
            {
                result.Errors.Add(Strings.F303);
                return result;
            }

            for (int i = 0; i < records.Count; i++)
            {
                var rec = records[i];
                var rowNum = i + 2;

                if (string.IsNullOrWhiteSpace(rec.Name))
                {
                    result.Errors.Add(string.Format(Strings.F299, rowNum));
                    continue;
                }
                if (string.IsNullOrWhiteSpace(rec.PlcAddress))
                {
                    result.Errors.Add(string.Format(Strings.F183, rowNum));
                    continue;
                }

                // PLC 地址格式校验：必须为 DWord 类型（缺陷地址是数值存储区，与 DeviceManagerDefectsTab ValidationRule 一致）
                var parseResult = CurrentCodec.Parse(rec.PlcAddress.Trim());
                if (!parseResult.IsValid)
                {
                    result.Errors.Add(string.Format(Strings.F182, rowNum, rec.PlcAddress, parseResult.ErrorMessage));
                    continue;
                }
                if (parseResult.Type != PlcAddressType.DWord)
                {
                    result.Errors.Add(string.Format(Strings.F302, rowNum, rec.PlcAddress, parseResult.Type));
                    continue;
                }

                if (!Enum.TryParse<DefectSeverity>(rec.Severity, ignoreCase: true, out var severity))
                {
                    result.Errors.Add(string.Format(Strings.F300, rowNum, rec.Severity));
                    continue;
                }

                if (!Enum.TryParse<DefectCategory>(rec.Category, ignoreCase: true, out var category))
                {
                    result.Errors.Add(string.Format(Strings.F301, rowNum, rec.Category));
                    continue;
                }

                result.Imported.Add(new Defect
                {
                    Name = rec.Name.Trim(),
                    PlcAddress = rec.PlcAddress.Trim(),
                    Severity = severity,
                    Category = category,
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
    /// 将导入的缺陷列表应用到目标设备（追加或替换模式）。
    /// 同 PlcAddress 的缺陷去重：后导入的覆盖先导入的。
    /// 不调用 SaveAll：统一由用户点保存按钮持久化。
    /// </summary>
    public void ApplyImportedDefects(Device device, List<Defect> imported, bool replace)
    {
        if (replace)
        {
            device.Defects.Clear();
        }

        var codec = CurrentCodec;
        var existingByCanonicalAddress = new Dictionary<string, Defect>(StringComparer.OrdinalIgnoreCase);
        foreach (var existing in device.Defects)
        {
            var key = codec.CanonicalKey(existing.PlcAddress);
            if (!string.IsNullOrWhiteSpace(key))
                existingByCanonicalAddress[key] = existing;
        }

        foreach (var defect in imported)
        {
            defect.DeviceId = device.Id;

            var canonicalAddress = codec.CanonicalKey(defect.PlcAddress);
            if (existingByCanonicalAddress.TryGetValue(canonicalAddress, out var existing))
            {
                existing.Name = defect.Name;
                existing.Severity = defect.Severity;
                existing.Category = defect.Category;
            }
            else
            {
                device.Defects.Add(defect);
                existingByCanonicalAddress[canonicalAddress] = defect;
            }
        }
    }
}
