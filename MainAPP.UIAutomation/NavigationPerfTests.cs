using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using Xunit;

namespace MainAPP.UIAutomation;

/// <summary>
/// 导航切换性能回归：首轮预热（懒加载 XAML/VM），第二轮断言各页切换在 2s 内完成。
/// 2s 阈值覆盖 PageTransition 动画 + Background 优先级数据刷新，远高于可感知卡顿（~300ms）。
/// </summary>
[Collection("UIA")]
public sealed class NavigationPerfTests : IDisposable
{
    private readonly KanbanAppFixture _fixture;
    private readonly ITestOutputHelper _output;

    public NavigationPerfTests(ITestOutputHelper output)
    {
        _output = output;
        _fixture = new();
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void NavigateAllPages_WarmThenRevisit_CompletesWithinTwoSeconds()
    {
        var window = _fixture.MainWindow;
        var automation = _fixture.Automation;
        var navList = GetNavList(window, automation);
        Assert.NotNull(navList);

        var pageNames = navList!.Items
            .Select(item => item.Name ?? item.Text ?? string.Empty)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();
        Assert.True(pageNames.Count >= 10, $"侧边栏项过少：{string.Join(" / ", pageNames)}");

        // 首轮：懒加载 XAML + VM，不计入断言
        foreach (var name in pageNames)
        {
            var ms = NavigateAndWait(navList, name, out var ok);
            _output.WriteLine($"[warmup] {name}: {ms:F0}ms ok={ok}");
            Assert.True(ok, $"预热导航失败：{name}");
        }

        // 第二轮：视图已缓存，切换应无明显阻塞
        var slow = new List<string>();
        foreach (var name in pageNames)
        {
            var ms = NavigateAndWait(navList, name, out var ok);
            _output.WriteLine($"[revisit] {name}: {ms:F0}ms ok={ok}");
            Assert.True(ok, $"复访导航失败：{name}");
            if (ms > 2000) slow.Add($"{name}={ms:F0}ms");
        }

        Assert.True(slow.Count == 0,
            "复访切换超过 2s（可感知卡顿）：" + string.Join(", ", slow));
    }

    private static ListBox? GetNavList(FlaUI.Core.AutomationElements.Window window, UIA3Automation automation)
    {
        var cf = automation.ConditionFactory;
        var navList = window.FindFirstDescendant(cf.ByName("主导航"))?.AsListBox();
        if (navList != null) return navList;

        var menuButton = window.FindFirstDescendant(cf.ByName("显示导航菜单"))?.AsButton();
        if (menuButton != null)
        {
            menuButton.SafeInvoke();
            Thread.Sleep(800);
            navList = window.FindFirstDescendant(cf.ByName("主导航"))?.AsListBox();
        }

        return navList;
    }

    private static double NavigateAndWait(ListBox navList, string pageName, out bool success)
    {
        ListBoxItem? target = null;
        foreach (var item in navList.Items)
        {
            var label = item.Name ?? item.Text ?? string.Empty;
            if (label.Contains(pageName, StringComparison.Ordinal))
            {
                target = item;
                break;
            }
        }

        if (target is null)
        {
            success = false;
            return 0;
        }

        var sw = Stopwatch.StartNew();
        target.Select();

        while (sw.ElapsedMilliseconds < 5000)
        {
            var selected = navList.SelectedItem?.Name ?? navList.SelectedItem?.Text ?? string.Empty;
            if (selected.Contains(pageName, StringComparison.Ordinal))
            {
                success = true;
                return sw.ElapsedMilliseconds;
            }

            Thread.Sleep(30);
        }

        success = false;
        return sw.ElapsedMilliseconds;
    }
}
