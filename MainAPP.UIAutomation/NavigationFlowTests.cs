using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Xunit;

namespace MainAPP.UIAutomation;

/// <summary>
/// 启动与主导航端到端测试。
/// MainAPP 启动时自动登录内置 admin（App.xaml.cs 自动登录逻辑），侧边栏显示全部 13 项：
/// 主页/产线总览/报警中心/设备管理/工单管理/历史查询/生产复盘/设置/运行监控/用户管理/审计日志/配方管理/采集监控。
/// 导航断言策略：UIA 树中页面内容 TextBlock 默认不暴露 Name（无 AutomationProperties.Name），
/// 故用"主导航 ListBox 选中项 = 目标页"断言导航生效（SelectedIndex 双向绑定）；
/// 页面 View 的真实渲染由 E2E 层视觉树断言覆盖（MainAPP.E2E.NavigationFlowTests）。
/// 角色过滤（Operator 仅 7 项）由 E2E OperatorRole_HidesGatedPages 覆盖。
/// </summary>
[Collection("UIA")]
public class NavigationFlowTests : IDisposable
{
    private readonly KanbanAppFixture _fixture;

    public NavigationFlowTests() => _fixture = new();

    public void Dispose() => _fixture.Dispose();

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
        // 全屏启动时侧边栏 Collapsed，"折叠展开侧边栏"按钮在侧边栏内不可见；
        // 先点悬浮菜单按钮展开侧边栏（与 NavigateToPage 相同的前置处理）
        var toggle = window.FindFirstDescendant(cf => cf.ByName("折叠展开侧边栏"));
        if (toggle == null)
        {
            var menuButton = window.FindFirstDescendant(cf => cf.ByName("显示导航菜单"))?.AsButton();
            if (menuButton != null)
            {
                menuButton.SafeInvoke();
                Thread.Sleep(800);
            }
            toggle = window.FindFirstDescendant(cf => cf.ByName("折叠展开侧边栏"));
        }
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
            "生产仪表板", "设备状态", "当前生产状态", "实时故障",
            "OEE 综合效率", "缺陷帕累托（TOP5）"
        };
        foreach (var text in expectedTexts)
        {
            var el = window.FindFirstDescendant(cf => cf.ByName(text));
            Assert.True(el != null, $"未找到主页文本：{text}");
        }
    }

    /// <summary>
    /// 遍历全部 13 个导航页面：断言侧边栏选中项切换成功。
    /// 页面内容区 TextBlock 在 UIA 树中不暴露 Name（无 AutomationProperties.Name），
    /// 页面 View 渲染由 E2E 视觉树断言覆盖；主页为初始页，额外断言内容可见。
    /// </summary>
    [Theory]
    [InlineData("主页")]
    [InlineData("产线总览")]
    [InlineData("报警中心")]
    [InlineData("设备管理")]
    [InlineData("工单管理")]
    [InlineData("历史查询")]
    [InlineData("生产复盘")]
    [InlineData("设置")]
    [InlineData("运行监控")]
    [InlineData("用户管理")]
    [InlineData("审计日志")]
    [InlineData("配方管理")]
    [InlineData("采集监控")]
    public void NavigateToAllPages_SelectionChanges(string navName)
    {
        var window = _fixture.MainWindow;
        var automation = _fixture.Automation;

        UiaTestHelpers.NavigateToPage(window, automation, navName);

        // 导航生效断言：主导航 ListBox 选中项 = 目标页（SelectedIndex 双向绑定到 VM）
        var navList = window.FindFirstDescendant(cf => cf.ByName("主导航"))?.AsListBox();
        Assert.NotNull(navList);
        var selected = navList!.SelectedItem?.Name ?? navList.SelectedItem?.Text ?? "";
        Assert.True(selected.Contains(navName),
            $"导航到「{navName}」后选中项为「{selected}」");

        // 主页为初始页：内容在 UIA 树可见（TextBlock 直接暴露），断言渲染完整
        if (navName == "主页")
        {
            var title = window.FindFirstDescendant(cf => cf.ByName("生产仪表板"));
            Assert.NotNull(title);
        }
    }

    /// <summary>
    /// MainAPP 启动自动登录内置 admin（App.xaml.cs），侧边栏应显示全部 13 个导航项
    /// （含角色受限页：设备管理/配方管理=Engineer，设置/运行监控/用户管理/审计日志=Admin）。
    /// 这是"自动登录 + 角色过滤"在真实进程的回归断言——若侧边栏缺项，说明登录/过滤链路失效。
    /// Operator 视角的过滤行为（仅 7 项）由 E2E OperatorRole_HidesGatedPages 覆盖。
    /// </summary>
    [Fact]
    public void RoleGatedPages_VisibleForAdminAutoLogin()
    {
        var window = _fixture.MainWindow;
        var automation = _fixture.Automation;

        var navList = window.FindFirstDescendant(cf => cf.ByName("主导航"))?.AsListBox();
        if (navList == null)
        {
            // 全屏启动适配：展开侧边栏（与 NavigateToPage 相同的前置处理）
            var menuButton = window.FindFirstDescendant(cf => cf.ByName("显示导航菜单"))?.AsButton();
            if (menuButton != null)
            {
                menuButton.SafeInvoke();
                Thread.Sleep(800);
                navList = window.FindFirstDescendant(cf => cf.ByName("主导航"))?.AsListBox();
            }
        }
        Assert.NotNull(navList);

        var visibleNames = navList!.Items
            .Select(item => item.Name ?? item.Text ?? string.Empty)
            .ToList();
        Assert.True(visibleNames.Count > 0, "侧边栏导航项为空");

        // Admin 自动登录视角：13 项全部可见（含 6 个角色受限页）
        foreach (var expected in new[] { "主页", "产线总览", "报警中心", "设备管理", "工单管理", "历史查询",
            "生产复盘", "设置", "运行监控", "用户管理", "审计日志", "配方管理", "采集监控" })
        {
            Assert.True(visibleNames.Any(n => n.Contains(expected)),
                $"Admin 视角下侧边栏缺少「{expected}」，实际导航项：{string.Join(" / ", visibleNames)}");
        }
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


