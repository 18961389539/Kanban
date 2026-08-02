using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MainAPP.Views;

/// <summary>
/// 产线总览页：根据设备数量自适应切换布局（大卡片/中卡片/表格）。
/// </summary>
public partial class ProductionLineView : UserControl
{
    public ProductionLineView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Ctrl+F 焦点切到设备搜索框（hc:SearchBar 内部含 TextBox）。
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
