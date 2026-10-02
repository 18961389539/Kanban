using Xunit;
using System.IO;
using MainAPP.Services;

namespace MainAPP.Tests.Unit;

public class PageHelpContentTests
{
    private const string Manual = """
        # 手册

        <!-- page-help:home -->
        首页用来看当前班次。

        - 上方是状态。
        - 初始不是故障。
        <!-- /page-help -->

        <!-- page-help:history-oee -->
        综合值是三项相乘。

        - 时间稼动率：运行时间占比。
        <!-- /page-help -->
        """;

    [Fact]
    public void Extract_ReturnsParagraphsAndBulletsForTheRequestedPage()
    {
        var blocks = PageHelpContent.Extract(Manual, PageHelpContent.Home);

        Assert.Equal(3, blocks.Count);
        Assert.False(blocks[0].IsBullet);
        Assert.Equal("首页用来看当前班次。", blocks[0].Text);
        Assert.True(blocks[1].IsBullet);
        Assert.Equal("上方是状态。", blocks[1].Text);
        Assert.Equal("初始不是故障。", blocks[2].Text);
    }

    [Fact]
    public void Extract_ReadsSectionHeadings()
    {
        const string markdown = """
            <!-- page-help:home -->
            开篇。

            ### 上方三张卡

            - 设备状态。
            <!-- /page-help -->
            """;

        var blocks = PageHelpContent.Extract(markdown, PageHelpContent.Home);

        Assert.Equal(3, blocks.Count);
        Assert.True(blocks[1].IsHeading);
        Assert.False(blocks[1].IsBullet);
        Assert.Equal("上方三张卡", blocks[1].Text);
    }

    [Fact]
    public void Extract_DoesNotIncludeAnotherPage()
    {
        var blocks = PageHelpContent.Extract(Manual, PageHelpContent.HistoryOee);

        Assert.Equal(2, blocks.Count);
        Assert.Equal("综合值是三项相乘。", blocks[0].Text);
        Assert.True(blocks[1].IsBullet);
    }

    [Fact]
    public void Extract_ReadsBoldStepsAndColorTables()
    {
        const string markdown = """
            <!-- page-help:home -->
            点 **查询** 马上查。

            1. 先选设备。

            | 数字 | 颜色 |
            | --- | --- |
            | **良品率** | 绿 |
            <!-- /page-help -->
            """;

        var blocks = PageHelpContent.Extract(markdown, PageHelpContent.Home);

        Assert.Equal("点 查询 马上查。", blocks[0].Text);
        Assert.Contains(blocks[0].Runs, run => run.IsBold && run.Text == "查询");
        Assert.True(blocks[1].IsOrdered);
        Assert.Equal(1, blocks[1].Number);
        Assert.Equal("先选设备。", blocks[1].Text);
        Assert.True(blocks[2].IsTable);
        Assert.Equal(2, blocks[2].Rows.Count);
        Assert.True(blocks[2].Rows[0].IsHeader);
        Assert.Contains(blocks[2].Rows[1].Cells[0].Runs, run => run.IsBold && run.Text == "良品率");
    }

    [Fact]
    public void ExtractDocument_KeepsIntroOutsideSections()
    {
        const string markdown = """
            <!-- page-help:home -->
            开篇。

            ### 上方三张卡

            - 设备状态。
            <!-- /page-help -->
            """;

        var document = PageHelpContent.ExtractDocument(markdown, PageHelpContent.Home);

        Assert.Single(document.Intro);
        Assert.Equal("开篇。", document.Intro[0].Text);
        Assert.Single(document.Sections);
        Assert.Equal("上方三张卡", document.Sections[0].Title);
        Assert.Single(document.Sections[0].Blocks);
        Assert.True(document.Sections[0].Blocks[0].IsBullet);
    }

    [Fact]
    public void ChapterAnchor_UsesTheHeadingAboveTheMarker()
    {
        const string markdown = """
            ## 5. 首页：看懂生产情况

            <!-- page-help:home -->
            正文
            - 一条
            <!-- /page-help -->
            """;

        Assert.Equal("5-首页看懂生产情况", PageHelpContent.ChapterAnchor(markdown, PageHelpContent.Home));
        Assert.Equal("127-运行模式通用设置", PageHelpContent.Slugify("12.7 运行模式（通用设置）"));
    }

    [Fact]
    public void ShippedManuals_OpenOnTheChapterThatContainsTheGuide()
    {
        var root = FindRepoRoot();
        var chinese = File.ReadAllText(Path.Combine(root, "MainAPP", "手册", "MainAPP用户使用手册.md"));
        var english = File.ReadAllText(Path.Combine(root, "MainAPP", "手册", "MainAPP_User_Manual_EN.md"));

        Assert.Equal(
            PageHelpContent.Slugify("5. 首页：看懂生产情况"),
            PageHelpContent.ChapterAnchor(chinese, PageHelpContent.Home));
        Assert.Equal(
            PageHelpContent.Slugify("5. Home: Reading Production Status"),
            PageHelpContent.ChapterAnchor(english, PageHelpContent.Home));
        Assert.Equal(
            PageHelpContent.Slugify("17. 用户管理"),
            PageHelpContent.ChapterAnchor(chinese, PageHelpContent.Login));
    }

    [Fact]
    public void Extract_MissingKey_ReturnsEmpty()
    {
        Assert.Empty(PageHelpContent.Extract(Manual, PageHelpContent.AlarmCenter));
        Assert.Empty(PageHelpContent.Extract(null, PageHelpContent.Home));
        Assert.Empty(PageHelpContent.Extract(Manual, null));
    }

    [Fact]
    public void ShippedManuals_ContainPageGuides()
    {
        var root = FindRepoRoot();
        foreach (var fileName in new[] { "MainAPP用户使用手册.md", "MainAPP_User_Manual_EN.md" })
        {
            var markdown = File.ReadAllText(Path.Combine(root, "MainAPP", "手册", fileName));
            foreach (var key in new[]
                     {
                         PageHelpContent.Home,
                         PageHelpContent.AlarmCenter,
                         PageHelpContent.History,
                         PageHelpContent.HistoryOee,
                         PageHelpContent.ProductionLine,
                         PageHelpContent.WorkOrders,
                         PageHelpContent.Overview,
                         PageHelpContent.DeviceDetail,
                         PageHelpContent.RuntimeMonitor,
                         PageHelpContent.DeviceManager,
                         PageHelpContent.Settings,
                         PageHelpContent.Recipes,
                         PageHelpContent.Users,
                         PageHelpContent.Audit,
                         PageHelpContent.DataSource,
                         PageHelpContent.Login
                     })
            {
                var blocks = PageHelpContent.Extract(markdown, key);
                Assert.True(blocks.Count >= 2, $"{fileName} 缺少 {key}");
                Assert.Contains(blocks, block => block.IsBullet);
                if (key is PageHelpContent.Home
                    or PageHelpContent.History
                    or PageHelpContent.HistoryOee
                    or PageHelpContent.AlarmCenter
                    or PageHelpContent.DeviceDetail
                    or PageHelpContent.ProductionLine
                    or PageHelpContent.Overview
                    or PageHelpContent.WorkOrders
                    or PageHelpContent.DataSource
                    or PageHelpContent.RuntimeMonitor
                    or PageHelpContent.Audit)
                    Assert.Contains(blocks, block => block.IsTable);
            }
        }
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 8 && current is not null; depth++)
        {
            if (File.Exists(Path.Combine(current.FullName, "MainAPP", "手册", "MainAPP用户使用手册.md")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("未找到手册目录");
    }
}
