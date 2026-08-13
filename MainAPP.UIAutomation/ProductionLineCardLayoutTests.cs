using System.Diagnostics;
using System.IO;
using System.Text;
using FlaUI.Core.AutomationElements;
using Xunit;

namespace MainAPP.UIAutomation;

/// <summary>
/// 产线概览页设备卡片布局回归测试（B 方案：左右分栏 + OEE 三率条）。
/// 预置带 3 台设备配置的验证目录（复制自真实 Config 的 json），导航到产线页断言：
/// 1. 每台设备卡片渲染（Border AutomationProperties.Name=设备名）；
/// 2. OEE 拆解区三率条标签（可用率/性能率/质量率）与辅助指标（停机）渲染；
/// 3. 截图保存到 screenshots/。
/// 注：UIA 树更新异步于 UI 线程（Content 设置后需布局/渲染通道），断言用轮询等待。
/// </summary>
[Collection("UIA")]
public class ProductionLineCardLayoutTests : IDisposable
{
    private readonly KanbanAppFixture _fixture;
    private readonly string _saveDir;

    public ProductionLineCardLayoutTests()
    {
        // 固定验证目录：运行前需准备 KanbanUIA_CardVerify/Config/{devices,settings,baselines}.json
        var dataDir = Path.Combine(Path.GetTempPath(), "KanbanUIA_CardVerify");
        _fixture = new KanbanAppFixture(dataDir);
        _saveDir = Path.Combine(GetSolutionRoot(), "screenshots");
        Directory.CreateDirectory(_saveDir);
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void DeviceCards_WithRateBars_Rendered()
    {
        var window = _fixture.MainWindow;
        var automation = _fixture.Automation;
        UiaTestHelpers.NavigateToPage(window, automation, "产线");

        // 三台设备卡片（设备名来自 devices.json，与仿真环境一致；UIA 树异步更新，轮询等待）
        foreach (var name in new[] { "注塑机1", "注塑机2", "组装机1" })
        {
            var card = WaitUntil(() => window.FindFirstDescendant(cf => cf.ByName(name)) != null,
                timeoutMs: 15000);
            if (!card)
            {
                // 失败诊断：dump 产线页 UIA 树到临时目录
                var sb = new StringBuilder();
                DumpDescendants(window, sb, 0, 10);
                var diagPath = Path.Combine(_fixture.TempDir, "linepage_tree.txt");
                File.WriteAllText(diagPath, sb.ToString());
                Console.WriteLine($"  [DIAG] 产线页 UIA 树已导出: {diagPath}");
                Assert.Fail($"未找到设备卡片: {name}");
            }
        }

        // OEE 拆解区：三率条 + 停机指标（B 方案新增内容）
        foreach (var text in new[] { "可用率", "性能率", "质量率", "停机" })
        {
            var found = WaitUntil(() => UiaTestHelpers.ContainsTextRecursive(window, text),
                timeoutMs: 10000);
            Assert.True(found, $"缺少文本: {text}");
        }

        // 截图留档
        var path = Path.Combine(_saveDir, "02_production_line_card_b.png");
        using var bmp = window.Capture();
        bmp.Save(path);
        Console.WriteLine($"  [OK] 已保存截图: {path}");
    }

    private static bool WaitUntil(Func<bool> condition, int timeoutMs, int intervalMs = 500)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Thread.Sleep(intervalMs);
        }
        return condition();
    }

    private static string GetSolutionRoot()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Kanban.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new FileNotFoundException("未找到解决方案根目录");
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
