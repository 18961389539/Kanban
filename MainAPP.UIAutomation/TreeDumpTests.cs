using System.IO;
using System.Text;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using Xunit;
// v3: ITestOutputHelper 已从 Xunit.Abstractions 移入 Xunit 命名空间

namespace MainAPP.UIAutomation;

/// <summary>
/// 诊断测试：导出 UIA 树到测试输出，用于了解 WPF 控件在 UIA 中的实际暴露形式。
/// 不是常规断言测试，仅用于诊断控件查找问题。
/// </summary>
[Collection("UIA")]
public class TreeDumpTests : IDisposable
{
    private readonly KanbanAppFixture _fixture;
    private readonly ITestOutputHelper _output;
    public TreeDumpTests(ITestOutputHelper output) { _fixture = new(); _output = output; }
    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void DumpMainWindowTree()
    {
        var window = _fixture.MainWindow;
        var sb = new StringBuilder();
        sb.AppendLine($"Window: Name='{window.Name}', Title='{window.Title}'");
        DumpDescendants(window, sb, 0, maxDepth: 9);
        var dump = sb.ToString();
        _output.WriteLine(dump);
        // 写到临时文件以便保留
        var path = Path.Combine(_fixture.TempDir, "ui_tree.txt");
        File.WriteAllText(path, dump);
        // 弱断言：窗口标题应包含"看板"
        Assert.Contains("看板", dump);
    }

    /// <summary>
    /// 通过 RawViewWalker 遍历原始 UIA 树，验证 hc:SideMenu 项是否在原始树中可见。
    /// 若 Raw 树中也无法找到"主页/产线/设备/历史查询/设置"等导航项文本，
    /// 则证明 hc:SideMenu 控件模板根本不暴露子元素到 UIA。
    /// </summary>
    [Fact]
    public void RawTree_SearchForSideMenuItems()
    {
        var window = _fixture.MainWindow;
        var walker = _fixture.Automation.TreeWalkerFactory.GetRawViewWalker();
        var found = new List<string>();
        ScanRawTree(window, walker, found, depth: 0, maxDepth: 12);

        var dump = string.Join("\n", found);
        _output.WriteLine($"Raw tree nav-related elements:\n{dump}");

        // 期望至少能找到部分导航相关文本（来自 ToolTip 或 TextBlock）
        // 若全部为空，证明 SideMenu 完全不可访问
        var navKeywords = new[] { "主页", "产线", "设备", "历史查询", "设置", "看板系统" };
        var matched = navKeywords.Where(k => found.Any(f => f.Contains(k))).ToList();
        _output.WriteLine($"Matched nav keywords: {string.Join(", ", matched)}");

        // 至少应找到"看板系统"（窗口标题）和"主页"（侧边栏第一项 + 主页内容）
        Assert.Contains("看板系统", matched);
    }

    private static void ScanRawTree(AutomationElement parent, ITreeWalker walker,
        List<string> found, int depth, int maxDepth)
    {
        if (depth >= maxDepth) return;
        var child = walker.GetFirstChild(parent);
        while (child != null)
        {
            if (!string.IsNullOrEmpty(child.Name))
                found.Add($"[d{depth}] [{child.ControlType}] '{child.Name}'");
            ScanRawTree(child, walker, found, depth + 1, maxDepth);
            child = walker.GetNextSibling(child);
        }
    }

    private static void DumpDescendants(AutomationElement parent, StringBuilder sb, int depth, int maxDepth)
    {
        if (depth >= maxDepth) return;
        var pad = new string(' ', depth * 2);
        foreach (var child in parent.FindAllChildren())
        {
            var name = string.IsNullOrEmpty(child.Name) ? "(empty)" : child.Name;
            string autoId;
            try { autoId = child.AutomationId; } catch { autoId = "(unsupported)"; }
            sb.AppendLine($"{pad}- [{child.ControlType}] Name='{name}' AutoId='{autoId}'");
            DumpDescendants(child, sb, depth + 1, maxDepth);
        }
    }
}
