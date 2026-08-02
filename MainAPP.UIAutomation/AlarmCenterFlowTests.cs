using FlaUI.Core.AutomationElements;
using Xunit;

namespace MainAPP.UIAutomation;

/// <summary>
/// 报警中心页交互测试：时间范围切换、报警列表/事件流/排行列表渲染。
/// 无 PLC 连接时列表可能为空，仅验证控件存在与可交互性。
/// </summary>
[Collection("UIA")]
public class AlarmCenterFlowTests : IDisposable
{
    private readonly KanbanAppFixture _fixture;

    public AlarmCenterFlowTests() => _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void AlarmCenterPage_TimeRangeButtons_Clickable()
    {
        var window = _fixture.MainWindow;
        var automation = _fixture.Automation;
        UiaTestHelpers.NavigateToPage(window, automation, "报警中心");

        // 三个时间范围 ToggleButton
        foreach (var name in new[] { "近1小时", "近4小时", "近24小时" })
        {
            var btn = window.FindFirstDescendant(cf => cf.ByName(name));
            Assert.True(btn != null, $"未找到时间范围按钮：{name}");
            btn.AsButton().SafeInvoke();
            Thread.Sleep(500); // 等待列表刷新
        }
    }

    [Fact]
    public void AlarmCenterPage_ThreeListsVisible()
    {
        var window = _fixture.MainWindow;
        var automation = _fixture.Automation;
        UiaTestHelpers.NavigateToPage(window, automation, "报警中心");

        // 验证三个列表区域可见（即使内容为空，控件应已渲染）
        foreach (var name in new[] { "活跃报警列表", "最近事件流", "报警排行列表" })
        {
            var el = window.FindFirstDescendant(cf => cf.ByName(name));
            Assert.True(el != null, $"未找到报警中心列表：{name}");
        }
    }
}
