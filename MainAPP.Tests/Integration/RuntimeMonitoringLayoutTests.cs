using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Resources;
using MainAPP.Services;
using MainAPP.Tests.Unit;
using MainAPP.ViewModels;
using MainAPP.Views;
using Xunit;

namespace MainAPP.Tests.Integration;

/// <summary>
/// 运行监控页布局与诊断导出的可执行验证。
/// 纯布局改动无法靠肉眼在 CI 里确认，这里把"列数随宽度降级""列表不再产生内层滚动条"
/// "诊断快照包含全部字段"固化成断言，避免后续改 XAML 时静默回退。
/// </summary>
[Collection("WpfUi")]
[Trait("Category", "Integration")]
[Trait("Speed", "Slow")]
[Trait("Requires", "STA")]
public sealed class RuntimeMonitoringLayoutTests : WpfTestHost, IDisposable
{
    private readonly List<string> _tempDirs = new();

    public RuntimeMonitoringLayoutTests(WpfStaFixture fixture) : base(fixture) { }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// KPI 行按可用宽度降级列数：写死 Columns=4 时窄窗口会把卡片挤成细条。
    /// 阈值：内容宽 ≥1000 排 4 列，≥630 排 2 列，更窄单列。
    /// </summary>
    [Theory]
    [InlineData(1400, 4)]
    [InlineData(900, 2)]
    [InlineData(520, 1)]
    public void KpiRow_Columns_DegradeWithAvailableWidth(int windowWidth, int expectedColumns)
    {
        RunOnSta(_ =>
        {
            var (view, _) = BuildView();
            Layout(view, windowWidth);

            var kpiRow = FindByName<UniformGrid>(view, "KpiRow");
            Assert.NotNull(kpiRow);
            Assert.Equal(expectedColumns, kpiRow!.Columns);
        });
    }

    /// <summary>
    /// 设备状态列表不再出现"外层 ScrollViewer + 内层 ListView 滚动条"的双层滚动：
    /// 少量设备时内容应完整展开，内层 ScrollViewer 无可滚动高度。
    /// </summary>
    [Fact]
    public void DeviceStatusList_RendersWithoutNestedScroll()
    {
        RunOnSta(_ =>
        {
            var (view, vm) = BuildView();
            for (var i = 0; i < 6; i++)
            {
                vm.DeviceStatuses.Add(new DeviceAcquisitionStatusItem
                {
                    DeviceId = $"dev-{i}",
                    DeviceName = $"注塑机 A{i + 1}",
                    StatusText = "运行",
                    AcquisitionText = "已采集",
                    ConfiguredAddressCount = 12,
                    OkProduction = 100 + i,
                    NgProduction = i,
                });
            }
            Layout(view, 1400);

            var list = FindByName<ListView>(view, "DeviceStatusList");
            Assert.NotNull(list);

            // 整行铺满（Grid.ColumnSpan=3）：半栏宽度下 5 列会挤到横向滚动
            Assert.True(list!.ActualWidth > view.ActualWidth * 0.8,
                $"设备列表应占满整行，实际宽度 {list.ActualWidth:F0} / 视图 {view.ActualWidth:F0}");

            var inner = FindDescendant<ScrollViewer>(list!);
            Assert.NotNull(inner);
            Assert.Equal(0d, inner!.ScrollableHeight);

            // MaxHeight 是设备极多时的兜底（同时保住虚拟化），必须保留
            Assert.True(list.MaxHeight > 0);
        });
    }

    /// <summary>诊断快照覆盖全部指标分区与设备明细，供"复制/导出"两个出口共用。</summary>
    [Fact]
    public void BuildDiagnosticsReport_CoversAllSectionsAndDeviceRows()
    {
        RunOnSta(_ =>
        {
            var (_, vm) = BuildView();
            vm.CompletedCycles = 42;
            vm.CycleP95Milliseconds = 88;
            vm.CycleP99Milliseconds = 132;
            vm.LastFailureMessage = "PLC 读取超时";
            vm.RefreshErrorMessage = "配置校验抛异常";
            vm.DeviceStatuses.Add(new DeviceAcquisitionStatusItem
            {
                DeviceId = "dev-1",
                DeviceName = "注塑机 A1",
                StatusText = "运行",
                AcquisitionText = "已采集",
                ConfiguredAddressCount = 12,
                OkProduction = 1200,
                NgProduction = 12,
            });

            var report = vm.BuildDiagnosticsReport();

            Assert.Contains(Strings.Rtmon_DiagnosticsTitle, report);
            Assert.Contains($"[{Strings.Rtmon_DiagOverview}]", report);
            Assert.Contains($"[{Strings.K427}]", report);
            Assert.Contains($"[{Strings.K434}]", report);
            Assert.Contains($"[{Strings.K440}]", report);
            Assert.Contains($"[{Strings.K444}]", report);
            Assert.Contains($"[{Strings.K450}]", report);
            Assert.Contains($"[{Strings.K456}]", report);
            Assert.Contains($"[{Strings.K467}]", report);

            // 数值与异常必须落到文本里，否则导出的快照没有排查价值
            Assert.Contains("42", report);
            Assert.Contains("88 ms", report);
            Assert.Contains("132 ms", report);
            Assert.Contains("PLC 读取超时", report);
            Assert.Contains(Strings.Rtmon_RefreshFailed, report);
            Assert.Contains("注塑机 A1", report);
            // 设备明细为制表符分隔，便于直接粘进表格
            Assert.Contains($"{Strings.K002}\t{Strings.K083}", report);
        });
    }

    /// <summary>设备为空、时间字段为 null 时快照仍可生成（导出入口不能因为空状态崩掉）。</summary>
    [Fact]
    public void BuildDiagnosticsReport_HandlesEmptyState()
    {
        RunOnSta(_ =>
        {
            var (_, vm) = BuildView();
            var report = vm.BuildDiagnosticsReport();

            Assert.NotNull(report);
            Assert.Contains(Strings.K595, report);   // 最近成功时间 = 暂无
            Assert.Contains(Strings.K583, report);   // 设备状态 = 暂无数据
            Assert.DoesNotContain("\t运行\t", report);
        });
    }

    // ── 装配 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 构造视图与 VM。诊断快照与布局断言都不会触碰 PLC / 历史库 / 资源采样，
    /// 这些依赖传 null! 即可，避免为纯 UI 测试拉起真实的 SQLite 与 GPU 采样线程。
    /// </summary>
    private (RuntimeMonitoringView View, RuntimeMonitoringViewModel Vm) BuildView()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "KanbanRtmonTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        _tempDirs.Add(tempDir);

        var appSettings = new AppSettings { ConfigDirectory = tempDir };
        var repo = new DeviceRepository(appSettings);
        var vm = new RuntimeMonitoringViewModel(
            connectionManager: null!,
            acquisitionService: null!,
            appSettings: appSettings,
            deviceRepository: repo,
            historyService: null!,
            systemResourceMonitor: null!,
            dialogService: new FakeDialogService());

        var view = new RuntimeMonitoringView { DataContext = vm };
        return (view, vm);
    }

    /// <summary>按给定宽度完成一次完整布局（跑两遍，让依赖 ActualWidth 的触发器收敛）。</summary>
    private static void Layout(FrameworkElement view, double width)
    {
        for (var pass = 0; pass < 2; pass++)
        {
            view.Measure(new Size(width, 6000));
            view.Arrange(new Rect(0, 0, width, 6000));
            view.UpdateLayout();
        }
    }

    private static T? FindByName<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is FrameworkElement { } fe && fe.Name == name && root is T typed) return typed;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindByName<T>(VisualTreeHelper.GetChild(root, i), name);
            if (found != null) return found;
        }
        return null;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T typed) return typed;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindDescendant<T>(VisualTreeHelper.GetChild(root, i));
            if (found != null) return found;
        }
        return null;
    }
}
