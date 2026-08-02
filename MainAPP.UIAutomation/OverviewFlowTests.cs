using FlaUI.Core.AutomationElements;
using Xunit;

namespace MainAPP.UIAutomation;

/// <summary>
/// 生产复盘页交互测试：时间范围选择、刷新按钮。
/// 复盘数据需要历史数据库有内容，无数据时图表为空，仅验证控件存在与可交互性。
/// </summary>
[Collection("UIA")]
public class OverviewFlowTests : IDisposable
{
    private readonly KanbanAppFixture _fixture;

    public OverviewFlowTests() => _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void OverviewPage_ControlsVisible()
    {
        var window = _fixture.MainWindow;
        var automation = _fixture.Automation;
        UiaTestHelpers.NavigateToPage(window, automation, "生产复盘");

        // 时间范围选择区与刷新按钮
        var range = window.FindFirstDescendant(cf => cf.ByName("时间范围选择"));
        Assert.True(range != null, "未找到时间范围选择控件");

        var refresh = window.FindFirstDescendant(cf => cf.ByName("刷新复盘数据"));
        Assert.True(refresh != null, "未找到刷新按钮");
    }

    [Fact]
    public void OverviewPage_RefreshButton_Clickable_NoException()
    {
        var window = _fixture.MainWindow;
        var automation = _fixture.Automation;
        UiaTestHelpers.NavigateToPage(window, automation, "生产复盘");

        var refresh = window.FindFirstDescendant(cf => cf.ByName("刷新复盘数据"));
        Assert.NotNull(refresh);
        refresh.AsButton().SafeInvoke();
        Thread.Sleep(1000); // 等待刷新完成（无数据时快速返回）
    }
}
