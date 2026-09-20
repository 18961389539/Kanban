using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MainAPP.Views;

/// <summary>
/// 产线总览页：单一详细设备卡片模板（<see cref="ViewModels.ProductionLineViewModel.IsDetailedLayout"/> 恒 true，
/// 不按设备数切换大卡/中卡/表格）、顶部目标产能批量设置、虚拟化自适应网格、汇总 KPI 条，以及筛选 / 搜索 / 排序工具栏。
/// </summary>
public partial class ProductionLineView : UserControl
{
    public ProductionLineView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Ctrl+F：焦点切到设备搜索框（hc:SearchBar 内部含 TextBox）。
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            if (FindVisualChild<TextBox>(LineSearchBar) is { } searchBox)
            {
                searchBox.Focus();
                Keyboard.Focus(searchBox);
                e.Handled = true;
            }
        }
    }

    /// <summary>
    /// 进入页面（切换为当前页时 Visibility 变 Visible）自动聚焦设备搜索框，
    /// 免点击直接输入关键字过滤设备。
    /// </summary>
    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (IsVisible && FindVisualChild<TextBox>(LineSearchBar) is { } searchBox)
                {
                    searchBox.Focus();
                    Keyboard.Focus(searchBox);
                }
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) return typed;
            var result = FindVisualChild<T>(child);
            if (result != null) return result;
        }
        return null;
    }
}
