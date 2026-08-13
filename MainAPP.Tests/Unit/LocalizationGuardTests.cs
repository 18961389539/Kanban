using System.Globalization;
using System.IO;
using System.Resources;
using System.Text.RegularExpressions;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// 多语言资源一致性守卫：CI 自动阻止 key 缺失、漏翻译、重复 key 合入。
/// 涵盖：WPF Strings.resx/en.resx/ja.resx、Strings.cs 强类型类、WEB L.cs 字典、
/// Core ConnectionStatusMessages、以及 en/ja resx 的中文残留检测。
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public sealed class LocalizationGuardTests
{
    private static readonly ResourceManager Res = new("MainAPP.Resources.Strings", typeof(MainAPP.Resources.Strings).Assembly);

    // ─── 辅助：解析 resx 为 {key: value} 字典 ───
    private static Dictionary<string, string> ParseResx(string path)
    {
        var xml = File.ReadAllText(path);
        var dict = new Dictionary<string, string>();
        foreach (Match m in Regex.Matches(xml, @"<data name=""(\w+)""[^>]*>\s*<value>([^<]*)</value>", RegexOptions.Singleline))
            dict[m.Groups[1].Value] = m.Groups[2].Value;
        return dict;
    }

    private static string ResxPath(string fileName) =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../../../../MainAPP/Resources", fileName);

    // ─── 测试 1：Strings.cs 所有 key 在三语 resx 中都存在 ───
    [Fact]
    public void All_StringsCs_Keys_Exist_In_All_Three_Resx()
    {
        var csPath = ResxPath("Strings.cs");
        var csText = File.ReadAllText(csPath);
        var csKeys = Regex.Matches(csText, @"public static string \w+ => S\(""(\w+)"",")
            .Select(m => m.Groups[1].Value)
            .ToHashSet();

        var zh = ParseResx(ResxPath("Strings.resx"));
        var en = ParseResx(ResxPath("Strings.en.resx"));
        var ja = ParseResx(ResxPath("Strings.ja.resx"));

        var missingZh = csKeys.Where(k => !zh.ContainsKey(k)).ToList();
        var missingEn = csKeys.Where(k => !en.ContainsKey(k)).ToList();
        var missingJa = csKeys.Where(k => !ja.ContainsKey(k)).ToList();

        Assert.Empty(missingZh);
        Assert.Empty(missingEn);
        Assert.Empty(missingJa);
    }

    // ─── 测试 2：三语 resx 中所有 key 都在 Strings.cs 中定义（防僵尸 key）───
    [Fact]
    public void All_Resx_Keys_Defined_In_StringsCs()
    {
        var csText = File.ReadAllText(ResxPath("Strings.cs"));
        var csKeys = Regex.Matches(csText, @"public static string \w+ => S\(""(\w+)""")
            .Select(m => m.Groups[1].Value)
            .ToHashSet();

        // resx 系统 key (resmimetype/reader/writer/version) 是 VS 自动生成的，跳过
        var systemKeys = new HashSet<string> { "resmimetype", "reader", "writer", "version" };

        foreach (var lang in new[] { ("zh", "Strings.resx"), ("en", "Strings.en.resx"), ("ja", "Strings.ja.resx") })
        {
            var resx = ParseResx(ResxPath(lang.Item2));
            var extra = resx.Keys.Where(k => !csKeys.Contains(k) && !systemKeys.Contains(k)).ToList();
            Assert.True(extra.Count == 0,
                $"{lang.Item1} resx has {extra.Count} keys NOT in Strings.cs: {string.Join(", ", extra)}");
        }
    }

    // ─── 测试 3：en resx 不含 CJK 字符（防漏翻译）。ja 使用汉字（日本語漢字）属正常，跳过。───
    [Fact]
    public void En_Resx_Contain_No_Cjk_Characters()
    {
        var resx = ParseResx(ResxPath("Strings.en.resx"));
        var leaks = resx
            .Where(kv => Regex.IsMatch(kv.Value, @"[\u4e00-\u9fff]"))
            .Select(kv => $"{kv.Key}: {kv.Value[..Math.Min(40, kv.Value.Length)]}")
            .ToList();
        Assert.True(leaks.Count == 0,
            $"en resx has {leaks.Count} untranslated keys: {string.Join(", ", leaks.Take(10))}");
    }

    // ─── 测试 4：Strings.cs 无重复 key ───
    [Fact]
    public void StringsCs_Has_No_Duplicate_Keys()
    {
        var csText = File.ReadAllText(ResxPath("Strings.cs"));
        var allKeys = Regex.Matches(csText, @"public static string \w+ => S\(""(\w+)"",")
            .Select(m => m.Groups[1].Value);

        var dups = allKeys.GroupBy(k => k).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.Empty(dups);
    }

    // ─── 测试 5：全部 key 三语占位符数量一致（防 #{0} 不匹配；审查修复 2026-08-13 由 F 系列扩展到全部 key——
    // 此前仅覆盖 F key，M148 en 缺失 {0} 的误译得以漏网）───
    [Fact]
    public void All_Keys_Have_Consistent_Placeholder_Count_Across_Languages()
    {
        var zh = ParseResx(ResxPath("Strings.resx"));
        var en = ParseResx(ResxPath("Strings.en.resx"));
        var ja = ParseResx(ResxPath("Strings.ja.resx"));

        var fKeys = zh.Keys.Where(k => k.StartsWith("F")).ToList();
        Assert.True(fKeys.Count >= 40, $"Expected >=40 F-keys, found {fKeys.Count}");

        foreach (var key in zh.Keys)
        {
            if (!en.TryGetValue(key, out var enVal) || !ja.TryGetValue(key, out var jaVal)) continue;

            int CountPlaceholders(string s) => Regex.Matches(s, @"\{\d+").Count;

            var zhCount = CountPlaceholders(zh[key]);
            var enCount = CountPlaceholders(enVal);
            var jaCount = CountPlaceholders(jaVal);

            Assert.True(zhCount == enCount,
                $"Key '{key}' placeholder mismatch: zh={zhCount} en={enCount} (zh='{zh[key]}' en='{enVal}')");
            Assert.True(zhCount == jaCount,
                $"Key '{key}' placeholder mismatch: zh={zhCount} ja={jaCount}");
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

        var webText = File.ReadAllText(webPath);
        // 匹配生成格式：["key"] = new[] { ... }
        var webKeys = Regex.Matches(webText, @"\[""(\w+)""\]\s*=\s*new\[\]")
            .Select(m => m.Groups[1].Value)
            .ToHashSet();

        var csText = File.ReadAllText(ResxPath("Strings.cs"));
        var wpfKeys = Regex.Matches(csText, @"public static string \w+ => S\(""(\w+)""")
            .Select(m => m.Groups[1].Value)
            .ToHashSet();

        // 无特殊映射：Web key 命名与 WPF resx 完全一致（Web_ 前缀 key 去前缀 或 同名共享 key）。
        // 生成脚本 ci/generate_web_loc.py 的 SHARED_KEYS 白名单已保证一致性，此处仅防漂移。

        var missing = new List<string>();
        foreach (var wk in webKeys)
        {
            // 1. Web_ 前缀
            if (wpfKeys.Contains($"Web_{wk}")) continue;
            // 2. 直接匹配
            if (wpfKeys.Contains(wk)) continue;
            // 3. 未找到
            missing.Add($"{wk} (no WPF counterpart)");
        }

        Assert.True(missing.Count == 0,
            $"WEB keys with no WPF counterpart: {string.Join(", ", missing)}");
    }

    // ─── 测试 10：WEB Localization.cs 不得包含模板占位符（防止 generate_web_loc.py 未运行）───
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
            $"(run python ci/generate_web_loc.py): {string.Join(", ", placeholders.Take(5))}");
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

    // ─── 测试 8：ResourceManager 能正确加载三语（卫星程序集完整）───
    [Fact]
    public void ResourceManager_Loads_All_Three_Languages()
    {
        // 中文（中性资源）
        Assert.NotNull(Res.GetString("Status_Running", CultureInfo.GetCultureInfo("zh-CN")));

        // 英文
        Assert.Equal("Running", Res.GetString("Status_Running", CultureInfo.GetCultureInfo("en-US")));

        // 日文
        Assert.NotNull(Res.GetString("Status_Running", CultureInfo.GetCultureInfo("ja-JP")));
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

    // ─── 辅助：从源码中提取指定方法/属性体（花括号匹配）───
    private static string ExtractMemberBody(string content, string memberSignature)
    {
        var idx = content.IndexOf(memberSignature, StringComparison.Ordinal);
        if (idx < 0) return string.Empty;
        var braceStart = content.IndexOf('{', idx);
        if (braceStart < 0) return string.Empty;
        int depth = 1;
        int i = braceStart + 1;
        while (i < content.Length && depth > 0)
        {
            if (content[i] == '{') depth++;
            else if (content[i] == '}') depth--;
            i++;
        }
        return content.Substring(braceStart, i - braceStart);
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
            // 排除日志调用
            if (trimmed.Contains("_logger.Log") || trimmed.Contains("Log.Warning") ||
                trimmed.Contains("Log.Error") || trimmed.Contains("Log.Information") ||
                trimmed.Contains("Log.Debug") || trimmed.Contains("Log.Fatal") ||
                trimmed.Contains("Serilog.Log.") || trimmed.Contains("logger.Log"))
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
            "../../../../MainAPP/Services/CountAlarmCsvIOService.cs",
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
    // 同时反射遍历 ValidationMessages 全部属性，断言三语 resx 均有对应 key（防新增属性漏配资源）。
    [Fact]
    public void Core_Messages_ResourceName_Follows_Assembly_Convention()
    {
        var assembly = typeof(Kanban.Collector.Core.Localization.ValidationMessages).Assembly;
        var rm = new ResourceManager("Kanban.Collector.Core.Resources.Messages", assembly);

        // 属性名 == resx key 名（ValidationMessages 的约定）；断言每个属性三语可解析
        var properties = typeof(Kanban.Collector.Core.Localization.ValidationMessages)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        Assert.True(properties.Length >= 20, $"ValidationMessages 属性数异常: {properties.Length}");

        foreach (var p in properties)
        {
            Assert.NotNull(rm.GetString(p.Name, CultureInfo.GetCultureInfo("zh-CN")));
            Assert.NotNull(rm.GetString(p.Name, CultureInfo.GetCultureInfo("en-US")));
            Assert.NotNull(rm.GetString(p.Name, CultureInfo.GetCultureInfo("ja-JP")));
        }
    }

}
