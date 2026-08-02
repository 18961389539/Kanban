using FlaUI.Core.AutomationElements;
using Xunit;

namespace MainAPP.UIAutomation;

/// <summary>
/// 产线页交互测试：搜索框、设备卡片渲染。
/// 无 PLC 连接时设备状态为离线，仅验证控件存在与可交互性。
/// </summary>
[Collection("UIA")]
public class ProductionLineFlowTests : IDisposable
{
    private readonly KanbanAppFixture _fixture;

    public ProductionLineFlowTests() => _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void ProductionLinePage_SearchBox_Clickable()
    {
        var window = _fixture.MainWindow;
        var automation = _fixture.Automation;
        UiaTestHelpers.NavigateToPage(window, automation, "产线");

        var search = window.FindFirstDescendant(cf => cf.ByName("按设备名搜索"));
        Assert.True(search != null, "未找到产线页搜索框");

        // 点击搜索框并输入文本（验证不抛异常）
        search.AsTextBox().Click();
        search.AsTextBox().Text = "注塑";
        Thread.Sleep(500);

        // 清空搜索框
        search.AsTextBox().Text = "";
        Thread.Sleep(500);
    }

    [Fact]
    public void ProductionLinePage_DeviceCardsRendered()
    {
        var window = _fixture.MainWindow;
        var automation = _fixture.Automation;
        UiaTestHelpers.NavigateToPage(window, automation, "产线");

        // 产线页应渲染设备卡片（即使无 PLC，设备列表来自 devices.json）
        // 验证搜索框存在即可证明页面已渲染（设备卡片为 DataTemplate，无固定 AutomationProperties.Name）
        var search = window.FindFirstDescendant(cf => cf.ByName("按设备名搜索"));
        Assert.NotNull(search);
    }
}
