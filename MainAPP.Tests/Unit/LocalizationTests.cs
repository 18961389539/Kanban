using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml.Linq;
using Kanban.Core.Services;
using MainAPP.Resources;
using MainAPP.Services;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// 多语言测试：三语 resx key 一致性（防翻译缺失）、语言映射、启动应用、AppSettings 持久化。
/// </summary>
[Trait("Category", "Unit")]
public class LocalizationTests
{
    /// <summary>读取源 resx 文件（XML）的 key→value 映射。
    /// 用源文件而非编译后的 .resources（后者为二进制格式）；验证的是源文件 key 一致性。</summary>
    private static Dictionary<string, string> ReadKeys(string culture)
    {
        var fileName = culture == "zh" ? "Strings.resx" : $"Strings.{culture}.resx";
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "MainAPP", "Resources", fileName);
        var doc = XDocument.Load(path);
        return doc.Root!
            .Elements("data")
            .ToDictionary(e => e.Attribute("name")!.Value, e => e.Element("value")!.Value);
    }

    [Fact]
    public void AllThreeLocales_HaveIdenticalKeySets_AndNonEmptyValues()
    {
        var zh = ReadKeys("zh");
        var en = ReadKeys("en");
        var ja = ReadKeys("ja");

        Assert.NotEmpty(zh);
        Assert.Equal(zh.Keys.OrderBy(k => k), en.Keys.OrderBy(k => k));
        Assert.Equal(zh.Keys.OrderBy(k => k), ja.Keys.OrderBy(k => k));

        // 值非空且非纯空白（防占位/漏翻译）
        foreach (var key in zh.Keys)
        {
            Assert.False(string.IsNullOrWhiteSpace(zh[key]), $"中文值缺失: {key}");
            Assert.False(string.IsNullOrWhiteSpace(en[key]), $"英文值缺失: {key}");
            Assert.False(string.IsNullOrWhiteSpace(ja[key]), $"日文值缺失: {key}");
        }
    }

    [Fact]
    public void GetCultureName_MapsAllLanguages()
    {
        Assert.Equal("zh-CN", Localization.GetCultureName(AppLanguage.Zh));
        Assert.Equal("en-US", Localization.GetCultureName(AppLanguage.En));
        Assert.Equal("ja-JP", Localization.GetCultureName(AppLanguage.Ja));
    }

    [Fact]
    public void Apply_SetsCurrentUICulture()
    {
        Localization.Apply(AppLanguage.En);
        Assert.Equal("en-US", CultureInfo.CurrentUICulture.Name);

        Localization.Apply(AppLanguage.Ja);
        Assert.Equal("ja-JP", CultureInfo.CurrentUICulture.Name);

        Localization.Apply(AppLanguage.Zh); // 还原，避免影响并行测试
        Assert.Equal("zh-CN", CultureInfo.CurrentUICulture.Name);
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

        Localization.Apply(AppLanguage.Zh);
    }

    [Fact]
    public void AppSettings_Language_DefaultsToZh_AndPersistsRoundTrip()
    {
        var app = new AppSettings();
        Assert.Equal(AppLanguage.Zh, app.Language); // 默认中文

        app.Language = AppLanguage.Ja;
        var json = System.Text.Json.JsonSerializer.Serialize(app, AppSettings.JsonOptions);
        var restored = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json, AppSettings.JsonOptions);
        Assert.NotNull(restored);
        Assert.Equal(AppLanguage.Ja, restored!.Language);
    }
}
