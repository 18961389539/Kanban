using Kanban.Localization;
using Kanban.Collector.Core.Localization;
using Xunit;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace MainAPP.Tests.Unit;

/// <summary>
/// 多语言资源一致性守卫：CI 自动阻止 key 缺失、漏翻译、重复 key 合入。
/// 涵盖：Localization.csv、生成的 LocalizationCatalog、屏端 L.T 调用，以及非中文资源的中文残留检测。
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public sealed class LocalizationGuardTests
{
    private static string RepoPath(params string[] parts)
    {
        var path = AppDomain.CurrentDomain.BaseDirectory;
        foreach (var part in parts)
            path = Path.Combine(path, part);
        return path;
    }

    private static string CatalogPath() =>
        RepoPath("..", "..", "..", "..", "Kanban.Localization", "LocalizationCatalog.cs");

    private static IReadOnlyList<string> ConfiguredLanguages()
    {
        var csvPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
            "../../../../MainAPP/Resources/Localization.csv");
        return File.ReadLines(csvPath)
            .First()
            .TrimStart('\ufeff')
            .Split(',')
            .Skip(2)
            .ToArray();
    }

    private static HashSet<string> WpfAccessorKeys()
    {
        var csText = File.ReadAllText(CatalogPath());
        return Regex.Matches(csText, @"public static string \w+ => S\(""(\w+)""")
            .Select(m => m.Groups[1].Value)
            .ToHashSet();
    }

    [Fact]
    public void WpfAccessors_Match_Catalog_Keys()
    {
        var accessors = WpfAccessorKeys();
        var catalogKeys = LocalizationCatalog.Keys("Wpf").ToHashSet();
        Assert.Empty(accessors.Except(catalogKeys));
        Assert.Empty(catalogKeys.Except(accessors));
    }

    [Fact]
    public void NonCjk_Catalog_Values_Contain_No_Cjk_Characters()
    {
        foreach (var language in ConfiguredLanguages().Where(language =>
            !language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            && !language.StartsWith("ja", StringComparison.OrdinalIgnoreCase)))
        {
            var leaks = LocalizationCatalog.Keys("Wpf")
                .Select(key => (key, value: LocalizationCatalog.Get("Wpf", key, language) ?? ""))
                .Where(pair => Regex.IsMatch(pair.value, @"[\u4e00-\u9fff]"))
                .Select(pair => $"{pair.key}: {pair.value[..Math.Min(40, pair.value.Length)]}")
                .ToList();
            Assert.True(leaks.Count == 0,
                $"{language} catalog has {leaks.Count} CJK remnants: {string.Join(", ", leaks.Take(10))}");
        }
    }

    [Fact]
    public void StringsCs_Has_No_Duplicate_Keys()
    {
        var csText = File.ReadAllText(CatalogPath());
        var allKeys = Regex.Matches(csText, @"public static string \w+ => S\(""(\w+)"",")
            .Select(m => m.Groups[1].Value);

        var dups = allKeys.GroupBy(k => k).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.Empty(dups);
    }

    // ─── 测试 5：全部 key 在配置语言中占位符数量一致（防 #{0} 不匹配；审查修复 2026-08-13 由 F 系列扩展到全部 key——
    // 此前仅覆盖 F key，M148 en 缺失 {0} 的误译得以漏网）───
    [Fact]
    public void All_Keys_Have_Consistent_Placeholder_Count_Across_Languages()
    {
        var languages = ConfiguredLanguages().ToArray();
        var baselineLanguage = languages[0];
        int CountPlaceholders(string s) => Regex.Matches(s, @"\{\d+").Count;

        foreach (var resource in new[] { "Wpf", "Core" })
        {
            foreach (var key in LocalizationCatalog.Keys(resource))
            {
                var baseline = LocalizationCatalog.Get(resource, key, baselineLanguage) ?? "";
                var baselineCount = CountPlaceholders(baseline);
                foreach (var language in languages)
                {
                    var value = LocalizationCatalog.Get(resource, key, language);
                    Assert.False(value is null, $"{language} is missing {resource}/{key}");
                    Assert.Equal(baselineCount, CountPlaceholders(value!));
                }
            }
        }
    }

    // ─── 测试 6：WEB L.cs 所有 key 在 WPF Strings.cs 中有对应 ───
    [Fact]
    public void Web_LKeys_Have_Corresponding_Wpf_Keys()
    {
        var webPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../../../../Kanban.Web", "Localization.cs");
        if (!File.Exists(webPath))
        {
            // WEB 项目不在 CI 中时显式跳过（审查修复 2026-08-13：原静默 return 假通过，跳过应可统计）
            Assert.Skip("Kanban.Web/Localization.cs 不存在（当前 runner 未带 WEB 目录）");
        }

        var webRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../../../../Kanban.Web");
        var webKeys = Directory.EnumerateFiles(webRoot, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .SelectMany(path => Regex.Matches(File.ReadAllText(path), @"L\.T\(\s*""(\w+)""")
                .Select(match => match.Groups[1].Value))
            .ToHashSet();
        var wpfKeys = WpfAccessorKeys();

        var missing = new List<string>();
        foreach (var wk in webKeys)
        {
            if (wpfKeys.Contains($"Web_{wk}")) continue;
            if (wpfKeys.Contains(wk)) continue;
            missing.Add($"{wk} (no WPF counterpart)");
        }

        Assert.True(missing.Count == 0,
            $"WEB keys with no WPF counterpart: {string.Join(", ", missing)}");
    }

    // ─── 测试 10：WEB Localization.cs 不得包含模板占位符（防止生成器未运行）───
    [Fact]
    public void Web_Localization_Has_No_Template_Placeholders()
    {
        var webPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../../../../Kanban.Web", "Localization.cs");
        if (!File.Exists(webPath))
        {
            // WEB 项目不在 CI 中时显式跳过（审查修复 2026-08-13：原静默 return 假通过）
            Assert.Skip("Kanban.Web/Localization.cs 不存在（当前 runner 未带 WEB 目录）");
        }

        var webText = File.ReadAllText(webPath);
        var placeholders = Regex.Matches(webText, @"\{(zh_val|en_val|ja_val)\}")
            .Select(m => m.Value)
            .ToList();

        Assert.True(placeholders.Count == 0,
            $"Kanban.Web/Localization.cs contains {placeholders.Count} template placeholders " +
            $"(run python ci/generate_localization.py --web): {string.Join(", ", placeholders.Take(5))}");
    }

    // ─── 测试 7：Core ConnectionStatusMessages 已由 Localization.Apply 驱动，不再硬编码 ───
    [Fact]
    public void ConnectionStatusMessages_Can_Be_Overridden_By_Wpf_Resx()
    {
        // 验证 Override 方法存在且可被调用（不抛异常）
        Kanban.Collector.Core.Localization.ConnectionStatusMessages.Override(
            connected: "Connected",
            disconnected: "Disconnected",
            connectionLost: "Connection Lost");

        Assert.Equal("Connected", Kanban.Collector.Core.Localization.ConnectionStatusMessages.Connected);
        Assert.Equal("Disconnected", Kanban.Collector.Core.Localization.ConnectionStatusMessages.Disconnected);

        // 还原默认中文（避免影响其他测试）
        Kanban.Collector.Core.Localization.ConnectionStatusMessages.Override();
    }

    // ─── 测试 8：ResourceManager 能正确加载 CSV 中配置的语言（卫星程序集完整）───
    [Fact]
    public void ResourceManager_Loads_All_Configured_Languages()
    {
        foreach (var language in ConfiguredLanguages())
        {
            var value = LocalizationCatalog.Get("Wpf", "Status_Running", language);
            Assert.False(string.IsNullOrWhiteSpace(value), $"目录未加载语言 {language}");
        }
    }

    [Fact]
    public void LocalizationCsv_IsTheGeneratedResourceSource()
    {
        var csvPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
            "../../../../MainAPP/Resources/Localization.csv");
        Assert.True(File.Exists(csvPath), $"Localization CSV not found: {csvPath}");

        var lines = File.ReadAllLines(csvPath);
        Assert.NotEmpty(lines);
        var header = lines[0].TrimStart('\ufeff').Split(',');
        Assert.Equal(["Resource", "Key"], header.Take(2));
        Assert.Equal(LocalizationCatalog.LanguageCodes, header.Skip(2));
        Assert.Contains(lines, line => line.StartsWith("Wpf,Nav_Home,"));
        Assert.Contains(lines, line => line.StartsWith("Core,PlcIpEmpty,"));
        Assert.Contains(lines, line => line.Contains(",Language_Portuguese,"));
    }

    // ─── 测试 9：目录扫描 — 不得新增硬编码中文显示字符串 ───
    // 扫描 MainAPP/ViewModels/ + MainAPP/Services/ + MainAPP/Converters/ 全目录，
    // 排除：注释行、nameof()、日志调用（_logger.Log* / Log.* / Serilog.Log.*）、
    //       异常构造（throw new *Exception）、测试特性（[Trait]/[Theory]/[InlineData]/[Fact]）、
    //       样本数据文件（SampleDeviceBuilder/SampleDataSeeder/SampleWorkOrderBuilder）。
    // baseline 记录已有违规（LocalizationChineseLeakBaseline.txt），新增违规会失败。
    // 清理已有违规后，运行 python ci/scan_chinese_leaks.py 更新 baseline（可选，不更新也不会失败）。
    [Fact]
    public void No_New_Hardcoded_Chinese_DisplayStrings_In_Scanned_Dirs()
    {
        var scanDirs = new[]
        {
            "../../../../MainAPP/ViewModels",
            "../../../../MainAPP/Services",
            "../../../../MainAPP/Converters",
        };
        var excludeFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SampleDeviceBuilder.cs", "SampleDataSeeder.cs", "SampleWorkOrderBuilder.cs",
        };

        var baselinePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
            "LocalizationChineseLeakBaseline.txt");
        var baseline = File.Exists(baselinePath)
            ? new HashSet<string>(File.ReadAllLines(baselinePath))
            : new HashSet<string>();

        var actual = new HashSet<string>();
        foreach (var dir in scanDirs)
        {
            var fullPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, dir);
            if (!Directory.Exists(fullPath)) continue;
            foreach (var file in Directory.EnumerateFiles(fullPath, "*.cs", SearchOption.AllDirectories))
            {
                var fileName = Path.GetFileName(file);
                if (excludeFiles.Contains(fileName)) continue;
                var content = File.ReadAllText(file);
                foreach (var leak in FindChineseDisplayStrings(content))
                    actual.Add($"{fileName}\t{leak}");
            }
        }

        // 不在 baseline 中的违规 = 新增违规
        var newLeaks = actual.Except(baseline).ToList();
        Assert.True(newLeaks.Count == 0,
            $"Found {newLeaks.Count} NEW hardcoded Chinese display strings (not in baseline).\n" +
            $"Either migrate to resx (preferred) or add to baseline if intentional:\n" +
            string.Join("\n", newLeaks.Take(30)));
    }

    // ─── 辅助：检测代码中的中文显示字符串（排除注释/日志/异常/nameof/测试特性）───
    private static List<string> FindChineseDisplayStrings(string code)
    {
        var leaks = new List<string>();
        if (string.IsNullOrEmpty(code)) return leaks;
        foreach (var line in code.Split('\n'))
        {
            var trimmed = line.TrimStart();
            // 排除注释行（// /// *）
            if (trimmed.StartsWith("//") || trimmed.StartsWith("*"))
                continue;
            // 排除 nameof()
            if (trimmed.Contains("nameof("))
                continue;
            // 排除日志调用（_logger.Log*/Log.*/Serilog.Log.* 以及 _log.* 字段命名变体）
            if (trimmed.Contains("_logger.Log") || trimmed.Contains("Log.Warning") ||
                trimmed.Contains("Log.Error") || trimmed.Contains("Log.Information") ||
                trimmed.Contains("Log.Debug") || trimmed.Contains("Log.Fatal") ||
                trimmed.Contains("Serilog.Log.") || trimmed.Contains("logger.Log") ||
                trimmed.Contains("_log."))
                continue;
            // 排除异常构造
            if (trimmed.Contains("throw new "))
                continue;
            // 排除测试特性
            if (trimmed.StartsWith("[Trait") || trimmed.StartsWith("[Theory") ||
                trimmed.StartsWith("[InlineData") || trimmed.StartsWith("[Fact") ||
                trimmed.StartsWith("[Collection"))
                continue;
            // 检测字符串字面量（"..." / $"..." / @"..."）中的中文
            foreach (Match m in Regex.Matches(line, @"@?\$?""[^""]*"""))
            {
                if (Regex.IsMatch(m.Value, @"[\u4e00-\u9fff]"))
                {
                    var clean = m.Value.Trim().TrimStart('$').Trim('"').Trim();
                    leaks.Add(clean.Length > 100 ? clean[..100] : clean);
                }
            }
        }
        return leaks;
    }

    // ─── 测试 11：导出服务（PDF/CSV/日报）必须引用 resx 命名空间并调用 Strings.* ───
    // 架构守卫：防止未来重构时导出服务丢失 resx 依赖、回退到硬编码字符串。
    // 导出服务的列名/标题/对话框文本必须走 Strings.* 体系（由 P0-2 目录扫描守卫具体违规）。
    [Fact]
    public void Export_Services_Reference_Resx_Namespace_And_Strings()
    {
        var exportServices = new[]
        {
            "../../../../MainAPP/Services/ProductionReviewPdfService.cs",
            "../../../../MainAPP/Services/ProductionReviewCsvExportService.cs",
            "../../../../MainAPP/Services/AlarmCsvIOService.cs",
            "../../../../MainAPP/Services/DefectCsvIOService.cs",
            "../../../../MainAPP/Services/CounterAlarmCsvIOService.cs",
            "../../../../MainAPP/Services/RecipeJsonIOService.cs",
        };

        var missing = new List<string>();
        foreach (var relativePath in exportServices)
        {
            var fullPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, relativePath);
            if (!File.Exists(fullPath)) continue;

            var content = File.ReadAllText(fullPath);
            var fileName = Path.GetFileName(relativePath);

            // 必须引用 MainAPP.Resources 命名空间
            if (!content.Contains("using MainAPP.Resources;"))
                missing.Add($"{fileName}: missing 'using MainAPP.Resources;'");

            // 必须至少调用一次 Strings.* （确保 resx 真正被使用，而非仅 import 未用）
            if (!Regex.IsMatch(content, @"Strings\.\w+"))
                missing.Add($"{fileName}: no 'Strings.*' calls found");
        }

        Assert.True(missing.Count == 0,
            $"Export services must reference resx namespace and call Strings.*:\n{string.Join("\n", missing)}");
    }

    // ─── 测试 12：Core 共享文案资源名与程序集约定一致（RootNamespace=Kanban.Collector.Core）───
    // 防止 RootNamespace 被改回 MainAPP 或资源被重命名时，ValidationMessages/ConnectionStatusMessages
    // 的 ResourceManager 字符串与资源名脱节——脱节时编译期零告警、运行时静默找不到资源（历史事故点）。
    // 同时反射遍历 ValidationMessages 全部属性，断言配置语言 resx 均有对应 key（防新增属性漏配资源）。
    [Fact]
    public void Core_Messages_ResourceName_Follows_Assembly_Convention()
    {
        var properties = typeof(Kanban.Collector.Core.Localization.ValidationMessages)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        Assert.True(properties.Length >= 20, $"ValidationMessages 属性数异常: {properties.Length}");

        foreach (var property in properties)
        {
            foreach (var language in ConfiguredLanguages())
                Assert.False(string.IsNullOrWhiteSpace(LocalizationCatalog.Get("Core", property.Name, language)),
                    $"Core 目录缺少 {language}/{property.Name}");
        }
    }

}
