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
    /// 三个 Tab 的分页行为完全相同：同步 CurrentPage 后触发 QueryCurrentTab。
    /// </summary>
    private void Pagination_PageChanged(object sender, FunctionEventArgs<int> e)
    {
        if (DataContext is HistoryQueryViewModel vm)
        {
            vm.CurrentPage = e.Info;
            vm.QueryCurrentTabCommand.Execute(null);
        }
    }
}
