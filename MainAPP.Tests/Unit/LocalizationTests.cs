using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml.Linq;
using Kanban.Contracts.Dtos;
using Kanban.Collector.Core.Localization;
using Kanban.Collector.Core.Services;
using MainAPP.Resources;
using MainAPP.Services;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// 多语言测试：CSV 配置语言 resx key 一致性（防翻译缺失）、语言映射、启动应用、AppSettings 持久化。
/// </summary>
[Trait("Category", "Unit")]
[Collection("LocalizationSensitive")]
public class LocalizationTests
{
    private static string CreateOverrideFile(string content, System.Text.Encoding? encoding = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"KanbanLocalizationOverride_{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, content, encoding ?? new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    private static IReadOnlyList<string> ConfiguredLanguages()
    {
        var csvPath = Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "MainAPP", "Resources", "Localization.csv");
        return File.ReadLines(csvPath)
            .First()
            .TrimStart('\ufeff')
            .Split(',')
            .Skip(2)
            .ToArray();
    }

    private static string ResxFileName(string language)
        => language switch
        {
            "zh-CN" => "Strings.resx",
            "en-US" => "Strings.en.resx",
            "ja-JP" => "Strings.ja.resx",
            "pt-BR" => "Strings.pt-BR.resx",
            _ => $"Strings.{language}.resx",
        };

    /// <summary>读取源 resx 文件（XML）的 key→value 映射。
    /// 用源文件而非编译后的 .resources（后者为二进制格式）；验证的是源文件 key 一致性。</summary>
    private static Dictionary<string, string> ReadKeys(string language)
    {
        var fileName = ResxFileName(language);
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "MainAPP", "Resources", fileName);
        var doc = XDocument.Load(path);
        return doc.Root!
            .Elements("data")
            .ToDictionary(e => e.Attribute("name")!.Value, e => e.Element("value")!.Value);
    }

    [Fact]
    public void AllConfiguredLocales_HaveIdenticalKeySets_AndNonEmptyValues()
    {
        var resources = ConfiguredLanguages().ToDictionary(language => language, ReadKeys);
        var first = resources.Values.First();

        Assert.NotEmpty(first);
        foreach (var (language, values) in resources)
        {
            Assert.Equal(first.Keys.OrderBy(k => k), values.Keys.OrderBy(k => k));

            foreach (var key in first.Keys)
                Assert.False(string.IsNullOrWhiteSpace(values[key]), $"{language} 值缺失: {key}");
        }
    }

    [Fact]
    public void GetCultureName_MapsAllLanguages()
    {
        Assert.Equal("zh-CN", Localization.GetCultureName(AppLanguage.Zh));
        Assert.Equal("en-US", Localization.GetCultureName(AppLanguage.En));
        Assert.Equal("ja-JP", Localization.GetCultureName(AppLanguage.Ja));
        Assert.Equal("pt-BR", Localization.GetCultureName(AppLanguage.PtBr));
    }

    [Fact]
    public void LanguageCatalog_MatchesCsvHeader_AndSupportsDynamicCodes()
    {
        var csvPath = Path.Combine(AppContext.BaseDirectory,
            "../../../../MainAPP/Resources/Localization.csv");
        var header = File.ReadLines(csvPath, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            .First()
            .TrimStart('\ufeff')
            .Split(',');

        Assert.Equal(header.Skip(2), LocalizationCatalog.LanguageCodes);
        Assert.Equal("zh-CN", LocalizationCatalog.Normalize("ZH-cn"));
        Assert.True(LocalizationCatalog.IsSupported("pt-br"));
        Assert.False(LocalizationCatalog.IsSupported("xx-YY"));
    }

    [Fact]
    public void AppSettings_LanguageCode_TakesPrecedenceOverLegacyEnum()
    {
        var app = new AppSettings
        {
            Language = AppLanguage.En,
            LanguageCode = "PT-br",
        };

        Assert.Equal("pt-BR", app.EffectiveLanguageCode);
        Assert.Equal(AppLanguage.PtBr, AppSettings.LegacyLanguage(app.EffectiveLanguageCode));
    }

    [Fact]
    public void AppSettings_NewConfiguredLanguage_IsNotOverwrittenByLegacyEnum()
    {
        var app = new AppSettings { LanguageCode = "zh-CN" };
        app.LanguageCode = LocalizationCatalog.LanguageCodes.First();

        Assert.Equal(LocalizationCatalog.DefaultLanguage, app.EffectiveLanguageCode);
    }

    [Fact]
    public void Apply_SetsCurrentUICulture()
    {
        Localization.Apply(AppLanguage.En);
        Assert.Equal("en-US", CultureInfo.CurrentUICulture.Name);

        Localization.Apply(AppLanguage.Ja);
        Assert.Equal("ja-JP", CultureInfo.CurrentUICulture.Name);

        Localization.Apply(AppLanguage.PtBr);
        Assert.Equal("pt-BR", CultureInfo.CurrentUICulture.Name);

        Localization.Apply(AppLanguage.Zh); // 还原，避免影响并行测试
        Assert.Equal("zh-CN", CultureInfo.CurrentUICulture.Name);
    }

    [Fact]
    public void Apply_Forces24HourTimePatterns_EvenForEnglishCulture()
    {
        Localization.Apply(AppLanguage.En);
        Assert.Equal("HH:mm", CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern);
        Assert.Equal("HH:mm:ss", CultureInfo.CurrentCulture.DateTimeFormat.LongTimePattern);
        Assert.Equal("20:05", new DateTime(2026, 9, 16, 20, 5, 0).ToString("t"));
        Localization.Apply(AppLanguage.Zh);
    }

    [Fact]
    public void Strings_ResolvesPerCulture()
    {
        Localization.Apply(AppLanguage.Zh);
        Assert.Equal("主页", Strings.Nav_Home);
        Assert.Equal("运行", Strings.Status_Running);

        Localization.Apply(AppLanguage.En);
        Assert.Equal("Home", Strings.Nav_Home);
        Assert.Equal("Running", Strings.Status_Running);

        Localization.Apply(AppLanguage.Ja);
        Assert.Equal("ホーム", Strings.Nav_Home);
        Assert.Equal("稼働中", Strings.Status_Running);

        Localization.Apply(AppLanguage.PtBr);
        Assert.Equal("Início", Strings.Nav_Home);
        Assert.Equal("Em execução", Strings.Status_Running);
        Assert.Equal("Português", Strings.Language_Portuguese);

        Localization.Apply(AppLanguage.Zh);
    }

    [Fact]
    public void AppSettings_Language_DefaultsToZh_AndPersistsRoundTrip()
    {
        var app = new AppSettings();
        Assert.Equal(AppLanguage.Zh, app.Language); // 默认中文

        app.Language = AppLanguage.PtBr;
        var json = System.Text.Json.JsonSerializer.Serialize(app, AppSettings.JsonOptions);
        var restored = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json, AppSettings.JsonOptions);
        Assert.NotNull(restored);
        Assert.Equal(AppLanguage.PtBr, restored!.Language);
    }

    [Fact]
    public void OverrideLoader_AppliesExistingKeys_AndAllowsPartialLanguageColumns()
    {
        var path = CreateOverrideFile("""
Resource,Key,zh-CN,en-US,ja-JP,pt-BR
Wpf,Nav_Home,,,,Página inicial
Core,PlcIpEmpty,,PLC host is empty,,
""");
        try
        {
            var result = LocalizationOverrideLoader.Load(
                path,
                Strings.IsKnownKey,
                Strings.GetEmbeddedValue);

            Assert.True(result.FileFound);
            Assert.True(result.IsValid, string.Join("; ", result.Errors));
            Assert.Equal(2, result.AppliedCount);

            Localization.Apply(AppLanguage.PtBr);
            Assert.Equal("Página inicial", Strings.Nav_Home);
            Localization.Apply(AppLanguage.En);
            ValidationMessages.ApplyLanguage(Localization.GetCultureName(AppLanguage.En));
            Assert.Equal("PLC host is empty", ValidationMessages.PlcIpEmpty);
        }
        finally
        {
            LocalizationOverrideStore.Clear();
            ValidationMessages.ApplyLanguage(null);
            Localization.Apply(AppLanguage.Zh);
            File.Delete(path);
        }
    }

    [Fact]
    public void OverrideLoader_RejectsUnknownKey_AndClearsAllOverrides()
    {
        var path = CreateOverrideFile("""
Resource,Key,zh-CN,en-US,ja-JP,pt-BR
Wpf,Nav_Home,主页覆盖,,,
Wpf,NotAnExistingKey,无效,,,
""");
        try
        {
            var result = LocalizationOverrideLoader.Load(
                path,
                Strings.IsKnownKey,
                Strings.GetEmbeddedValue);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, error => error.Contains("不存在资源 Key"));
            Localization.Apply(AppLanguage.Zh);
            Assert.Equal("主页", Strings.Nav_Home);
        }
        finally
        {
            LocalizationOverrideStore.Clear();
            File.Delete(path);
        }
    }

    [Fact]
    public void OverrideLoader_RejectsPlaceholderMismatch_AndFallsBackToEmbeddedText()
    {
        var path = CreateOverrideFile("""
Resource,Key,zh-CN,en-US,ja-JP,pt-BR
Wpf,Common_RestartRequired,翻译 {0},,,
""");
        try
        {
            var result = LocalizationOverrideLoader.Load(
                path,
                Strings.IsKnownKey,
                Strings.GetEmbeddedValue);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, error => error.Contains("占位符不一致"));
            Localization.Apply(AppLanguage.Zh);
            Assert.Equal("语言切换将在重启后生效。", Strings.Common_RestartRequired);
        }
        finally
        {
            LocalizationOverrideStore.Clear();
            File.Delete(path);
        }
    }

    [Fact]
    public void OverrideLoader_ValidatesWpfKeysWithoutWpfResourceManager()
    {
        var path = CreateOverrideFile("""
Resource,Key,zh-CN,en-US,ja-JP,pt-BR
Wpf,NotAnExistingKey,无效值,,,
""");
        try
        {
            var result = LocalizationOverrideLoader.Load(path);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, error => error.Contains("不存在资源 Key"));
            Assert.Empty(LocalizationOverrideStore.Snapshot());
        }
        finally
        {
            LocalizationOverrideStore.Clear();
            File.Delete(path);
        }
    }

    [Fact]
    public void OverrideLoader_AcceptsSemicolonAndCaseInsensitiveLanguageHeaders()
    {
        var path = CreateOverrideFile("""
Resource;Key;ZH-cn;EN-us;JA-jp;PT-br
Wpf;Nav_Home;;Página inicial;;
""");
        try
        {
            var result = LocalizationOverrideLoader.Load(path);

            Assert.True(result.IsValid, string.Join("; ", result.Errors));
            Assert.Equal(1, result.AppliedCount);
            Localization.Apply(AppLanguage.En);
            Assert.Equal("Página inicial", Strings.Nav_Home);
        }
        finally
        {
            LocalizationOverrideStore.Clear();
            Localization.Apply(AppLanguage.Zh);
            File.Delete(path);
        }
    }

    [Fact]
    public void OverrideLoader_AcceptsTabSeparatedUtf8WithBom()
    {
        var path = CreateOverrideFile(
            "Resource\tKey\tzh-CN\ten-US\tja-JP\tpt-BR\nWpf\tNav_Home\t\tTab home\t\t\n",
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        try
        {
            var result = LocalizationOverrideLoader.Load(path);

            Assert.True(result.IsValid, string.Join("; ", result.Errors));
            Assert.Equal(1, result.AppliedCount);
            Localization.Apply(AppLanguage.En);
            Assert.Equal("Tab home", Strings.Nav_Home);
        }
        finally
        {
            LocalizationOverrideStore.Clear();
            Localization.Apply(AppLanguage.Zh);
            File.Delete(path);
        }
    }

    [Fact]
    public void OverrideLoader_RejectsMalformedCompositeFormat()
    {
        var path = CreateOverrideFile("""
Resource,Key,zh-CN,en-US,ja-JP,pt-BR
Wpf,Nav_Home,"坏 {0",,,
""");
        try
        {
            var result = LocalizationOverrideLoader.Load(path);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, error => error.Contains("占位符不一致"));
            Assert.Empty(LocalizationOverrideStore.Snapshot());
        }
        finally
        {
            LocalizationOverrideStore.Clear();
            File.Delete(path);
        }
    }
}
