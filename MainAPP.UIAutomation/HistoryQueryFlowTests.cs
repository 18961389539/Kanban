using System.Text;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using Xunit;
// v3: ITestOutputHelper 已从 Xunit.Abstractions 移入 Xunit 命名空间

namespace MainAPP.UIAutomation;

/// <summary>
/// 历史查询页流程测试。
/// 控件存在性断言（Raw 树）+ 交互流程（Tab 切换/查询/重置/导出）。
/// 通过主导航 ListBox 切换到历史查询页。
/// </summary>
[Collection("UIA")]
public class HistoryQueryFlowTests : IDisposable
{
    private readonly KanbanAppFixture _fixture;
    private readonly ITestOutputHelper _output;

    public HistoryQueryFlowTests(ITestOutputHelper output)
    {
        _fixture = new();
        _output = output;
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void HistoryQueryPage_ControlsExistInRawTree()
    {
        var window = _fixture.MainWindow;
        var walker = _fixture.Automation.TreeWalkerFactory.GetRawViewWalker();
        var names = new HashSet<string>();
        UiaTestHelpers.CollectNames(window, walker, names, depth: 0, maxDepth: 12);

        var dump = new StringBuilder();
        dump.AppendLine($"Raw tree 共收集到 {names.Count} 个非空 Name：");
        foreach (var n in names.OrderBy(x => x)) dump.AppendLine($"  - {n}");
        _output.WriteLine(dump.ToString());

        Assert.Contains("历史数据查询", names);
        Assert.Contains("从", names);
        Assert.Contains("至", names);
        Assert.Contains("产量查询", names);
        Assert.Contains("状态时长", names);
        Assert.Contains("报警记录", names);
        Assert.Contains("OEE 分析", names);
        Assert.Contains("查询", names);
        Assert.Contains("重置", names);
        Assert.Contains("导出", names);
        Assert.Contains("设备筛选", names);
        Assert.Contains("快捷时间选择", names);
        Assert.Contains("查询历史数据", names);
        Assert.Contains("重置筛选条件", names);
        Assert.Contains("导出当前 Tab 数据为 CSV", names);
        Assert.Contains("班次筛选", names);
        Assert.Contains("报警类型筛选", names);
        Assert.Contains("产量日志表", names);
    }

    /// <summary>
    /// 导航到历史查询页，验证 4 个 Tab 可切换。
    /// </summary>
    [Fact]
    public void HistoryQueryPage_TabSwitch_Clickable_NoException()
    {
        var window = _fixture.MainWindow;
        var automation = _fixture.Automation;
        UiaTestHelpers.NavigateToPage(window, automation, "历史查询");

        // 4 个 Tab Header（TabItem.Header 内的 TextBlock）
        foreach (var name in new[] { "产量查询", "状态时长", "报警记录", "OEE 分析" })
        {
            var tab = window.FindFirstDescendant(cf => cf.ByName(name));
            Assert.True(tab != null, $"未找到 Tab：{name}");
            tab.AsButton().SafeInvoke();
            Thread.Sleep(800); // 等待 Tab 内容按需加载
        }
    }

    /// <summary>
    /// 导航到历史查询页，点击"查询"按钮验证不抛异常。
    /// 无历史数据时结果为空，不影响测试通过。
    /// </summary>
    [Fact]
    public void HistoryQueryPage_QueryButton_Clickable_NoException()
    {
        var window = _fixture.MainWindow;
        var automation = _fixture.Automation;
        UiaTestHelpers.NavigateToPage(window, automation, "历史查询");

        var queryBtn = window.FindFirstDescendant(cf => cf.ByName("查询历史数据"));
        Assert.True(queryBtn != null, "未找到查询按钮");
        queryBtn.AsButton().Click();
        Thread.Sleep(2000); // 等待查询完成（空数据库快速返回）
    }

    /// <summary>
    /// 导航到历史查询页，点击"重置"按钮验证不抛异常。
    /// </summary>
    [Fact]
    public void HistoryQueryPage_ResetButton_Clickable_NoException()
    {
        var window = _fixture.MainWindow;
        var automation = _fixture.Automation;
        UiaTestHelpers.NavigateToPage(window, automation, "历史查询");

        var resetBtn = window.FindFirstDescendant(cf => cf.ByName("重置筛选条件"));
        Assert.True(resetBtn != null, "未找到重置按钮");
        resetBtn.AsButton().SafeInvoke();
        Thread.Sleep(500);
    }

    /// <summary>
    /// 导航到历史查询页，点击"导出"按钮验证不抛异常。
    /// 导出会弹出文件保存对话框（HC FileSaverDialog），需手动关闭或用 Win32 API 处理。
    /// 此测试默认 Skip，避免文件对话框阻塞测试。
    /// </summary>
    [Fact(Skip = "导出测试：会弹文件保存对话框阻塞测试，默认跳过。手动运行时需处理对话框。")]
    public void HistoryQueryPage_ExportButton_Clickable()
    {
        var window = _fixture.MainWindow;
        var automation = _fixture.Automation;
        UiaTestHelpers.NavigateToPage(window, automation, "历史查询");

        var exportBtn = window.FindFirstDescendant(cf => cf.ByName("导出当前 Tab 数据为 CSV"));
        Assert.True(exportBtn != null, "未找到导出按钮");
        exportBtn.AsButton().Click();
        Thread.Sleep(1000);
        // TODO: 处理文件保存对话框（Win32 API 发送 ESC 关闭，或用 FileSaverDialog 自动化）
    }
}
