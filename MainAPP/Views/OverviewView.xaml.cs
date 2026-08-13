using System.ComponentModel;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Input;
using MainAPP.ViewModels;
using OxyPlot;
using OxyPlot.Wpf;

namespace MainAPP.Views;

/// <summary>
/// OverviewView.xaml 的交互逻辑。
/// 概览页：最近 N 小时生产汇总（KPI + 趋势图 + Top 报警 + 设备明细表格）。
/// </summary>
public partial class OverviewView : UserControl
{
    public OverviewView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is OverviewViewModel oldVm)
            oldVm.PropertyChanged -= OnViewModelPropertyChanged;
        if (DataContext is OverviewViewModel vm)
        {
            vm.PropertyChanged += OnViewModelPropertyChanged;
            ForceChartsRefresh(vm);
        }
    }

    private void OnUnloaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is OverviewViewModel vm)
            vm.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is OverviewViewModel vm)
        {
            vm.RefreshCommand.Execute(null);
            ForceChartsRefresh(vm);
        }
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
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (DataContext is not OverviewViewModel vm) return;
        switch (e.PropertyName)
        {
            case nameof(OverviewViewModel.TrendChart):
                RefreshChart(TrendPlotView, vm.TrendChart);
                break;
            case nameof(OverviewViewModel.OeeWaterfallChart):
                RefreshChart(OeeWaterfallPlotView, vm.OeeWaterfallChart);
                break;
            case nameof(OverviewViewModel.ProductionHeatmapChart):
                RefreshChart(HeatmapPlotView, vm.ProductionHeatmapChart);
                break;
            case nameof(OverviewViewModel.DefectParetoChart):
                RefreshChart(DefectParetoPlotView, vm.DefectParetoChart);
                break;
        }
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
