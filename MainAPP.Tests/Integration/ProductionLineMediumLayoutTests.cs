using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MainAPP.Data;
using MainAPP.Models;
using MainAPP.Services;
using MainAPP.Tests.Unit;
using MainAPP.ViewModels;
using MainAPP.Views;
using Xunit;
// v3: ITestOutputHelper 已从 Xunit.Abstractions 移入 Xunit 命名空间

namespace MainAPP.Tests.Integration;

[Collection("WpfUi")]
[Trait("Category","Integration")]
[Trait("Speed","Slow")]
[Trait("Requires","STA")]
public class ProductionLineMediumLayoutTests : WpfTestHost
{
    private readonly ITestOutputHelper _output;

    public ProductionLineMediumLayoutTests(WpfStaFixture fixture, ITestOutputHelper output) : base(fixture)
    {
        _output = output;
    }

    [Fact]
    public void MediumLayout_10Devices_CardsRenderWithFullHeight()
    {
        var appSettings = new AppSettings();
        var repo = new DeviceRepository(appSettings);
        for (int i = 0; i < 10; i++)
        {
            var d = new Device { Name = $"设备{i + 1}", RecipeName = $"配方-{i + 1}" };
            repo.Devices.Add(d);
            repo.Runtimes.Add(new DeviceRuntime(d));
        }
        var conn = new PlcConnectionManager(new FakePlcDriver(), appSettings);
        var selection = new DeviceSelectionService();
        var homeVm = new HomeViewModel(repo, conn, appSettings, null!, selection);
        var lineVm = new ProductionLineViewModel(repo, selection);

        var allCardHeights = new List<double>();
        var cardsWithoutName = new List<int>();
        int deviceCardCount = 0;
        string allBordersInfo = "";

        RunOnSta(app =>
        {
            // 用 ContentControl 包一层（模拟 MainWindow 真实容器）
            var view = new ProductionLineView { DataContext = lineVm };
            var contentHost = new ContentControl { Content = view };
            var win = new Window { Content = contentHost, Width = 1280, Height = 800 };
            win.Show();
            win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            win.UpdateLayout();
            win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            win.UpdateLayout();

            // 精确找设备卡：只找引用 DeviceCard Style 的 Border（KPI 卡用的是 SummaryKpiBox Style）
            var allBorders = FindVisualDescendants<Border>(view).ToList();
            var diag = new StringBuilder();
            diag.AppendLine($"=== 全部 Border 数量: {allBorders.Count} ===");
            foreach (var b in allBorders)
            {
                var styleKey = b.Style?.TargetType?.Name + (b.Style?.BasedOn != null ? $"->{b.Style.BasedOn.TargetType?.Name}" : "");
                diag.AppendLine($"  Border Style={styleKey} W={b.ActualWidth:F0} H={b.ActualHeight:F0}");
            }

            // 精确找中卡片：4 列布局下卡片宽度约 286，最小高度 200。
            // 必须排除 Button 模板里的 FocusBorder（无 Style），它和 inner Border（DeviceCardBorder Style）尺寸都落入过滤区间。
            // 仅保留 Style 非空的 Border，即真正承载卡片内容的 inner Border。
            var cards = allBorders
                .Where(b => b.Style != null
                    && b.ActualWidth >= 200 && b.ActualWidth <= 300
                    && b.ActualHeight >= 200 && b.ActualHeight <= 260)
                .ToList();
            deviceCardCount = cards.Count;
            foreach (var card in cards) allCardHeights.Add(card.ActualHeight);

            diag.AppendLine($"中卡片数量: {cards.Count}（期望 10），高度列表: [{string.Join(", ", allCardHeights.Select(h => h.ToString("F0")))}]");
            allBordersInfo = diag.ToString();

            Console.WriteLine(allBordersInfo);

            win.Close();
        });

        _output.WriteLine(allBordersInfo);

        // 总是把诊断数据塞到 Assert 消息里，无论 Pass/Fail 都能看到
            Assert.True(deviceCardCount == 10 && allCardHeights.All(h => h >= 200 && h <= 260),
            $"诊断: {allBordersInfo}");
    }

    [Fact]
    public void MediumLayout_10Devices_ShowsPerformanceParams()
    {
        var appSettings = new AppSettings();
        var repo = new DeviceRepository(appSettings);
        for (int i = 0; i < 10; i++)
        {
            var d = new Device { Name = $"设备{i + 1}", RecipeName = $"配方-{i + 1}" };
            repo.Devices.Add(d);
            repo.Runtimes.Add(new DeviceRuntime(d));
        }
        var conn = new PlcConnectionManager(new FakePlcDriver(), appSettings);
        var selection = new DeviceSelectionService();
        var homeVm = new HomeViewModel(repo, conn, appSettings, null!, selection);
        var lineVm = new ProductionLineViewModel(repo, selection);

        int perf = 0, avail = 0, cycle = 0, downtime = 0;
        int recipeShown = 0;
        RunOnSta(app =>
        {
            var view = new ProductionLineView { DataContext = lineVm };
            var win = new Window { Content = new ContentControl { Content = view }, Width = 1280, Height = 800 };
            win.Show();
            win.UpdateLayout();
            win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            win.UpdateLayout();

            var texts = FindVisualDescendants<TextBlock>(view)
                .Where(t => t.IsVisible)
                .Select(t => t.Text).Where(t => !string.IsNullOrEmpty(t)).ToList();
            perf = texts.Count(t => t == "性能效率");
            avail = texts.Count(t => t == "时间稼动率");
            cycle = texts.Count(t => t == "目标 / 实际");
            downtime = texts.Count(t => t == "综合停机");
            // 卡片不应再显示配方信息（表格布局已 Collapsed，被 IsVisible 过滤，不影响）
            recipeShown = texts.Count(t => t.Contains("配方"));
            win.Close();
        });

        Assert.True(perf == 10 && avail == 10 && cycle == 10 && downtime == 10,
            $"中卡片性能参数标签: 性能效率={perf} A时间稼动率={avail} 目标/实际={cycle} 综合停机={downtime}（期望各 10）");
        Assert.True(recipeShown == 0,
            $"中卡片不应显示配方信息，但检测到 {recipeShown} 处含『配方』文本");
    }

    [Fact]
    public void LargeLayout_8Devices_ShowsPerformanceParams()
    {
        var appSettings = new AppSettings();
        var repo = new DeviceRepository(appSettings);
        for (int i = 0; i < 8; i++)
        {
            var d = new Device { Name = $"设备{i + 1}", RecipeName = $"配方-{i + 1}" };
            repo.Devices.Add(d);
            repo.Runtimes.Add(new DeviceRuntime(d));
        }
        var conn = new PlcConnectionManager(new FakePlcDriver(), appSettings);
        var selection = new DeviceSelectionService();
        var homeVm = new HomeViewModel(repo, conn, appSettings, null!, selection);
        var lineVm = new ProductionLineViewModel(repo, selection);

        int perf = 0, avail = 0, cycle = 0, downtime = 0, oee = 0, quality = 0;
        int recipeShown = 0;
        var cardHeights = new List<double>();
        RunOnSta(app =>
        {
            var view = new ProductionLineView { DataContext = lineVm };
            var win = new Window { Content = new ContentControl { Content = view }, Width = 1280, Height = 800 };
            win.Show();
            win.UpdateLayout();
            win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            win.UpdateLayout();

            var texts = FindVisualDescendants<TextBlock>(view)
                .Where(t => t.IsVisible)
                .Select(t => t.Text).Where(t => !string.IsNullOrEmpty(t)).ToList();
            perf = texts.Count(t => t == "性能效率");
            avail = texts.Count(t => t == "时间稼动率");
            cycle = texts.Count(t => t == "目标 / 实际");
            downtime = texts.Count(t => t == "综合停机");
            oee = texts.Count(t => t == "OEE");
            quality = texts.Count(t => t == "良品率");
            // 卡片不应再显示配方信息（表格布局已 Collapsed，被 IsVisible 过滤，不影响）
            recipeShown = texts.Count(t => t.Contains("配方"));
            // 仅统计卡片按钮：排除工具栏的段选按钮（H≈32）和 hc:SearchBar 内部清除按钮（H≈34）。
            // 卡片按钮 MinHeight≥140，工具栏按钮均 < 35。
            cardHeights = FindVisualDescendants<Button>(view)
                .Where(b => b.IsVisible && b.ActualHeight >= 140)
                .Select(b => b.ActualHeight).ToList();
            win.Close();
        });

        Assert.True(perf == 8 && avail == 8 && cycle == 8 && downtime == 8 && oee == 8 && quality == 8,
            $"大卡片指标标签: 性能效率={perf} A时间稼动率={avail} 目标/实际={cycle} 综合停机={downtime} OEE={oee} C良品率={quality}（期望各 8）");
        Assert.True(recipeShown == 0,
            $"大卡片不应显示配方信息，但检测到 {recipeShown} 处含『配方』文本");
        Assert.True(cardHeights.Count == 8 && cardHeights.All(h => h >= 140 && h <= 240),
            $"大卡片高度应变矮: 数量={cardHeights.Count} 高度=[{string.Join(", ", cardHeights.Select(h => h.ToString("F0")))}]（期望 8 张、140-240）");
    }
}
