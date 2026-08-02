using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Xunit;

namespace MainAPP.UIAutomation;

/// <summary>
/// 启动与主导航端到端测试。
/// 主导航 ListBox（AutomationProperties.Name="主导航"）包含 8 项：主页/产线/报警中心/设备管理/历史查询/生产复盘/设置/工单管理。
/// 通过 item.Select() 切换页面，验证各页面标题/关键控件可见。
/// </summary>
[Collection("UIA")]
public class NavigationFlowTests : IDisposable
{
    private readonly KanbanAppFixture _fixture;

    public NavigationFlowTests() => _fixture = new();

    public void Dispose() => _fixture.Dispose();

    /// <summary>页面名称 → 切换后应可见的关键文本（用于断言页面已渲染）</summary>
    private static readonly (string NavName, string ExpectedText)[] s_allPages =
    [
        ("主页", "生产仪表板"),
        ("产线", "按设备名搜索"),
        ("报警中心", "活跃报警列表"),
        ("设备管理", "设备列表"),
        ("历史查询", "历史数据查询"),
        ("生产复盘", "时间范围选择"),
        ("设置", "PLC 连接配置"),
        ("工单管理", "工单"),  // 工单管理页无 AutomationProperties.Name，用"工单"文本兜底
    ];

    [Fact]
    public void AppLaunches_MainWindow_TitleAndHomeContentVisible()
    {
        var window = _fixture.MainWindow;
        Assert.Contains("看板", window.Title);

        // 主页标题应可见（OnMainWindowLoaded 预热结束后 SelectedIndex=0）
        var title = window.FindFirstDescendant(cf => cf.ByName("生产仪表板"));
        Assert.NotNull(title);

        // PLC 断线横幅可见（无 PLC 连接时应显示）
        var banner = window.FindFirstDescendant(cf => cf.ByName("PLC 未连接，实时数据可能已过期"));
        Assert.NotNull(banner);
    }

    [Fact]
    public void SidebarToggle_Click_CollapsesAndExpands()
    {
        var window = _fixture.MainWindow;
        var toggle = window.FindFirstDescendant(cf => cf.ByName("折叠展开侧边栏"));
        Assert.NotNull(toggle);

        // 折叠
        toggle.AsButton().SafeInvoke();
        Thread.Sleep(500); // 折叠动画 0.2s + 缓冲

        // 再次展开
        toggle.AsButton().SafeInvoke();
        Thread.Sleep(500); // 展开动画 0.2s + 缓冲

        // 主页内容仍可见，证明切换未破坏 UI
        var title = window.FindFirstDescendant(cf => cf.ByName("生产仪表板"));
        Assert.NotNull(title);
    }

    [Fact]
    public void Home_HmlFilterButtons_Clickable_NoException()
    {
        var window = _fixture.MainWindow;
        // 三个级别筛选 ToggleButton 已通过 AutomationProperties.Name 暴露给 UIA：
        // 补全 Name 后可直接 ByName 定位，无需再依赖 ByControlType(Button)+ByName("H/M/L") 的脆弱组合
        foreach (var name in new[] { "筛选 High 级别报警", "筛选 Medium 级别报警", "筛选 Low 级别报警" })
        {
            var btn = window.FindFirstDescendant(cf => cf.ByName(name));
            Assert.NotNull(btn);
            // ToggleButton 在 UIA 中表现为 Button + TogglePattern，AsButton().Click() 仍可触发 IsChecked 切换
            btn.AsButton().SafeInvoke();
            Thread.Sleep(200);
        }

        // 静音按钮也应可定位（补全 Name 前只能靠 ToolTip 间接定位）
        var mute = window.FindFirstDescendant(cf => cf.ByName("静音或恢复新报警闪烁"));
        Assert.NotNull(mute);
    }

    [Fact]
    public void Home_AllKpiSections_Displayed()
    {
        // 验证主页所有 KPI 区域文本可见（证明首屏渲染完整）
        var window = _fixture.MainWindow;
        string[] expectedTexts =
        {
            "生产仪表板", "设备状态", "生产概览", "实时故障",
            "OEE 综合效率", "缺陷帕累托（TOP5）"
        };
        foreach (var text in expectedTexts)
        {
            var el = window.FindFirstDescendant(cf => cf.ByName(text));
            Assert.True(el != null, $"未找到主页文本：{text}");
        }
    }

    /// <summary>
    /// 遍历全部 8 个导航页面，验证切换后关键文本可见。
    /// 这是导航完整性的端到端断言：确保每页 ContentControl 都正确渲染了对应 View。
    /// </summary>
    [Theory]
    [InlineData("主页", "生产仪表板")]
    [InlineData("产线", "按设备名搜索")]
    [InlineData("报警中心", "活跃报警列表")]
    [InlineData("设备管理", "设备列表")]
    [InlineData("历史查询", "历史数据查询")]
    [InlineData("生产复盘", "时间范围选择")]
    [InlineData("设置", "PLC 连接配置")]
    [InlineData("工单管理", "工单")]
    public void NavigateToAllPages_PageContentVisible(string navName, string expectedText)
    {
        var window = _fixture.MainWindow;
        var automation = _fixture.Automation;

        UiaTestHelpers.NavigateToPage(window, automation, navName);

        // 切换后验证页面关键文本可见（证明 View 已渲染）
        var el = window.FindFirstDescendant(cf => cf.ByName(expectedText));
        Assert.True(el != null, $"导航到「{navName}」后未找到文本「{expectedText}」");
    }

    /// <summary>
    /// 验证导航后再返回主页，主页内容仍正常（确保页面切换不破坏 DI 单例 VM 状态）。
    /// </summary>
    [Fact]
    public void NavigateAwayAndBack_HomeStillVisible()
    {
        var window = _fixture.MainWindow;
        var automation = _fixture.Automation;

        // 切到设备管理再切回主页
        UiaTestHelpers.NavigateToPage(window, automation, "设备管理");
        UiaTestHelpers.NavigateToPage(window, automation, "主页");

        var title = window.FindFirstDescendant(cf => cf.ByName("生产仪表板"));
        Assert.NotNull(title);
    }
}


