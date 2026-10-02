using System.Windows;
using System.Windows.Controls;
using HandyControl.Data;
using MainAPP.ViewModels;

namespace MainAPP.Views;

/// <summary>
/// HistoryQueryView.xaml 的交互逻辑
/// </summary>
public partial class HistoryQueryView : UserControl
{
    public HistoryQueryView()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => RefreshRepeatedQuestions();

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
            RefreshRepeatedQuestions();
    }

    private void RefreshRepeatedQuestions()
    {
        if (DataContext is HistoryQueryViewModel vm)
            vm.RefreshRepeatedQuestions();
    }

    /// <summary>
    /// 产量/状态/报警三个分页控件的统一 PageUpdated 处理函数。
    /// 只切换已缓存结果的当前页，不重新查询。
    /// </summary>
    private void Pagination_PageChanged(object sender, FunctionEventArgs<int> e)
    {
        if (DataContext is HistoryQueryViewModel vm)
            vm.GoToPage(e.Info);
    }
}
