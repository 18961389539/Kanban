using System.IO;
using MainAPP.Services;
using Xunit;

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
