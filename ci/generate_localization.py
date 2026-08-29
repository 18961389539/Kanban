#!/usr/bin/env python3
"""Generate all application localization artifacts from one CSV source.

The source format is Localization.csv with columns:
Resource,Key,<language-code>...

Resource is either Wpf or Core. The build invokes this script before compiling
those projects. Existing RESX files are runtime artifacts and must not be edited
manually after the CSV migration.
"""

from __future__ import annotations

import argparse
import csv
import re
import sys
import xml.etree.ElementTree as ET
from collections import Counter
from pathlib import Path
from xml.sax.saxutils import escape

PROJECT_ROOT = Path(__file__).resolve().parent.parent
CSV_PATH = PROJECT_ROOT / "MainAPP" / "Resources" / "Localization.csv"
DEFAULT_LANGUAGES = ("zh-CN", "en-US", "ja-JP", "pt-BR")
LANGUAGES: tuple[str, ...] = DEFAULT_LANGUAGES
RESOURCES = ("Wpf", "Core")
KEY_PATTERN = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*$")
LANGUAGE_CODE_PATTERN = re.compile(r"^[A-Za-z]{2,3}(?:-[A-Za-z0-9]+)*$")
CSV_PREFIX = ["Resource", "Key"]
KNOWN_RESX_SUFFIXES = {
    "zh-CN": "",
    "en-US": "en",
    "ja-JP": "ja",
    "pt-BR": "pt-BR",
}


def resx_path(resource: str, language: str) -> Path:
    directory = "MainAPP/Resources" if resource == "Wpf" else "Kanban.Collector.Core/Resources"
    base_name = "Strings" if resource == "Wpf" else "Messages"
    suffix = KNOWN_RESX_SUFFIXES.get(language, language)
    file_name = f"{base_name}{'.' + suffix if suffix else ''}.resx"
    return PROJECT_ROOT / directory / file_name


def resource_files(languages: tuple[str, ...]) -> dict[str, dict[str, Path]]:
    return {
        resource: {language: resx_path(resource, language) for language in languages}
        for resource in RESOURCES
    }


def generated_resx_paths(resource: str) -> set[Path]:
    directory = PROJECT_ROOT / (
        "MainAPP/Resources" if resource == "Wpf" else "Kanban.Collector.Core/Resources"
    )
    base_name = "Strings" if resource == "Wpf" else "Messages"
    return set(directory.glob(f"{base_name}.*.resx"))

# These are the shared strings consumed by the Web dashboard without a Web_ prefix.
SHARED_WEB_KEYS = {
    "Badge_Recent1Min", "Card_Alarms", "Card_DevCount", "Card_DevStatus", "Card_Oee",
    "Card_Output", "Card_ProdStatus", "Card_Shift", "Card_Source", "Card_Trend",
    "Card_WorkOrder", "Conn_Connected", "Conn_Disconnected", "Conn_Live", "Fresh_Live", "Fresh_Mins",
    "Fresh_None", "Fresh_Secs", "Lbl_Achievement", "Lbl_ActualCycle", "Lbl_Availability",
    "Lbl_ConnStatus", "Lbl_DataFresh", "Lbl_DevTotal", "Lbl_Elapsed", "Lbl_Ng",
    "Lbl_NgRate", "Lbl_Ok", "Lbl_Performance", "Lbl_Planned", "Lbl_Quality",
    "Lbl_RealTimeSpeed", "Lbl_Remaining", "Lbl_SnapSeq", "Lbl_SvcAddr", "Lbl_SvcVersion",
    "Lbl_TargetCycle", "Lbl_TotalOutput", "Meta_PlanWo", "Meta_QualityRate", "Meta_ShiftAuto",
    "Meta_StatusWord", "Meta_TrendCurrent", "Msg_EmptyHint", "Msg_NoDevSelected",
    "Msg_NoDeviceData", "Msg_SelectHint", "Msg_WaitingConn", "Status_Alarm", "Status_Idle",
    "Status_Offline", "Status_Paused", "Status_Running", "Val_DynAddr", "Val_NoAlarms", "Val_NoWorkOrder",
    "Val_WaitData", "Wo_Aborted", "Wo_Completed", "Wo_Pending", "Wo_Progress",
    "Wo_ProgressPending", "Wo_Running",
}

# Added only during the one-time RESX -> CSV migration. Afterwards it lives in CSV.
BOOTSTRAP_ROWS = {
    ("Wpf", "Language_Portuguese"): {
        "zh-CN": "葡萄牙语",
        "en-US": "Portuguese",
        "ja-JP": "ポルトガル語",
        "pt-BR": "Português",
    },
}


def parse_dotnet_format(value: str) -> Counter[int]:
    """Parse a .NET composite format string and return placeholder multiplicities.

    The parser accepts escaped braces ({{ and }}) plus alignment and format
    sections. It rejects malformed braces and incomplete format items before
    the value can reach string.Format at runtime.
    """
    signature: Counter[int] = Counter()
    index = 0
    while index < len(value):
        char = value[index]
        if char == "{":
            if index + 1 < len(value) and value[index + 1] == "{":
                index += 2
                continue

            index += 1
            start = index
            while index < len(value) and value[index].isdigit():
                index += 1
            if start == index:
                raise ValueError("format item index is missing")
            argument_index = int(value[start:index])

            if index < len(value) and value[index] == ",":
                index += 1
                while index < len(value) and value[index].isspace():
                    index += 1
                if index < len(value) and value[index] == "-":
                    index += 1
                start = index
                while index < len(value) and value[index].isdigit():
                    index += 1
                if start == index:
                    raise ValueError("format item alignment is missing")
                while index < len(value) and value[index].isspace():
                    index += 1

            if index < len(value) and value[index] == ":":
                index += 1
                while index < len(value) and value[index] != "}":
                    if value[index] == "{":
                        raise ValueError("unescaped opening brace in format section")
                    index += 1

            if index >= len(value) or value[index] != "}":
                raise ValueError("format item closing brace is missing")
            signature[argument_index] += 1
            index += 1
        elif char == "}":
            if index + 1 < len(value) and value[index + 1] == "}":
                index += 2
                continue
            raise ValueError("unescaped closing brace")
        else:
            index += 1
    return signature


def signature_text(signature: Counter[int]) -> str:
    return ";".join(f"{key}:{signature[key]}" for key in sorted(signature))


def read_resx(path: Path) -> dict[str, str]:
    if not path.exists():
        return {}
    root = ET.parse(path).getroot()
    values: dict[str, str] = {}
    for data in root.findall("data"):
        key = data.attrib.get("name")
        value = data.find("value")
        if key and value is not None:
            values[key] = value.text or ""
    return values


def write_if_changed(path: Path, content: str, encoding: str = "utf-8") -> bool:
    old = path.read_text(encoding=encoding) if path.exists() else None
    if old == content:
        return False
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(content, encoding=encoding, newline="")
    return True


def bootstrap_csv() -> None:
    if CSV_PATH.exists():
        raise ValueError(f"CSV already exists: {CSV_PATH}")

    rows: list[dict[str, str]] = []
    languages = DEFAULT_LANGUAGES
    files = resource_files(languages)
    for resource in RESOURCES:
        maps = {language: read_resx(path) for language, path in files[resource].items()}
        keys: list[str] = []
        for key in maps["zh-CN"]:
            if key not in keys:
                keys.append(key)
        for language in LANGUAGES[1:]:
            for key in maps[language]:
                if key not in keys:
                    keys.append(key)

        for key in keys:
            english = maps["en-US"].get(key, maps["zh-CN"].get(key, key))
            rows.append({
                "Resource": resource,
                "Key": key,
                "zh-CN": maps["zh-CN"].get(key, key),
                "en-US": english,
                "ja-JP": maps["ja-JP"].get(key, english),
                # No Portuguese RESX existed before this migration. English is an explicit,
                # visible fallback baseline that translators can replace in the CSV.
                "pt-BR": maps["pt-BR"].get(key, english),
            })

    for (resource, key), values in BOOTSTRAP_ROWS.items():
        if not any(row["Resource"] == resource and row["Key"] == key for row in rows):
            rows.append({"Resource": resource, "Key": key, **values})

    write_csv(rows)
    print(f"Bootstrapped {CSV_PATH} from existing RESX files ({len(rows)} rows)")


def load_csv() -> list[dict[str, str]]:
    global LANGUAGES
    if not CSV_PATH.exists():
        raise ValueError(f"Localization CSV not found: {CSV_PATH}. Run with --bootstrap once.")

    with CSV_PATH.open("r", encoding="utf-8-sig", newline="") as stream:
        reader = csv.DictReader(stream)
        headers = [header.strip() for header in (reader.fieldnames or [])]
        if len(headers) < 3 or headers[:2] != CSV_PREFIX:
            raise ValueError(
                f"Localization.csv header must start with {CSV_PREFIX} and contain language columns, got {headers}"
            )
        languages = tuple(headers[2:])
        if len(set(languages)) != len(languages):
            raise ValueError(f"Localization.csv contains duplicate language columns: {languages}")
        if "zh-CN" not in languages:
            raise ValueError("Localization.csv must contain the zh-CN language column")
        for language in languages:
            if not LANGUAGE_CODE_PATTERN.fullmatch(language):
                raise ValueError(f"Invalid language culture code in Localization.csv: {language!r}")
        LANGUAGES = languages

        rows: list[dict[str, str]] = []
        for line_number, raw in enumerate(reader, start=2):
            if not any((value or "").strip() for value in raw.values()):
                continue
            row = {column: (raw.get(column) or "") for column in [*CSV_PREFIX, *LANGUAGES]}
            row["Resource"] = row["Resource"].strip()
            row["Key"] = row["Key"].strip()
            rows.append(row)

    validate_rows(rows)
    return rows


def validate_rows(rows: list[dict[str, str]]) -> None:
    seen: set[tuple[str, str]] = set()
    errors: list[str] = []
    for index, row in enumerate(rows, start=2):
        resource = row["Resource"]
        key = row["Key"]
        identity = (resource, key)
        if resource not in RESOURCES:
            errors.append(f"row {index}: Resource must be Wpf or Core, got {resource!r}")
        if not KEY_PATTERN.fullmatch(key):
            errors.append(f"row {index}: invalid resource key {key!r}")
        if identity in seen:
            errors.append(f"row {index}: duplicate resource key {resource}/{key}")
        seen.add(identity)

        for language in LANGUAGES:
            if not row[language].strip():
                errors.append(f"row {index}: {resource}/{key} has empty {language} translation")

        placeholder_counts: dict[str, Counter[int]] = {}
        for language in LANGUAGES:
            try:
                placeholder_counts[language] = parse_dotnet_format(row[language])
            except ValueError as error:
                errors.append(f"row {index}: invalid format for {resource}/{key}/{language}: {error}")
                placeholder_counts[language] = Counter()
        if len({tuple(sorted(count.items())) for count in placeholder_counts.values()}) != 1:
            details = ", ".join(
                f"{language}={dict(placeholder_counts[language])}" for language in LANGUAGES
            )
            errors.append(f"row {index}: placeholder mismatch for {resource}/{key}: {details}")

    if errors:
        raise ValueError("Localization CSV validation failed:\n" + "\n".join(errors[:50]))


def coverage_rows(rows: list[dict[str, str]]) -> list[dict[str, object]]:
    """Return per-resource language coverage and likely English fallback counts."""
    result: list[dict[str, object]] = []
    has_english_baseline = "en-US" in LANGUAGES
    for resource in RESOURCES:
        resource_rows = [row for row in rows if row["Resource"] == resource]
        total = len(resource_rows)
        for language in LANGUAGES:
            non_empty = sum(bool(row[language].strip()) for row in resource_rows)
            same_as_english = sum(
                has_english_baseline
                and language != "en-US"
                and row[language].strip() == row["en-US"].strip()
                for row in resource_rows
            )
            result.append({
                "resource": resource,
                "language": language,
                "total": total,
                "non_empty": non_empty,
                "same_as_english": same_as_english,
                "fallback_ratio": same_as_english / total if total else 0.0,
            })
    return result


def print_coverage(rows: list[dict[str, str]]) -> None:
    print("Localization coverage (same_as_english is a likely fallback, not a translation verdict):")
    for item in coverage_rows(rows):
        print(
            "  resource={resource} language={language} rows={total} "
            "non_empty={non_empty} same_as_english={same_as_english} "
            "fallback_ratio={fallback_ratio:.2%}".format(**item)
        )


def validate_fallback_ratio(rows: list[dict[str, str]], maximum: float) -> None:
    if not 0 <= maximum <= 1:
        raise ValueError("--max-wpf-english-fallback-ratio must be between 0 and 1")

    violations = [
        item for item in coverage_rows(rows)
        if item["resource"] == "Wpf"
        and item["language"] not in {"zh-CN", "en-US"}
        and item["fallback_ratio"] > maximum
    ]
    if violations:
        details = "; ".join(
            f"{item['language']}={item['fallback_ratio']:.2%}"
            for item in violations
        )
        raise ValueError(
            "WPF English fallback ratio exceeds the configured limit "
            f"({maximum:.2%}): {details}"
        )


def validate_fallback_count(rows: list[dict[str, str]], maximum: int) -> None:
    if maximum < 0:
        raise ValueError("--max-wpf-english-fallback-count must be non-negative")

    violations = [
        item for item in coverage_rows(rows)
        if item["resource"] == "Wpf"
        and item["language"] not in {"zh-CN", "en-US"}
        and item["same_as_english"] > maximum
    ]
    if violations:
        details = "; ".join(
            f"{item['language']}={item['same_as_english']}"
            for item in violations
        )
        raise ValueError(
            "WPF English fallback count exceeds the configured limit "
            f"({maximum}): {details}"
        )


def write_csv(rows: list[dict[str, str]]) -> None:
    with CSV_PATH.open("w", encoding="utf-8-sig", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=[*CSV_PREFIX, *LANGUAGES], lineterminator="\n")
        writer.writeheader()
        writer.writerows(rows)


def resx_content(rows: list[dict[str, str]], language: str) -> str:
    data = []
    for row in rows:
        key = escape(row["Key"], {'"': "&quot;"})
        value = escape(row[language])
        data.append(
            f'  <data name="{key}" xml:space="preserve">\n'
            f"    <value>{value}</value>\n"
            "  </data>"
        )

    return """<?xml version="1.0" encoding="utf-8"?>
<root>
  <resheader name="resmimetype">
    <value>text/microsoft-resx</value>
  </resheader>
  <resheader name="version">
    <value>2.0</value>
  </resheader>
  <resheader name="reader">
    <value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
  </resheader>
  <resheader name="writer">
    <value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
  </resheader>
%s
</root>
""" % "\n".join(data)


def generate_resx(rows: list[dict[str, str]], resource: str) -> dict[Path, str]:
    paths = resource_files(LANGUAGES)[resource]
    resource_rows = [row for row in rows if row["Resource"] == resource]
    return {path: resx_content(resource_rows, language) for language, path in paths.items()}


def generate_core_localization_catalog(rows: list[dict[str, str]]) -> str:
    entries: list[str] = []
    for row in rows:
        key = f'{row["Resource"]}|{row["Key"]}'
        values = ", ".join(
            f"[{csharp_literal(language)}] = {csharp_literal(row[language])}"
            for language in LANGUAGES
        )
        entries.append(
            f"        [{csharp_literal(key)}] = new Dictionary<string, string>\n"
            "        {\n"
            f"            {values}\n"
            "        },"
        )

    display_name_keys = {
        "zh-CN": "Language_Chinese",
        "en-US": "Language_English",
        "ja-JP": "Language_Japanese",
        "pt-BR": "Language_Portuguese",
    }
    display_entries: list[str] = []
    wpf = {row["Key"]: row for row in rows if row["Resource"] == "Wpf"}
    for language in LANGUAGES:
        display_row = wpf.get(display_name_keys.get(language, ""))
        display_values = [display_row[code] if display_row else language for code in LANGUAGES]
        display_entries.append(
            f"        [{csharp_literal(language)}] = new Dictionary<string, string>\n"
            "        {\n"
            + "\n".join(
                f"            [{csharp_literal(code)}] = {csharp_literal(display_values[index])},"
                for index, code in enumerate(LANGUAGES)
            )
            + "\n        },"
        )

    language_entries = ",\n".join(
        f"        {csharp_literal(language)}" for language in LANGUAGES
    )
    return f'''using System.Collections.Generic;

namespace Kanban.Collector.Core.Localization;

/// <summary>
/// 从 Localization.csv 生成的共享本地化目录。语言代码、资源 Key 和占位符签名均来自 CSV。
/// WARNING: AUTO-GENERATED by ci/generate_localization.py. DO NOT EDIT MANUALLY.
/// </summary>
public static class LocalizationCatalog
{{
    private static readonly Dictionary<string, Dictionary<string, string>> Values =
        new(StringComparer.Ordinal)
        {{
{chr(10).join(entries)}
        }};

    private static readonly Dictionary<string, Dictionary<string, string>> LanguageNames =
        new(StringComparer.Ordinal)
        {{
{chr(10).join(display_entries)}
        }};

    public static IReadOnlyList<string> LanguageCodes {{ get; }} =
    [
{language_entries}
    ];

    public const string DefaultLanguage = "zh-CN";
    private static readonly string[] LegacyLanguageCodes = ["zh-CN", "en-US", "ja-JP", "pt-BR"];

    public static bool IsSupported(string? language)
        => language is not null && LanguageCodes.Contains(language, StringComparer.OrdinalIgnoreCase);

    public static string Normalize(string? language)
        => LanguageCodes.FirstOrDefault(code => string.Equals(code, language, StringComparison.OrdinalIgnoreCase))
            ?? DefaultLanguage;

    public static int GetLegacyIndex(string? language)
    {{
        for (var index = 0; index < LegacyLanguageCodes.Length; index++)
        {{
            if (string.Equals(LegacyLanguageCodes[index], language, StringComparison.OrdinalIgnoreCase)
                && IsSupported(LegacyLanguageCodes[index]))
                return index;
        }}
        return -1;
    }}

    public static bool IsKnownKey(string resource, string key)
        => Values.ContainsKey(resource + "|" + key);

    public static string? Get(string resource, string key, string language)
        => Values.TryGetValue(resource + "|" + key, out var translations)
            && translations.TryGetValue(language, out var value)
            ? value
            : null;

    public static string GetLanguageDisplayName(string language, string displayCulture)
        => LanguageNames.TryGetValue(language, out var translations)
            && translations.TryGetValue(displayCulture, out var value)
            ? value
            : language;
}}
'''


def generate_wpf_strings_cs(rows: list[dict[str, str]]) -> str:
    keys = sorted(row["Key"] for row in rows if row["Resource"] == "Wpf")
    properties = "\n".join(
        f'        public static string {key} => S("{key}", "{key}");' for key in keys
    )
    return f'''using System.Globalization;
using Kanban.Collector.Core.Localization;

namespace MainAPP.Resources;

/// <summary>
/// 多语言资源强类型访问；语言代码和翻译来自 Localization.csv。
/// WARNING: AUTO-GENERATED by ci/generate_localization.py. DO NOT EDIT MANUALLY.
/// All {len(keys)} Wpf keys come from MainAPP/Resources/Localization.csv.
/// </summary>
public static class Strings
{{
    /// <summary>
    /// 启动时由 Localization.Apply 捕获的 UI 文化。
    /// </summary>
    private static CultureInfo s_capturedCulture = CultureInfo.GetCultureInfo("zh-CN");

    public static void CaptureCulture(CultureInfo culture)
    {{
        s_capturedCulture = culture;
    }}

    /// <summary>按启动期文化取资源；启动覆盖文件优先，缺失时回退 key 名本身。</summary>
    public static string S(string key, string fallback)
    {{
        if (LocalizationOverrideStore.TryGet("Wpf", key, s_capturedCulture, out var overrideValue))
            return overrideValue;
        return LocalizationCatalog.Get("Wpf", key, s_capturedCulture.Name) ?? fallback;
    }}

    /// <summary>查询内置资源是否包含指定 WPF Key，供启动覆盖文件拒绝新增 Key。</summary>
    public static bool IsKnownKey(string key)
        => LocalizationCatalog.IsKnownKey("Wpf", key);

    /// <summary>读取内置 WPF 资源，供启动覆盖文件校验格式占位符。</summary>
    public static string? GetEmbeddedValue(string key, string cultureName)
        => LocalizationCatalog.Get("Wpf", key, cultureName);

{properties}
}}
'''


def csharp_literal(value: str) -> str:
    escaped = (
        value.replace("\\", "\\\\")
        .replace('"', '\\"')
        .replace("\r", "\\r")
        .replace("\n", "\\n")
        .replace("\t", "\\t")
    )
    return f'"{escaped}"'


def generate_web_loc(rows: list[dict[str, str]]) -> str:
    wpf = {row["Key"]: row for row in rows if row["Resource"] == "Wpf"}
    keys = sorted(
        {key[4:] for key in wpf if key.startswith("Web_")} | SHARED_WEB_KEYS
    )

    entries: list[str] = []
    for key in keys:
        row = wpf.get(f"Web_{key}") or wpf.get(key)
        values = [row[language] if row else key for language in LANGUAGES]
        entries.append(
            f'        [{csharp_literal(key)}] = new[] {{ '
            + ", ".join(csharp_literal(value) for value in values)
            + " },"
        )

    language_entries = "\n".join(
        f"        [{csharp_literal(language)}] = {index},"
        for index, language in enumerate(LANGUAGES)
    )
    language_constants = "\n".join(
        f"    public const string {language.replace('-', '_')} = {csharp_literal(language)};"
        for language in LANGUAGES
        if language in {"zh-CN", "en-US", "ja-JP", "pt-BR"}
    )

    return f'''using System.Globalization;
using Kanban.Contracts.Dtos;

namespace Kanban.Web;

/// <summary>屏端语言代码常量。新增语言由 Localization.csv 表头自动加入运行时目录。</summary>
public static class WebLanguage
{{
{language_constants}
}}

/// <summary>
/// WASM 屏端多语言字典（语言代码由 Localization.csv 表头定义）。
/// WARNING: AUTO-GENERATED by ci/generate_localization.py. DO NOT EDIT MANUALLY.
/// All {len(keys)} keys are derived from Localization.csv Wpf rows.
/// </summary>
public static class L
{{
    private static readonly Dictionary<string, string[]> BuiltinD = new()
    {{
{chr(10).join(entries)}
    }};
    private static readonly Dictionary<string, int> LanguageIndices = new(StringComparer.OrdinalIgnoreCase)
    {{
{language_entries}
    }};
    private static readonly string[] LegacyLanguageCodes = ["zh-CN", "en-US", "ja-JP", "pt-BR"];
    private static Dictionary<string, string[]> s_values = Clone(BuiltinD);

    public static IReadOnlyList<string> LanguageCodes => LanguageIndices.Keys.ToArray();
    public const string DefaultLanguage = "zh-CN";
    public static string Current {{ get; set; }} = DefaultLanguage;

    public static bool IsSupportedLanguage(string? language)
        => language is not null && LanguageIndices.ContainsKey(language);

    public static string NormalizeLanguage(string? language)
        => LanguageIndices.Keys.FirstOrDefault(code =>
            string.Equals(code, language, StringComparison.OrdinalIgnoreCase))
            ?? DefaultLanguage;

    /// <summary>把旧版屏端的语言序号转换为动态语言代码。</summary>
    public static string FromLegacyIndex(int index)
    {{
        if (index < 0 || index >= LegacyLanguageCodes.Length)
            return DefaultLanguage;
        return IsSupportedLanguage(LegacyLanguageCodes[index])
            ? NormalizeLanguage(LegacyLanguageCodes[index])
            : DefaultLanguage;
    }}

    /// <summary>合并 Collector 启动时读取的 WPF 文案覆盖；未知 Web Key 会被忽略。</summary>
    public static void ApplyOverrides(IEnumerable<LocalizationOverrideDto> overrides)
    {{
        var values = Clone(BuiltinD);

        foreach (var item in overrides)
        {{
            if (!string.Equals(item.Resource, "Wpf", StringComparison.Ordinal)
                || !LanguageIndices.TryGetValue(NormalizeLanguage(item.CultureName), out var languageIndex))
                continue;

            var key = item.Key.StartsWith("Web_", StringComparison.Ordinal)
                ? item.Key[4..]
                : item.Key;
            if (values.TryGetValue(key, out var languageValues)
                && BuiltinD.TryGetValue(key, out var builtinValues)
                && HasCompatibleFormat(builtinValues[languageIndex], item.Value))
                languageValues[languageIndex] = item.Value;
        }}
        Interlocked.Exchange(ref s_values, values);
    }}

    private static bool HasCompatibleFormat(string expected, string actual)
    {{
        if (!TryGetPlaceholderSignature(expected, out var expectedSignature)
            || !TryGetPlaceholderSignature(actual, out var actualSignature)
            || !expectedSignature.OrderBy(pair => pair.Key)
                .SequenceEqual(actualSignature.OrderBy(pair => pair.Key)))
            return false;

        try
        {{
            var maxIndex = actualSignature.Keys.DefaultIfEmpty(-1).Max();
            var arguments = Enumerable.Range(0, maxIndex + 1)
                .Select(_ => (object)new FormatProbe())
                .ToArray();
            _ = string.Format(CultureInfo.InvariantCulture, actual, arguments);
            return true;
        }}
        catch (FormatException)
        {{
            return false;
        }}
        catch (ArgumentException)
        {{
            return false;
        }}
    }}

    private static bool TryGetPlaceholderSignature(
        string value,
        out Dictionary<int, int> signature)
    {{
        signature = new Dictionary<int, int>();
        var maxIndex = -1;
        for (var index = 0; index < value.Length;)
        {{
            if (value[index] == '{{')
            {{
                if (index + 1 < value.Length && value[index + 1] == '{{')
                {{
                    index += 2;
                    continue;
                }}

                index++;
                var start = index;
                while (index < value.Length && char.IsDigit(value[index])) index++;
                if (start == index || !int.TryParse(value[start..index], out var argumentIndex))
                    return false;
                maxIndex = Math.Max(maxIndex, argumentIndex);

                if (index < value.Length && value[index] == ',')
                {{
                    index++;
                    while (index < value.Length && char.IsWhiteSpace(value[index])) index++;
                    if (index < value.Length && value[index] == '-') index++;
                    start = index;
                    while (index < value.Length && char.IsDigit(value[index])) index++;
                    if (start == index) return false;
                    while (index < value.Length && char.IsWhiteSpace(value[index])) index++;
                }}

                if (index < value.Length && value[index] == ':')
                {{
                    index++;
                    while (index < value.Length && value[index] != '}}')
                    {{
                        if (value[index] == '{{') return false;
                        index++;
                    }}
                }}

                if (index >= value.Length || value[index] != '}}') return false;
                signature[argumentIndex] = signature.TryGetValue(argumentIndex, out var count)
                    ? count + 1
                    : 1;
                index++;
            }}
            else if (value[index] == '}}')
            {{
                if (index + 1 < value.Length && value[index + 1] == '}}')
                {{
                    index += 2;
                    continue;
                }}
                return false;
            }}
            else
            {{
                index++;
            }}
        }}
        return maxIndex < 0 || signature.Keys.All(argumentIndex => argumentIndex <= maxIndex);
    }}

    private sealed class FormatProbe : IFormattable
    {{
        public string ToString(string? format, IFormatProvider? formatProvider) => "x";
        public override string ToString() => "x";
    }}

    private static Dictionary<string, string[]> Clone(Dictionary<string, string[]> source)
        => source.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());

    public static string T(string key, params object[] args)
    {{
        var values = Volatile.Read(ref s_values);
        if (values.TryGetValue(key, out var languageValues))
        {{
            var languageIndex = LanguageIndices.TryGetValue(Current, out var index) ? index : 0;
            var text = languageValues[languageIndex];
            if (args.Length == 0) return text;
            try
            {{
                return string.Format(text, args);
            }}
            catch (FormatException)
            {{
                return text;
            }}
        }}
        return key;
    }}
}}
'''


def rendered_outputs(rows: list[dict[str, str]], scope: str) -> dict[Path, str]:
    outputs: dict[Path, str] = {}
    # The catalog is shared by MainAPP, Collector and the override validator.
    # Generate it for every build scope so a CSV edit cannot leave it stale.
    outputs[PROJECT_ROOT / "Kanban.Collector.Core" / "Localization" / "LocalizationCatalog.cs"] = \
        generate_core_localization_catalog(rows)
    if scope in ("all", "wpf"):
        outputs.update(generate_resx(rows, "Wpf"))
        outputs[PROJECT_ROOT / "MainAPP" / "Resources" / "Strings.cs"] = generate_wpf_strings_cs(rows)
    if scope in ("all", "core"):
        outputs.update(generate_resx(rows, "Core"))
    if scope in ("all", "web"):
        outputs[PROJECT_ROOT / "Kanban.Web" / "Localization.cs"] = generate_web_loc(rows)
    return outputs


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bootstrap", action="store_true", help="migrate existing RESX files into Localization.csv")
    parser.add_argument("--check", action="store_true", help="verify generated files without writing them")
    parser.add_argument("--wpf", action="store_true", help="generate WPF RESX files and Strings.cs")
    parser.add_argument("--core", action="store_true", help="generate Collector.Core Messages RESX files")
    parser.add_argument("--web", action="store_true", help="generate the Web localization dictionary")
    parser.add_argument(
        "--coverage-report",
        action="store_true",
        help="print per-resource language coverage and likely English fallback ratios",
    )
    parser.add_argument(
        "--max-wpf-english-fallback-ratio",
        type=float,
        help="fail when a non-default WPF language exceeds this likely English fallback ratio",
    )
    parser.add_argument(
        "--max-wpf-english-fallback-count",
        type=int,
        help="fail when a non-default WPF language exceeds this likely English fallback count",
    )
    args = parser.parse_args(argv)

    try:
        if args.bootstrap:
            bootstrap_csv()
        rows = load_csv()
        if args.coverage_report or args.max_wpf_english_fallback_ratio is not None:
            print_coverage(rows)
        if args.max_wpf_english_fallback_ratio is not None:
            validate_fallback_ratio(rows, args.max_wpf_english_fallback_ratio)
        if args.max_wpf_english_fallback_count is not None:
            validate_fallback_count(rows, args.max_wpf_english_fallback_count)
        if args.max_wpf_english_fallback_count is not None:
            validate_fallback_count(rows, args.max_wpf_english_fallback_count)
        scope = "all"
        if args.wpf:
            scope = "wpf"
        elif args.core:
            scope = "core"
        elif args.web:
            scope = "web"

        outputs = rendered_outputs(rows, scope)
        mismatches: list[Path] = []
        expected_paths = set(outputs)
        stale_paths: set[Path] = set()
        if scope in ("all", "wpf"):
            stale_paths.update(generated_resx_paths("Wpf") - expected_paths)
        if scope in ("all", "core"):
            stale_paths.update(generated_resx_paths("Core") - expected_paths)
        for path, content in outputs.items():
            if args.check:
                current = path.read_text(encoding="utf-8") if path.exists() else None
                if current != content:
                    mismatches.append(path)
            else:
                write_if_changed(path, content)

        if args.check:
            mismatches.extend(sorted(stale_paths))
        else:
            for path in stale_paths:
                path.unlink()

        if mismatches:
            print("Generated localization files are out of date:", file=sys.stderr)
            for path in mismatches:
                print(f"  {path}", file=sys.stderr)
            return 1
        if not args.check:
            print(f"Generated localization scope={scope} from {len(rows)} CSV rows")
        return 0
    except (OSError, ET.ParseError, ValueError) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
