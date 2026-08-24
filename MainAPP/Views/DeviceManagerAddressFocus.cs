using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MainAPP.Views;

internal static class DeviceManagerAddressFocus
{
    public static void FocusTextBox(DependencyObject root, string address)
    {
        var normalized = address.Trim();
        if (string.IsNullOrEmpty(normalized)) return;

        var target = FindTextBox(root, normalized);
        if (target == null) return;
        target.Focus();
        Keyboard.Focus(target);
        target.SelectAll();
    }

    public static void BeginEditAddress(DataGrid grid, object item, string address, int columnIndex)
    {
        grid.SelectedItem = item;
        grid.ScrollIntoView(item);
        if (columnIndex >= 0 && columnIndex < grid.Columns.Count)
        {
            grid.CurrentCell = new DataGridCellInfo(item, grid.Columns[columnIndex]);
            grid.Focus();
            grid.BeginEdit();
        }

        grid.Dispatcher.BeginInvoke(
            new Action(() => FocusTextBox(grid, address)),
            DispatcherPriority.Input);
    }

    private static TextBox? FindTextBox(DependencyObject parent, string address)
    {
        if (parent is TextBox textBox
            && string.Equals(textBox.Text.Trim(), address, StringComparison.OrdinalIgnoreCase))
            return textBox;

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var result = FindTextBox(VisualTreeHelper.GetChild(parent, i), address);
            if (result != null) return result;
        }

        return null;
    }
}