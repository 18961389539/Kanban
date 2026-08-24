using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using CsvHelper;
using CsvHelper.Configuration;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Resources;

namespace MainAPP.Services;

/// <summary>
/// 数据源配置 CSV 的一行记录。一个数据源的源级字段会在其每个值项行中重复。
/// 枚举映射使用 JSON 文本列保存，避免 CSV 失去多值配置。
/// </summary>
public sealed class DataSourceCsvRecord
{
    public string SourceName { get; set; } = string.Empty;
    public string SourceNameEn { get; set; } = string.Empty;
    public string SourceNameJa { get; set; } = string.Empty;
    public string SourceNamePt { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public string SourceEnabled { get; set; } = "True";
    public string SourceDescription { get; set; } = string.Empty;
    public string TriggerAddress { get; set; } = string.Empty;
    public string TriggerValue { get; set; } = "1";
    public string AckValue { get; set; } = "2";
    public string ValueName { get; set; } = string.Empty;
    public string ValueNameEn { get; set; } = string.Empty;
    public string ValueNameJa { get; set; } = string.Empty;
    public string ValueNamePt { get; set; } = string.Empty;
    public string ValueDataType { get; set; } = string.Empty;
    public string ValuePlcAddress { get; set; } = string.Empty;
    public string ValueUnit { get; set; } = string.Empty;
    public string ValueEnabled { get; set; } = string.Empty;
    public string LimitMin { get; set; } = string.Empty;
    public string LimitMax { get; set; } = string.Empty;
    public string FloatLimitMin { get; set; } = string.Empty;
    public string FloatLimitMax { get; set; } = string.Empty;
    public string Hysteresis { get; set; } = string.Empty;
    public string ConfirmSeconds { get; set; } = string.Empty;
    public string ExpectedValueConfigured { get; set; } = string.Empty;
    public string ExpectedValue { get; set; } = string.Empty;
    public string FloatExpectedValue { get; set; } = string.Empty;
    public string BoolExpectedValue { get; set; } = string.Empty;
    public string StringExpectedValue { get; set; } = string.Empty;
    public string StringLength { get; set; } = string.Empty;
    public string EnumValuesJson { get; set; } = string.Empty;
}

internal sealed class DataSourceCsvRecordMap : ClassMap<DataSourceCsvRecord>
{
    public DataSourceCsvRecordMap()
    {
        Map(record => record.SourceName).Name(CsvLocalization.HeaderAliases("Csv_DataSource_SourceName", nameof(DataSourceCsvRecord.SourceName)));
        Map(record => record.SourceNameEn).Name(CsvLocalization.HeaderAliases("Csv_DataSource_SourceNameEn", nameof(DataSourceCsvRecord.SourceNameEn))).Optional();
        Map(record => record.SourceNameJa).Name(CsvLocalization.HeaderAliases("Csv_DataSource_SourceNameJa", nameof(DataSourceCsvRecord.SourceNameJa))).Optional();
        Map(record => record.SourceNamePt).Name(CsvLocalization.HeaderAliases("Csv_DataSource_SourceNamePt", nameof(DataSourceCsvRecord.SourceNamePt))).Optional();
        Map(record => record.SourceType).Name(CsvLocalization.HeaderAliases("Csv_DataSource_SourceType", nameof(DataSourceCsvRecord.SourceType)));
        Map(record => record.SourceEnabled).Name(CsvLocalization.HeaderAliases("Csv_DataSource_SourceEnabled", nameof(DataSourceCsvRecord.SourceEnabled)));
        Map(record => record.SourceDescription).Name(CsvLocalization.HeaderAliases("Csv_DataSource_SourceDescription", nameof(DataSourceCsvRecord.SourceDescription)));
        Map(record => record.TriggerAddress).Name(CsvLocalization.HeaderAliases("Csv_DataSource_TriggerAddress", nameof(DataSourceCsvRecord.TriggerAddress)));
        Map(record => record.TriggerValue).Name(CsvLocalization.HeaderAliases("Csv_DataSource_TriggerValue", nameof(DataSourceCsvRecord.TriggerValue)));
        Map(record => record.AckValue).Name(CsvLocalization.HeaderAliases("Csv_DataSource_AckValue", nameof(DataSourceCsvRecord.AckValue)));
        Map(record => record.ValueName).Name(CsvLocalization.HeaderAliases("Csv_DataSource_ValueName", nameof(DataSourceCsvRecord.ValueName)));
        Map(record => record.ValueNameEn).Name(CsvLocalization.HeaderAliases("Csv_DataSource_ValueNameEn", nameof(DataSourceCsvRecord.ValueNameEn))).Optional();
        Map(record => record.ValueNameJa).Name(CsvLocalization.HeaderAliases("Csv_DataSource_ValueNameJa", nameof(DataSourceCsvRecord.ValueNameJa))).Optional();
        Map(record => record.ValueNamePt).Name(CsvLocalization.HeaderAliases("Csv_DataSource_ValueNamePt", nameof(DataSourceCsvRecord.ValueNamePt))).Optional();
        Map(record => record.ValueDataType).Name(CsvLocalization.HeaderAliases("Csv_DataSource_ValueDataType", nameof(DataSourceCsvRecord.ValueDataType)));
        Map(record => record.ValuePlcAddress).Name(CsvLocalization.HeaderAliases("Csv_DataSource_ValuePlcAddress", nameof(DataSourceCsvRecord.ValuePlcAddress)));
        Map(record => record.ValueUnit).Name(CsvLocalization.HeaderAliases("Csv_DataSource_ValueUnit", nameof(DataSourceCsvRecord.ValueUnit)));
        Map(record => record.ValueEnabled).Name(CsvLocalization.HeaderAliases("Csv_DataSource_ValueEnabled", nameof(DataSourceCsvRecord.ValueEnabled)));
        Map(record => record.LimitMin).Name(CsvLocalization.HeaderAliases("Csv_DataSource_LimitMin", nameof(DataSourceCsvRecord.LimitMin)));
        Map(record => record.LimitMax).Name(CsvLocalization.HeaderAliases("Csv_DataSource_LimitMax", nameof(DataSourceCsvRecord.LimitMax)));
        Map(record => record.FloatLimitMin).Name(CsvLocalization.HeaderAliases("Csv_DataSource_FloatLimitMin", nameof(DataSourceCsvRecord.FloatLimitMin)));
        Map(record => record.FloatLimitMax).Name(CsvLocalization.HeaderAliases("Csv_DataSource_FloatLimitMax", nameof(DataSourceCsvRecord.FloatLimitMax)));
        Map(record => record.Hysteresis).Name(CsvLocalization.HeaderAliases("Csv_DataSource_Hysteresis", nameof(DataSourceCsvRecord.Hysteresis)));
        Map(record => record.ConfirmSeconds).Name(CsvLocalization.HeaderAliases("Csv_DataSource_ConfirmSeconds", nameof(DataSourceCsvRecord.ConfirmSeconds)));
        Map(record => record.ExpectedValueConfigured).Name(CsvLocalization.HeaderAliases("Csv_DataSource_ExpectedValueConfigured", nameof(DataSourceCsvRecord.ExpectedValueConfigured)));
        Map(record => record.ExpectedValue).Name(CsvLocalization.HeaderAliases("Csv_DataSource_ExpectedValue", nameof(DataSourceCsvRecord.ExpectedValue)));
        Map(record => record.FloatExpectedValue).Name(CsvLocalization.HeaderAliases("Csv_DataSource_FloatExpectedValue", nameof(DataSourceCsvRecord.FloatExpectedValue)));
        Map(record => record.BoolExpectedValue).Name(CsvLocalization.HeaderAliases("Csv_DataSource_BoolExpectedValue", nameof(DataSourceCsvRecord.BoolExpectedValue)));
        Map(record => record.StringExpectedValue).Name(CsvLocalization.HeaderAliases("Csv_DataSource_StringExpectedValue", nameof(DataSourceCsvRecord.StringExpectedValue)));
        Map(record => record.StringLength).Name(CsvLocalization.HeaderAliases("Csv_DataSource_StringLength", nameof(DataSourceCsvRecord.StringLength)));
        Map(record => record.EnumValuesJson).Name(CsvLocalization.HeaderAliases("Csv_DataSource_EnumValuesJson", nameof(DataSourceCsvRecord.EnumValuesJson)));
    }
}

/// <summary>CSV 中的枚举映射项。</summary>
public sealed class DataSourceEnumCsvRecord
{
    public int Value { get; set; }
    public string DisplayName { get; set; } = string.Empty;
}

/// <summary>数据源 CSV 导出统计。</summary>
public readonly record struct DataSourceCsvExportSummary(int SourceCount, int ValueCount);

/// <summary>数据源 CSV 导入结果；Imported 中的设备归属由应用阶段注入。</summary>
public sealed class DataSourceCsvImportResult
{
    public List<DataSource> Imported { get; } = new();
    public List<string> Errors { get; } = new();
    public int ImportedValueCount { get; internal set; }
    public bool HasErrors => Errors.Count > 0;
}

/// <summary>
/// 设备管理数据源配置的 CSV 导入/导出服务。
/// 导出当前设备的源级配置与值项配置；导入只解析和校验，不修改设备，应用由调用方在确认后完成。
/// </summary>
public class DataSourceCsvIOService(
    IDialogService dialog,
    IPlcAddressCodecResolver? codecResolver = null,
    IPlcRuntimeProfileProvider? profileProvider = null)
{
    private static readonly CsvConfiguration CsvConfig = new(CultureInfo.InvariantCulture)
    {
        Comment = '#',
        HasHeaderRecord = true,
        HeaderValidated = null,
        MissingFieldFound = null,
        PrepareHeaderForMatch = args => args.Header.Trim().ToLowerInvariant(),
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IDialogService _dialog = dialog;
    private readonly IPlcAddressCodecResolver? _codecResolver = codecResolver;
    private readonly IPlcRuntimeProfileProvider? _profileProvider = profileProvider;
    private readonly IPlcAddressCodec _fallbackCodec = new MitsubishiAddressCodec();

    private IPlcAddressCodec CurrentCodec =>
        _profileProvider?.Current.AddressCodec ?? _codecResolver?.Current ?? _fallbackCodec;

    /// <summary>导出当前设备的数据源配置到用户选择的 CSV 文件。</summary>
    public bool ExportDataSources(Device device)
    {
        var path = PickExportPath(device);
        if (string.IsNullOrEmpty(path)) return false;

        try
        {
            var summary = ExportDataSourcesToPath(device, path);
            _dialog.NotifySuccess(string.Format(
                Strings.F331,
                summary.SourceCount,
                summary.ValueCount,
                Path.GetFileName(path)));
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
            Strings.Csv_DataSource_FileName,
            device.Name,
            DateTime.Now);
        return _dialog.ShowSaveFileDialog(Strings.M360, defaultFileName, Strings.M310);
    }

    /// <summary>将当前设备的数据源配置写入指定路径；可在线程池执行。</summary>
    public DataSourceCsvExportSummary ExportDataSourcesToPath(Device device, string path)
    {
        var records = device.Sources
            .SelectMany(source => source.Values.Count == 0
                ? [CreateRecord(source)]
                : source.Values.Select(value => CreateRecord(source, value)))
            .ToList();

        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        using var csv = new CsvWriter(writer, CsvConfig);
        csv.Context.RegisterClassMap<DataSourceCsvRecordMap>();
        csv.WriteRecords(records);
        return new DataSourceCsvExportSummary(device.Sources.Count, device.Sources.Sum(source => source.Values.Count));
    }

    /// <summary>仅弹出文件选择对话框，返回用户选择的路径。</summary>
    public string? PickImportPath()
        => _dialog.ShowOpenFileDialog(Strings.M361, Strings.M310);

    /// <summary>
    /// 读取并校验数据源 CSV。该方法不修改设备状态，可在线程池执行。
    /// 数据源以名称分组，值项以数据源名称和值项名称去重；ID 与运行时采样字段不会从 CSV 读取。
    /// </summary>
    public DataSourceCsvImportResult ParseAndValidate(string path)
    {
        var result = new DataSourceCsvImportResult();
        var sourceGroups = new Dictionary<string, ParsedSourceGroup>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var reader = new StreamReader(path, Encoding.UTF8);
            using var csv = new CsvReader(reader, CsvConfig);
            csv.Context.RegisterClassMap<DataSourceCsvRecordMap>();
            if (!csv.Read())
            {
                result.Errors.Add(Strings.F333);
                return result;
            }

            csv.ReadHeader();
            var headers = csv.HeaderRecord ?? [];
            var headerSet = headers
                .Select(header => header.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var requiredHeaderResources = new Dictionary<string, string>
            {
                [nameof(DataSourceCsvRecord.SourceName)] = "Csv_DataSource_SourceName",
                [nameof(DataSourceCsvRecord.ValueName)] = "Csv_DataSource_ValueName",
                [nameof(DataSourceCsvRecord.ValueDataType)] = "Csv_DataSource_ValueDataType",
                [nameof(DataSourceCsvRecord.ValuePlcAddress)] = "Csv_DataSource_ValuePlcAddress",
            };
            var missingHeaders = requiredHeaderResources
                .Where(pair => !CsvLocalization.HeaderAliases(pair.Value, pair.Key)
                    .Any(alias => headerSet.Contains(alias)))
                .Select(pair => pair.Key)
                .ToArray();
            if (missingHeaders.Length > 0)
            {
                result.Errors.Add(string.Format(Strings.F346, string.Join(", ", missingHeaders)));
                return result;
            }

            var recordIndex = 0;
            while (csv.Read())
            {
                recordIndex++;
                var rowNumber = recordIndex + 1;
                var record = csv.GetRecord<DataSourceCsvRecord>();
                if (record == null) continue;

                var sourceName = record.SourceName.Trim();
                if (string.IsNullOrWhiteSpace(sourceName))
                {
                    result.Errors.Add(string.Format(Strings.F334, rowNumber));
                    continue;
                }

                if (!CsvLocalization.TryParseBoolean(record.SourceEnabled, true, out var sourceEnabled))
                {
                    AddInvalidFieldError(result, rowNumber, nameof(record.SourceEnabled), record.SourceEnabled);
                    continue;
                }
                if (!TryParseInt(record.TriggerValue, 1, out var triggerValue))
                {
                    AddInvalidFieldError(result, rowNumber, nameof(record.TriggerValue), record.TriggerValue);
                    continue;
                }
                if (!TryParseInt(record.AckValue, 2, out var ackValue))
                {
                    AddInvalidFieldError(result, rowNumber, nameof(record.AckValue), record.AckValue);
                    continue;
                }

                var triggerAddress = record.TriggerAddress.Trim();
                if (!ValidateAddress(
                        triggerAddress,
                        PlcAddressType.DWord,
                        rowNumber,
                        result.Errors,
                        allowEmpty: true))
                    continue;

                var candidateSource = new DataSource
                {
                    Name = sourceName,
                    NameEn = NormalizeOrNull(record.SourceNameEn),
                    NameJa = NormalizeOrNull(record.SourceNameJa),
                    NamePt = NormalizeOrNull(record.SourceNamePt),
                    Type = record.SourceType.Trim(),
                    Enabled = sourceEnabled,
                    Description = record.SourceDescription,
                    TriggerAddress = triggerAddress,
                    TriggerValue = triggerValue,
                    AckValue = ackValue,
                };

                var sourceKey = sourceName.Trim();
                if (!sourceGroups.TryGetValue(sourceKey, out var group))
                {
                    group = new ParsedSourceGroup(candidateSource);
                    sourceGroups.Add(sourceKey, group);
                }
                else if (!HasSameSourceConfiguration(group.Source, candidateSource))
                {
                    result.Errors.Add(string.Format(Strings.F342, rowNumber));
                    continue;
                }

                if (IsSourceOnlyRow(record))
                {
                    AddImportedSource(result, group);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(record.ValueName))
                {
                    result.Errors.Add(string.Format(Strings.F335, rowNumber));
                    continue;
                }

                if (!TryParseValue(record, rowNumber, result.Errors, out var value))
                    continue;

                var valueKey = value.Name.Trim();
                if (!group.ValueNames.Add(valueKey))
                {
                    result.Errors.Add(string.Format(Strings.F343, rowNumber, sourceName, value.Name));
                    continue;
                }

                group.Source.Values.Add(value);
                AddImportedSource(result, group);
                result.ImportedValueCount++;
            }

            if (recordIndex == 0)
                result.Errors.Add(Strings.F333);
        }
        catch (Exception ex)
        {
            result.Errors.Add(string.Format(Strings.F134, ex.Message));
        }

        return result;
    }

    /// <summary>
    /// 将已校验的数据源应用到目标设备。
    /// 替换模式清空目标设备的数据源；追加模式按数据源名、值项名合并并覆盖同名配置。
    /// </summary>
    public void ApplyImportedDataSources(Device device, List<DataSource> imported, bool replace)
    {
        if (replace)
        {
            device.Sources.Clear();
            foreach (var source in imported)
            {
                source.DeviceId = device.Id;
                EnsureIds(source);
                device.Sources.Add(source);
            }
            return;
        }

        var existingSources = device.Sources
            .Where(source => !string.IsNullOrWhiteSpace(source.Name))
            .GroupBy(source => source.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var importedSource in imported)
        {
            if (!existingSources.TryGetValue(importedSource.Name.Trim(), out var existingSource))
            {
                importedSource.DeviceId = device.Id;
                EnsureIds(importedSource);
                device.Sources.Add(importedSource);
                existingSources[importedSource.Name.Trim()] = importedSource;
                continue;
            }

            existingSource.DeviceId = device.Id;
            CopySourceConfiguration(existingSource, importedSource);
            var existingValues = existingSource.Values
                .Where(value => !string.IsNullOrWhiteSpace(value.Name))
                .GroupBy(value => value.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            foreach (var importedValue in importedSource.Values)
            {
                if (existingValues.TryGetValue(importedValue.Name.Trim(), out var existingValue))
                {
                    CopyValueConfiguration(existingValue, importedValue);
                }
                else
                {
                    EnsureIds(importedValue);
                    existingSource.Values.Add(importedValue);
                    existingValues[importedValue.Name.Trim()] = importedValue;
                }
            }
        }
    }

    private static DataSourceCsvRecord CreateRecord(DataSource source, DataSourceValue? value = null)
    {
        if (value == null)
        {
            return new DataSourceCsvRecord
            {
                SourceName = source.Name,
                SourceNameEn = source.NameEn ?? string.Empty,
                SourceNameJa = source.NameJa ?? string.Empty,
                SourceNamePt = source.NamePt ?? string.Empty,
                SourceType = source.Type,
                SourceEnabled = CsvLocalization.BooleanText(source.Enabled),
                SourceDescription = source.Description,
                TriggerAddress = source.TriggerAddress,
                TriggerValue = source.TriggerValue.ToString(CultureInfo.InvariantCulture),
                AckValue = source.AckValue.ToString(CultureInfo.InvariantCulture),
            };
        }

        return new DataSourceCsvRecord
        {
            SourceName = source.Name,
            SourceNameEn = source.NameEn ?? string.Empty,
            SourceNameJa = source.NameJa ?? string.Empty,
            SourceNamePt = source.NamePt ?? string.Empty,
            SourceType = source.Type,
            SourceEnabled = CsvLocalization.BooleanText(source.Enabled),
            SourceDescription = source.Description,
            TriggerAddress = source.TriggerAddress,
            TriggerValue = source.TriggerValue.ToString(CultureInfo.InvariantCulture),
            AckValue = source.AckValue.ToString(CultureInfo.InvariantCulture),
            ValueName = value.Name,
            ValueNameEn = value.NameEn ?? string.Empty,
            ValueNameJa = value.NameJa ?? string.Empty,
            ValueNamePt = value.NamePt ?? string.Empty,
            ValueDataType = CsvLocalization.DataSourceValueTypeText(value.DataType),
            ValuePlcAddress = value.PlcAddress,
            ValueUnit = value.Unit,
            ValueEnabled = CsvLocalization.BooleanText(value.Enabled),
            LimitMin = value.DataType == DataSourceValueType.Int32
                ? value.LimitMin.ToString(CultureInfo.InvariantCulture)
                : string.Empty,
            LimitMax = value.DataType == DataSourceValueType.Int32
                ? value.LimitMax.ToString(CultureInfo.InvariantCulture)
                : string.Empty,
            FloatLimitMin = value.DataType == DataSourceValueType.Float32
                ? value.FloatLimitMin.ToString("G9", CultureInfo.InvariantCulture)
                : string.Empty,
            FloatLimitMax = value.DataType == DataSourceValueType.Float32
                ? value.FloatLimitMax.ToString("G9", CultureInfo.InvariantCulture)
                : string.Empty,
            Hysteresis = value.Hysteresis.ToString(CultureInfo.InvariantCulture),
            ConfirmSeconds = value.ConfirmSeconds.ToString(CultureInfo.InvariantCulture),
            ExpectedValueConfigured = CsvLocalization.BooleanText(value.HasExpectedValue),
            ExpectedValue = value.DataType == DataSourceValueType.Int32 && value.ExpectedValue.HasValue
                ? value.ExpectedValue.Value.ToString(CultureInfo.InvariantCulture)
                : string.Empty,
            FloatExpectedValue = value.DataType == DataSourceValueType.Float32 && value.FloatExpectedValue.HasValue
                ? value.FloatExpectedValue.Value.ToString("G9", CultureInfo.InvariantCulture)
                : string.Empty,
            BoolExpectedValue = value.DataType == DataSourceValueType.Bool && value.BoolExpectedValue.HasValue
                ? CsvLocalization.BooleanText(value.BoolExpectedValue.Value)
                : string.Empty,
            StringExpectedValue = value.DataType == DataSourceValueType.String && value.StringExpectedValue != null
                ? value.StringExpectedValue
                : string.Empty,
            StringLength = value.DataType == DataSourceValueType.String
                ? value.StringLength.ToString(CultureInfo.InvariantCulture)
                : string.Empty,
            EnumValuesJson = JsonSerializer.Serialize(
                value.EnumValues.Select(item => new DataSourceEnumCsvRecord
                {
                    Value = item.Value,
                    DisplayName = item.DisplayName,
                }),
                JsonOptions),
        };
    }

    private bool TryParseValue(
        DataSourceCsvRecord record,
        int rowNumber,
        List<string> errors,
        out DataSourceValue value)
    {
        value = new DataSourceValue
        {
            NameEn = NormalizeOrNull(record.ValueNameEn),
            NameJa = NormalizeOrNull(record.ValueNameJa),
            NamePt = NormalizeOrNull(record.ValueNamePt),
        };
        var valueName = record.ValueName.Trim();
        if (string.IsNullOrWhiteSpace(valueName))
        {
            errors.Add(string.Format(Strings.F335, rowNumber));
            return false;
        }

        if (!CsvLocalization.TryParseDataSourceValueType(record.ValueDataType, out var dataType))
        {
            errors.Add(string.Format(Strings.F336, rowNumber, record.ValueDataType));
            return false;
        }

        var valueAddress = record.ValuePlcAddress.Trim();
        if (!ValidateAddress(
                valueAddress,
                dataType == DataSourceValueType.Bool ? PlcAddressType.MBit : PlcAddressType.DWord,
                rowNumber,
                errors,
                allowEmpty: false))
            return false;

        if (!CsvLocalization.TryParseBoolean(record.ValueEnabled, true, out var enabled))
        {
            AddInvalidFieldError(errors, rowNumber, nameof(record.ValueEnabled), record.ValueEnabled);
            return false;
        }
        if (!TryParseInt(record.Hysteresis, 0, out var hysteresis))
        {
            AddInvalidFieldError(errors, rowNumber, nameof(record.Hysteresis), record.Hysteresis);
            return false;
        }
        if (!TryParseInt(record.ConfirmSeconds, 5, out var confirmSeconds))
        {
            AddInvalidFieldError(errors, rowNumber, nameof(record.ConfirmSeconds), record.ConfirmSeconds);
            return false;
        }

        var expectedConfiguredRaw = record.ExpectedValueConfigured.Trim();
        var expectedConfigured = string.IsNullOrWhiteSpace(expectedConfiguredRaw)
            ? HasExpectedValueText(record, dataType)
            : CsvLocalization.TryParseBoolean(expectedConfiguredRaw, false, out var configured) && configured;
        if (!string.IsNullOrWhiteSpace(expectedConfiguredRaw)
            && !CsvLocalization.TryParseBoolean(expectedConfiguredRaw, false, out _))
        {
            AddInvalidFieldError(errors, rowNumber, nameof(record.ExpectedValueConfigured), record.ExpectedValueConfigured);
            return false;
        }

        value.Name = valueName;
        value.DataType = dataType;
        value.PlcAddress = valueAddress;
        value.Unit = record.ValueUnit;
        value.Enabled = enabled;
        value.Hysteresis = hysteresis;
        value.ConfirmSeconds = confirmSeconds;

        switch (dataType)
        {
            case DataSourceValueType.Int32:
                if (!TryParseInt(record.LimitMin, 0, out var limitMin)
                    || !TryParseInt(record.LimitMax, 0, out var limitMax))
                {
                    AddInvalidFieldError(errors, rowNumber, "LimitMin/LimitMax", $"{record.LimitMin}/{record.LimitMax}");
                    return false;
                }
                value.LimitMin = limitMin;
                value.LimitMax = limitMax;
                if (expectedConfigured)
                {
                    if (!TryParseRequiredInt(record.ExpectedValue, out var expectedValue))
                    {
                        AddInvalidFieldError(errors, rowNumber, nameof(record.ExpectedValue), record.ExpectedValue);
                        return false;
                    }
                    value.ExpectedValue = expectedValue;
                }
                break;

            case DataSourceValueType.Float32:
                if (!TryParseFloat(record.FloatLimitMin, 0, out var floatLimitMin)
                    || !TryParseFloat(record.FloatLimitMax, 0, out var floatLimitMax))
                {
                    AddInvalidFieldError(errors, rowNumber, "FloatLimitMin/FloatLimitMax", $"{record.FloatLimitMin}/{record.FloatLimitMax}");
                    return false;
                }
                value.FloatLimitMin = floatLimitMin;
                value.FloatLimitMax = floatLimitMax;
                if (expectedConfigured)
                {
                    if (!TryParseRequiredFloat(record.FloatExpectedValue, out var floatExpectedValue))
                    {
                        AddInvalidFieldError(errors, rowNumber, nameof(record.FloatExpectedValue), record.FloatExpectedValue);
                        return false;
                    }
                    value.FloatExpectedValue = floatExpectedValue;
                }
                break;

            case DataSourceValueType.Bool:
                if (expectedConfigured)
                {
                    if (!CsvLocalization.TryParseRequiredBoolean(record.BoolExpectedValue, out var boolExpectedValue))
                    {
                        AddInvalidFieldError(errors, rowNumber, nameof(record.BoolExpectedValue), record.BoolExpectedValue);
                        return false;
                    }
                    value.BoolExpectedValue = boolExpectedValue;
                }
                break;

            case DataSourceValueType.String:
                if (!TryParseInt(record.StringLength, 32, out var stringLength))
                {
                    AddInvalidFieldError(errors, rowNumber, nameof(record.StringLength), record.StringLength);
                    return false;
                }
                value.StringLength = stringLength;
                if (expectedConfigured)
                    value.StringExpectedValue = record.StringExpectedValue;
                break;
        }

        if (!TryParseEnumValues(record.EnumValuesJson, rowNumber, errors, out var enumValues))
            return false;
        foreach (var enumValue in enumValues)
            value.EnumValues.Add(enumValue);

        return true;
    }

    private bool ValidateAddress(
        string address,
        PlcAddressType expectedType,
        int rowNumber,
        List<string> errors,
        bool allowEmpty)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            if (allowEmpty) return true;
            errors.Add(string.Format(Strings.F183, rowNumber));
            return false;
        }

        var parsed = CurrentCodec.Parse(address);
        if (parsed is not { IsValid: true })
        {
            errors.Add(string.Format(Strings.F337, rowNumber, address, parsed.ErrorMessage));
            return false;
        }
        if (parsed.Type != expectedType)
        {
            errors.Add(string.Format(Strings.F338, rowNumber, address, expectedType, parsed.Type));
            return false;
        }
        return true;
    }

    private static bool TryParseInt(string? raw, int defaultValue, out int value)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            value = defaultValue;
            return true;
        }
        return int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseRequiredInt(string? raw, out int value)
        => TryParseInt(raw, 0, out value) && !string.IsNullOrWhiteSpace(raw);

    private static bool TryParseFloat(string? raw, float defaultValue, out float value)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            value = defaultValue;
            return true;
        }
        if (!float.TryParse(raw.Trim(), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value))
            return false;
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static bool TryParseRequiredFloat(string? raw, out float value)
        => TryParseFloat(raw, 0, out value) && !string.IsNullOrWhiteSpace(raw);

    private static bool HasExpectedValueText(DataSourceCsvRecord record, DataSourceValueType dataType)
        => dataType switch
        {
            DataSourceValueType.Int32 => !string.IsNullOrWhiteSpace(record.ExpectedValue),
            DataSourceValueType.Float32 => !string.IsNullOrWhiteSpace(record.FloatExpectedValue),
            DataSourceValueType.Bool => !string.IsNullOrWhiteSpace(record.BoolExpectedValue),
            DataSourceValueType.String => !string.IsNullOrWhiteSpace(record.StringExpectedValue),
            _ => false,
        };

    private static bool TryParseEnumValues(
        string? raw,
        int rowNumber,
        List<string> errors,
        out List<DataSourceEnumValue> values)
    {
        values = [];
        if (string.IsNullOrWhiteSpace(raw)) return true;

        List<DataSourceEnumCsvRecord>? records;
        try
        {
            records = JsonSerializer.Deserialize<List<DataSourceEnumCsvRecord>>(raw, JsonOptions);
        }
        catch (JsonException ex)
        {
            errors.Add(string.Format(Strings.F340, rowNumber, ex.Message));
            return false;
        }

        if (records == null)
        {
            errors.Add(string.Format(Strings.F340, rowNumber, "JSON must be an array"));
            return false;
        }

        if (records.Any(record => string.IsNullOrWhiteSpace(record.DisplayName))
            || records.GroupBy(record => record.Value).Any(group => group.Count() > 1))
        {
            errors.Add(string.Format(Strings.F341, rowNumber));
            return false;
        }

        values = records
            .Select(record => new DataSourceEnumValue
            {
                Value = record.Value,
                DisplayName = record.DisplayName.Trim(),
            })
            .ToList();
        return true;
    }

    private static bool IsSourceOnlyRow(DataSourceCsvRecord record)
        => string.IsNullOrWhiteSpace(record.ValueName)
            && string.IsNullOrWhiteSpace(record.ValueDataType)
            && string.IsNullOrWhiteSpace(record.ValuePlcAddress)
            && string.IsNullOrWhiteSpace(record.ValueUnit)
            && string.IsNullOrWhiteSpace(record.ValueEnabled)
            && string.IsNullOrWhiteSpace(record.LimitMin)
            && string.IsNullOrWhiteSpace(record.LimitMax)
            && string.IsNullOrWhiteSpace(record.FloatLimitMin)
            && string.IsNullOrWhiteSpace(record.FloatLimitMax)
            && string.IsNullOrWhiteSpace(record.Hysteresis)
            && string.IsNullOrWhiteSpace(record.ConfirmSeconds)
            && string.IsNullOrWhiteSpace(record.ExpectedValueConfigured)
            && string.IsNullOrWhiteSpace(record.ExpectedValue)
            && string.IsNullOrWhiteSpace(record.FloatExpectedValue)
            && string.IsNullOrWhiteSpace(record.BoolExpectedValue)
            && string.IsNullOrWhiteSpace(record.StringExpectedValue)
            && string.IsNullOrWhiteSpace(record.StringLength)
            && string.IsNullOrWhiteSpace(record.EnumValuesJson);

    private static bool HasSameSourceConfiguration(DataSource first, DataSource current)
        => string.Equals(first.Name, current.Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(first.Type, current.Type, StringComparison.Ordinal)
            && first.Enabled == current.Enabled
            && string.Equals(first.Description, current.Description, StringComparison.Ordinal)
            && string.Equals(first.TriggerAddress, current.TriggerAddress, StringComparison.OrdinalIgnoreCase)
            && first.TriggerValue == current.TriggerValue
            && first.AckValue == current.AckValue;

    private static void AddImportedSource(DataSourceCsvImportResult result, ParsedSourceGroup group)
    {
        if (group.IsAdded) return;
        result.Imported.Add(group.Source);
        group.IsAdded = true;
    }

    private static void AddInvalidFieldError(
        DataSourceCsvImportResult result,
        int rowNumber,
        string field,
        string? value)
        => AddInvalidFieldError(result.Errors, rowNumber, field, value);

    private static void AddInvalidFieldError(
        List<string> errors,
        int rowNumber,
        string field,
        string? value)
        => errors.Add(string.Format(Strings.F339, rowNumber, field, value ?? string.Empty));

    private static void EnsureIds(DataSource source)
    {
        if (string.IsNullOrWhiteSpace(source.Id)) source.Id = Guid.NewGuid().ToString("N");
        foreach (var value in source.Values)
            EnsureIds(value);
    }

    private static void EnsureIds(DataSourceValue value)
    {
        if (string.IsNullOrWhiteSpace(value.Id)) value.Id = Guid.NewGuid().ToString("N");
    }

    private static void CopySourceConfiguration(DataSource target, DataSource source)
    {
        target.Name = source.Name;
        target.NameEn = source.NameEn;
        target.NameJa = source.NameJa;
        target.NamePt = source.NamePt;
        target.Type = source.Type;
        target.Enabled = source.Enabled;
        target.Description = source.Description;
        target.TriggerAddress = source.TriggerAddress;
        target.TriggerValue = source.TriggerValue;
        target.AckValue = source.AckValue;
    }

    private static void CopyValueConfiguration(DataSourceValue target, DataSourceValue source)
    {
        target.Name = source.Name;
        target.NameEn = source.NameEn;
        target.NameJa = source.NameJa;
        target.NamePt = source.NamePt;
        target.DataType = source.DataType;
        target.StringLength = source.StringLength;
        target.FloatLimitMin = source.FloatLimitMin;
        target.FloatLimitMax = source.FloatLimitMax;
        target.PlcAddress = source.PlcAddress;
        target.Unit = source.Unit;
        target.Enabled = source.Enabled;
        target.LimitMin = source.LimitMin;
        target.LimitMax = source.LimitMax;
        target.Hysteresis = source.Hysteresis;
        target.ConfirmSeconds = source.ConfirmSeconds;
        target.ExpectedValue = source.ExpectedValue;
        target.FloatExpectedValue = source.FloatExpectedValue;
        target.BoolExpectedValue = source.BoolExpectedValue;
        target.StringExpectedValue = source.StringExpectedValue;
        target.EnumValues.Clear();
        foreach (var enumValue in source.EnumValues)
        {
            target.EnumValues.Add(new DataSourceEnumValue
            {
                Value = enumValue.Value,
                DisplayName = enumValue.DisplayName,
                DisplayNameEn = enumValue.DisplayNameEn,
                DisplayNameJa = enumValue.DisplayNameJa,
                DisplayNamePt = enumValue.DisplayNamePt,
            });
        }
    }

    private static string? NormalizeOrNull(string? raw)
        => string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

    private sealed class ParsedSourceGroup(DataSource source)
    {
        public DataSource Source { get; } = source;
        public HashSet<string> ValueNames { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool IsAdded { get; set; }
    }
}