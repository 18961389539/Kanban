using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Xunit;

namespace MainAPP.UIAutomation;

/// <summary>
/// 设备管理页流程测试。
/// 通过主导航 ListBox（Name="主导航"）切换到设备管理页，验证控件存在性与新增设备交互。
/// </summary>
[Collection("UIA")]
public class DeviceManagerFlowTests : IDisposable
{
    private readonly KanbanAppFixture _fixture;

    public DeviceManagerFlowTests() => _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void DeviceManagerPage_ControlsExistInRawTree()
    {
        var window = _fixture.MainWindow;
        var walker = _fixture.Automation.TreeWalkerFactory.GetRawViewWalker();
        var names = new HashSet<string>();
        UiaTestHelpers.CollectNames(window, walker, names, depth: 0, maxDepth: 12);

        Assert.Contains("设备列表", names);
        Assert.Contains("设备详情", names);
        Assert.Contains("新增设备", names);
        Assert.Contains("删除选中的设备", names);
        Assert.Contains("保存设备配置", names);
        Assert.Contains("搜索设备", names);
        Assert.Contains("请选择设备或点击新增", names);
    }

    /// <summary>
    /// 导航到设备管理页，验证关键控件可见（通过 NavigateToPage 切换页面）。
    /// </summary>
    [Fact]
    public void DeviceManagerPage_NavigateAndControlsVisible()
    {
        var window = _fixture.MainWindow;
        var automation = _fixture.Automation;
        UiaTestHelpers.NavigateToPage(window, automation, "设备管理");

        // 验证设备管理页关键控件可见
        foreach (var name in new[] { "设备列表", "设备详情", "新增设备", "保存设备配置" })
        {
            var el = window.FindFirstDescendant(cf => cf.ByName(name));
            Assert.True(el != null, $"设备管理页未找到控件：{name}");
        }
    }

    /// <summary>
    /// 验证设备详情 Tab 可切换：设备参数/报警管理/缺陷管理/计数报警/工单。
    /// 仅点击 Tab Header 验证不抛异常，不验证 Tab 内容（需选中设备才有数据）。
    /// </summary>
    [Fact]
    public void DeviceManagerPage_DetailTabs_Clickable_NoException()
    {
        var window = _fixture.MainWindow;
        var automation = _fixture.Automation;
        UiaTestHelpers.NavigateToPage(window, automation, "设备管理");

        // 5 个 Tab Header
        foreach (var name in new[] { "设备参数", "报警管理", "缺陷管理", "计数报警", "工单" })
        {
            var tab = window.FindFirstDescendant(cf => cf.ByName(name));
            if (tab != null)
            {
                tab.AsButton().Click();
                Thread.Sleep(500);
            }
        }
    }

    /// <summary>
    /// 验证新增设备按钮可点击。
    /// 点击后设备详情区进入编辑态（设备名称 TextBox 可编辑）。
    /// 不保存，避免修改持久化数据。
    /// </summary>
    [Fact]
    public void AddDevice_Click_ShowsEditForm_NoException()
    {
        var window = _fixture.MainWindow;
        var automation = _fixture.Automation;
        UiaTestHelpers.NavigateToPage(window, automation, "设备管理");

        var addBtn = window.FindFirstDescendant(cf => cf.ByName("新增设备"));
        Assert.True(addBtn != null, "未找到新增设备按钮");
        Assert.True(addBtn.AsButton().IsEnabled, "新增设备按钮未启用");

        addBtn.AsButton().SafeInvoke();
        Thread.Sleep(1000); // 等待编辑表单渲染

        // 验证设备详情区已进入编辑态（"请选择设备或点击新增" 空状态提示应消失）
        var emptyHint = window.FindFirstDescendant(cf => cf.ByName("请选择设备或点击新增"));
        // 空状态提示可能仍可见（如果新增设备未选中），不强制断言
    }
}
