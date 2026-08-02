using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using Xunit;

namespace MainAPP.UIAutomation;

/// <summary>
/// UI 自动化测试共享辅助方法：页面导航、控件查找、文本匹配等。
/// 提取自 SimulationFlowTests 的 NavigateToPage，供所有 FlowTests 复用。
/// </summary>
internal static class UiaTestHelpers
{
    /// <summary>
    /// 通过主导航 ListBox 切换到指定页面。
    /// 主导航 ListBox 的 AutomationProperties.Name="主导航"，包含 8 个 ListBoxItem（主页/产线/报警中心/设备管理/历史查询/生产复盘/设置/工单管理）。
    /// 用 Select() 触发 IsSelected TwoWay 绑定，等待 PageTransition 动画 + 数据渲染。
    /// </summary>
    public static void NavigateToPage(FlaUI.Core.AutomationElements.Window window, UIA3Automation automation, string pageName)
    {
        var cf = automation.ConditionFactory;
        var navList = window.FindFirstDescendant(cf.ByName("主导航"))?.AsListBox();
        Assert.NotNull(navList);
        foreach (var item in navList!.Items)
        {
            if (item.Name.Contains(pageName) || (item.Text ?? string.Empty).Contains(pageName))
            {
                item.Select();
                Thread.Sleep(1500); // PageTransition 0.2s + 数据绑定 + 缓冲
                return;
            }
        }
        Assert.Fail($"未找到导航项: {pageName}");
    }

    /// <summary>递归遍历元素树查找包含指定文本的后代</summary>
    public static bool ContainsTextRecursive(FlaUI.Core.AutomationElements.AutomationElement element, string text)
    {
        var name = element.Name ?? string.Empty;
        if (name.Contains(text)) return true;
        foreach (var child in element.FindAllChildren())
        {
            if (ContainsTextRecursive(child, text)) return true;
        }
        return false;
    }

    /// <summary>收集 Raw 树中所有非空 Name 的元素（用于控件存在性断言）</summary>
    public static void CollectNames(AutomationElement parent, FlaUI.Core.ITreeWalker walker,
        HashSet<string> names, int depth, int maxDepth)
    {
        if (depth >= maxDepth) return;
        var child = walker.GetFirstChild(parent);
        while (child != null)
        {
            if (!string.IsNullOrEmpty(child.Name))
                names.Add(child.Name);
            CollectNames(child, walker, names, depth + 1, maxDepth);
            child = walker.GetNextSibling(child);
        }
    }

    /// <summary>查找按钮：优先 ByName 精确匹配，回退到遍历所有按钮找文本包含</summary>
    public static Button? FindButton(FlaUI.Core.AutomationElements.Window window, UIA3Automation automation, string text)
    {
        var cf = automation.ConditionFactory;
        var btn = window.FindFirstDescendant(cf.ByControlType(ControlType.Button).And(cf.ByName(text)))?.AsButton();
        if (btn != null) return btn;

        // 回退：遍历所有按钮找文本匹配
        var allBtns = window.FindAllDescendants(cf.ByControlType(ControlType.Button));
        foreach (var b in allBtns)
        {
            if ((b.Name ?? string.Empty).Contains(text) || (b.HelpText ?? string.Empty).Contains(text))
                return b.AsButton();
        }
        return null;
    }

    /// <summary>
    /// 安全调用按钮：优先用 InvokePattern（不依赖鼠标），回退到 TogglePattern，最后回退到键盘。
    /// Click() 使用 SendInput API 模拟鼠标，在非交互式桌面会话（如终端/SSH）下会"拒绝访问"。
    /// Invoke() 通过 UIA InvokePattern 触发按钮，Toggle() 通过 TogglePattern 切换状态，均无需鼠标权限。
    /// 若两者均不可用（罕见），用键盘 Space 键激活焦点元素。
    /// </summary>
    public static void SafeInvoke(this Button button)
    {
        // 方案 1：InvokePattern（标准 Button）
        try { button.Invoke(); return; }
        catch { /* InvokePattern 不支持（如 ToggleButton），继续尝试 */ }

        // 方案 2：TogglePattern（ToggleButton）
        try
        {
            var toggle = button.AsToggleButton();
            if (toggle != null) { toggle.Toggle(); return; }
        }
        catch { /* TogglePattern 也不支持，继续尝试 */ }

        // 方案 3：键盘 Space（不依赖 SendInput 鼠标 API）
        try
        {
            button.Focus();
            Thread.Sleep(100);
            FlaUI.Core.Input.Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.SPACE);
            return;
        }
        catch { /* 键盘也失败，最后尝试 Click */ }

        // 方案 4：Click（可能"拒绝访问"，作为最后手段）
        button.Click();
    }

    /// <summary>
    /// 安全激活元素：对 Button 用 Invoke，对 TabItem/ListItem 用 Select，其他用 Click。
    /// </summary>
    public static void SafeActivate(this FlaUI.Core.AutomationElements.AutomationElement element)
    {
        var btn = element.AsButton();
        if (btn != null)
        {
            btn.SafeInvoke();
            return;
        }
        element.Click();
    }
}
