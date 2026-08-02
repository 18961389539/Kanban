using System.Windows.Controls;
using MainAPP.ViewModels;

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
    }

    private void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is OverviewViewModel vm)
            vm.RefreshCommand.Execute(null);
    }
}
