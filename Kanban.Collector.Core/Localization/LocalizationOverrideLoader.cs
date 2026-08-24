using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;

namespace Kanban.Collector.Core.Localization;

/// <summary>启动时本地化覆盖文件的加载结果。</summary>
public sealed record LocalizationOverrideLoadResult(
    bool FileFound,
    bool IsValid,
    int AppliedCount,
    IReadOnlyList<string> Errors);

/// <summary>
/// 读取 Config/Localization.override.csv 中的已有资源覆盖值。
/// 文件采用构建时 Localization.csv 的同一列格式，但允许只填写需要修改的语言列。
/// </summary>
public static class LocalizationOverrideLoader
{
    public const string FileName = "Localization.override.csv";

    private static readonly string[] FixedHeaders = ["Resource", "Key"];

    /// <summary>
    /// 加载覆盖文件。WPF 与 Core Key/占位符均由共享 LocalizationCatalog 校验；
    /// MainAPP 仍可传入 WPF 资源回调以使用其 ResourceManager 读取路径。
    /// 校验失败时整份文件不生效，调用方继续使用编译内置资源。
    /// </summary>
    public static LocalizationOverrideLoadResult Load(
        string path,
        Func<string, bool>? isKnownWpfKey = null,
        Func<string, string, string?>? getWpfEmbeddedValue = null)
    {
        if (!File.Exists(path))
        {
            LocalizationOverrideStore.Clear();
            return new(false, true, 0, Array.Empty<string>());
        }

        var errors = new List<string>();
        var entries = new List<LocalizationOverrideEntry>();
        var seenRows = new HashSet<(string Resource, string Key)>();

        try
        {
            var csvConfiguration = new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                HasHeaderRecord = true,
                Delimiter = DetectDelimiter(path),
                TrimOptions = TrimOptions.None,
                MissingFieldFound = null,
                HeaderValidated = null,
                DetectColumnCountChanges = true,
                BadDataFound = _ => errors.Add("CSV 存在无法解析的字段"),
            };
            using var reader = new StringReader(ReadText(path));
            using var csv = new CsvReader(reader, csvConfiguration);

            if (!csv.Read())
            {
                errors.Add("覆盖文件为空");
            }
            else
            {
                csv.ReadHeader();
                var headers = csv.HeaderRecord?.Select(header => header.Trim()).ToArray()
                    ?? Array.Empty<string>();
                var languages = headers.Skip(FixedHeaders.Length).ToArray();
                if (headers.Length < 3
                    || !headers.Take(FixedHeaders.Length).SequenceEqual(FixedHeaders, StringComparer.Ordinal)
                    || languages.Any(language => !LocalizationCatalog.IsSupported(language))
                    || languages.Length != languages.Distinct(StringComparer.OrdinalIgnoreCase).Count())
                {
                    errors.Add($"表头必须以 Resource,Key 开头，语言列必须来自已编译资源：{string.Join(",", LocalizationCatalog.LanguageCodes)}");
                }
                else
                {
                    while (csv.Read())
                    {
                        var resource = (csv.GetField(0) ?? string.Empty).Trim();
                        var key = (csv.GetField(1) ?? string.Empty).Trim();
                        if (string.IsNullOrWhiteSpace(resource) && string.IsNullOrWhiteSpace(key))
                            continue;

                        var rowNumber = csv.Context.Parser?.Row ?? 0;
                        if (resource is not ("Wpf" or "Core"))
                        {
                            errors.Add($"第 {rowNumber} 行 Resource 必须为 Wpf 或 Core");
                            continue;
                        }
                        if (string.IsNullOrWhiteSpace(key))
                        {
                            errors.Add($"第 {rowNumber} 行 Key 不能为空");
                            continue;
                        }
                        if (!seenRows.Add((resource, key)))
                        {
                            errors.Add($"第 {rowNumber} 行重复覆盖 {resource}/{key}");
                            continue;
                        }

                        var known = resource == "Core"
                            ? LocalizationCatalog.IsKnownKey("Core", key)
                            : isKnownWpfKey?.Invoke(key) ?? LocalizationCatalog.IsKnownKey("Wpf", key);
                        if (!known)
                        {
                            errors.Add($"第 {rowNumber} 行不存在资源 Key {resource}/{key}");
                            continue;
                        }

                        var valueCount = 0;
                        for (var languageIndex = 0; languageIndex < languages.Length; languageIndex++)
                        {
                            var language = LocalizationCatalog.Normalize(languages[languageIndex]);
                            var value = csv.GetField(languageIndex + 2) ?? string.Empty;
                            if (string.IsNullOrWhiteSpace(value))
                                continue;

                            valueCount++;
                            var embedded = resource == "Core"
                                ? LocalizationCatalog.Get("Core", key, language)
                                : getWpfEmbeddedValue?.Invoke(key, language)
                                    ?? LocalizationCatalog.Get("Wpf", key, language);
                            if (embedded is null)
                            {
                                errors.Add($"第 {rowNumber} 行无法读取内置资源 {resource}/{key}/{language}");
                                continue;
                            }
                            if (!HaveSamePlaceholders(embedded, value))
                            {
                                errors.Add($"第 {rowNumber} 行占位符不一致 {resource}/{key}/{language}");
                                continue;
                            }

                            entries.Add(new LocalizationOverrideEntry(resource, key, language, value));
                        }

                        if (valueCount == 0)
                            errors.Add($"第 {rowNumber} 行没有可应用的翻译值");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            errors.Add($"覆盖文件读取失败：{ex.Message}");
        }

        if (errors.Count > 0)
        {
            LocalizationOverrideStore.Clear();
            return new(true, false, 0, errors);
        }

        LocalizationOverrideStore.Replace(entries);
        return new(true, true, entries.Count, Array.Empty<string>());
    }

    private static bool HaveSamePlaceholders(string expected, string actual)
    {
        if (!TryGetPlaceholderSignature(expected, out var expectedSignature)
            || !TryGetPlaceholderSignature(actual, out var actualSignature))
            return false;
        return expectedSignature.OrderBy(pair => pair.Key)
            .SequenceEqual(actualSignature.OrderBy(pair => pair.Key));
    }

    private static string DetectDelimiter(string path)
    {
        var header = ReadText(path).Split('\n', 2)[0];
        var candidates = new[] { ",", ";", "\t" };
        return candidates
            .OrderByDescending(delimiter => header.Count(character => character == delimiter[0]))
            .First();
    }

    private static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes, 3, bytes.Length - 3);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }))
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }))
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // Excel 在部分 Windows 区域设置下导出无 BOM 的本机 ANSI 文件；使用当前 Windows
            // 代码页读取后仍会经过同一套 CSV、Key 和格式占位符校验，不能绕过安全检查。
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(0).GetString(bytes);
        }
    }

    private static bool TryGetPlaceholderSignature(
        string value,
        out Dictionary<int, int> signature)
    {
        signature = new Dictionary<int, int>();
        var maxIndex = -1;
        for (var index = 0; index < value.Length;)
        {
            if (value[index] == '{')
            {
                if (index + 1 < value.Length && value[index + 1] == '{')
                {
                    index += 2;
                    continue;
                }

                index++;
                var start = index;
                while (index < value.Length && char.IsDigit(value[index])) index++;
                if (start == index || !int.TryParse(value[start..index], out var argumentIndex))
                    return false;
                maxIndex = Math.Max(maxIndex, argumentIndex);

                if (index < value.Length && value[index] == ',')
                {
                    index++;
                    while (index < value.Length && char.IsWhiteSpace(value[index])) index++;
                    if (index < value.Length && value[index] == '-') index++;
                    start = index;
                    while (index < value.Length && char.IsDigit(value[index])) index++;
                    if (start == index) return false;
                    while (index < value.Length && char.IsWhiteSpace(value[index])) index++;
                }

                if (index < value.Length && value[index] == ':')
                {
                    index++;
                    while (index < value.Length && value[index] != '}')
                    {
                        if (value[index] == '{') return false;
                        index++;
                    }
                }

                if (index >= value.Length || value[index] != '}') return false;
                signature[argumentIndex] = signature.TryGetValue(argumentIndex, out var count)
                    ? count + 1
                    : 1;
                index++;
            }
            else if (value[index] == '}')
            {
                if (index + 1 < value.Length && value[index + 1] == '}')
                {
                    index += 2;
                    continue;
                }
                return false;
            }
            else
            {
                index++;
            }
        }

        try
        {
            var arguments = Enumerable.Range(0, maxIndex + 1)
                .Select(_ => (object)new FormatProbe())
                .ToArray();
            _ = string.Format(CultureInfo.InvariantCulture, value, arguments);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private sealed class FormatProbe : IFormattable
    {
        public string ToString(string? format, IFormatProvider? formatProvider) => "x";
        public override string ToString() => "x";
    }
}
