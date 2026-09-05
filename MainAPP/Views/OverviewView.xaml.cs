using System;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MainAPP.ViewModels;
using OxyPlot;
using OxyPlot.Wpf;

namespace MainAPP.Views;

/// <summary>
/// OverviewView.xaml 的交互逻辑。
/// 概览页：最近 N 小时生产汇总（KPI + 趋势图 + Top 报警 + 设备明细表格）。
/// 生命周期（审查修复 2026-08-30）：图表定时器与订阅改由 ViewModel 的
/// INavigationPageLifecycle 驱动（Entered/Exited 事件）。此前依赖 Loaded/Unloaded，
/// 但 NavigationPageHost 常驻导致 Unloaded 永不触发 → 定时器切走后持续运行、逐页叠加，
/// 是页面切换越来越卡的主因之一。
/// </summary>
public partial class OverviewView : UserControl
{
    // 图表重绘节流：高频数据更新（产线 tick 一次抛 15+ 条 PropertyChanged）若每次都
    // InvalidatePlot 全量重绘 4 张图，是概览页最大渲染开销。改为最多 2Hz 重绘——
    // 属性变化时只标脏，DispatcherTimer 到点合并成一次重绘。
    private static readonly TimeSpan ChartRefreshInterval = TimeSpan.FromMilliseconds(500);
    private readonly DispatcherTimer _chartRefreshTimer;
    private bool _trendDirty;
    private bool _oeeDirty;
    private bool _heatmapDirty;
    private bool _paretoDirty;

    public OverviewView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;

        _chartRefreshTimer = new DispatcherTimer(ChartRefreshInterval, DispatcherPriority.Background, OnChartRefreshTick, Dispatcher);
        _chartRefreshTimer.IsEnabled = false;
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is OverviewViewModel oldVm)
        {
            oldVm.PropertyChanged -= OnViewModelPropertyChanged;
            oldVm.Entered -= OnVmEntered;
            oldVm.Exited -= OnVmExited;
            // 审查修复 2026-09-05（P2）：旧 VM 解绑时一并停图表定时器并清脏标记，与 OnVmExited
            // 对齐。原实现仅在 OnVmExited 里 Stop()；若解绑时旧 VM 仍处于激活态（进入页面后直接
            // 换页/换 VM 而未走 Exited），定时器保持 IsEnabled 且脏标记残留，多跑一拍空转 tick
            // （每次 Stop 自己 + 判 DataContext 类型后 return）。
            _chartRefreshTimer.Stop();
            _trendDirty = _oeeDirty = _heatmapDirty = _paretoDirty = false;
        }
        if (DataContext is OverviewViewModel vm)
        {
            vm.PropertyChanged += OnViewModelPropertyChanged;
            vm.Entered += OnVmEntered;
            vm.Exited += OnVmExited;
            // 视图创建可能晚于 VM 首次 OnPageEnter（懒加载）：若 VM 已激活则补一次进入逻辑
            if (vm.IsPageActive)
                OnVmEntered(vm, EventArgs.Empty);
        }
    }

    private void OnVmEntered(object? sender, EventArgs e)
    {
        if (DataContext is not OverviewViewModel vm) return;
        // 复盘页进入时先完成导航绘制，再在后台优先级触发历史查询与图表重绘。
        Dispatcher.BeginInvoke(() =>
        {
            if (!vm.IsPageActive) return;
            vm.RefreshCommand.Execute(null);
            ForceChartsRefresh(vm);
        }, DispatcherPriority.Background);
    }

    private void OnVmExited(object? sender, EventArgs e)
    {
        _chartRefreshTimer.Stop();
        _trendDirty = _oeeDirty = _heatmapDirty = _paretoDirty = false;
    }

    /// <summary>无条件强制重绘四个图表（页面重载后 OxyPlot 渲染可能停止，即使 Model 未变）。</summary>
    private void ForceChartsRefresh(OverviewViewModel vm)
    {
        RefreshChart(TrendPlotView, vm.TrendChart);
        RefreshChart(OeeWaterfallPlotView, vm.OeeWaterfallChart);
        RefreshChart(HeatmapPlotView, vm.ProductionHeatmapChart);
        RefreshChart(DefectParetoPlotView, vm.DefectParetoChart);
    }

    /// <summary>
    /// OxyPlot 2.2.0 页面导航重载后 PlotView 渲染可能停止（Model 更新不再重绘）：
    /// 在图表属性变化时强制重设 Model + InvalidatePlot，确保瀑布图/趋势图/热力图始终渲染。
    /// 节流版：仅标脏并启动定时器，实际重绘合并到 2Hz 的 tick，避免每次数据变化全量重绘。
    /// 仅页面激活期间生效（IsPageActive 守卫）：切走后属性变化不启动定时器，避免泄漏。
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (DataContext is not OverviewViewModel vm || !vm.IsPageActive) return;
        switch (e.PropertyName)
        {
            case nameof(OverviewViewModel.TrendChart):
                _trendDirty = true;
                break;
            case nameof(OverviewViewModel.OeeWaterfallChart):
                _oeeDirty = true;
                break;
            case nameof(OverviewViewModel.ProductionHeatmapChart):
                _heatmapDirty = true;
                break;
            case nameof(OverviewViewModel.DefectParetoChart):
                _paretoDirty = true;
                break;
            default:
                return;
        }
        if (!_chartRefreshTimer.IsEnabled)
            _chartRefreshTimer.Start();
    }

    private void OnChartRefreshTick(object? sender, EventArgs e)
    {
        _chartRefreshTimer.Stop();
        if (DataContext is not OverviewViewModel vm) return;

        if (_trendDirty) RefreshChart(TrendPlotView, vm.TrendChart);
        if (_oeeDirty) RefreshChart(OeeWaterfallPlotView, vm.OeeWaterfallChart);
        if (_heatmapDirty) RefreshChart(HeatmapPlotView, vm.ProductionHeatmapChart);
        if (_paretoDirty) RefreshChart(DefectParetoPlotView, vm.DefectParetoChart);

        _trendDirty = _oeeDirty = _heatmapDirty = _paretoDirty = false;
    }

    private static void RefreshChart(PlotView view, PlotModel? model)
    {
        if (view == null) return;
        view.Model = null;
        view.Model = model;
        view.InvalidatePlot(true);
    }

    /// <summary>快捷时间 chip 点击：Tag 携带 OverviewTimeRange 枚举名。</summary>
    private void OnTimeRangeChipClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is System.Windows.FrameworkElement fe
            && fe.Tag is string tag
            && System.Enum.TryParse<OverviewTimeRange>(tag, out var range)
            && DataContext is OverviewViewModel vm)
        {
            vm.SelectedTimeRange = range;
        }
    }
}
