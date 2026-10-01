using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MainAPP.Services;

namespace MainAPP.Controls;

/// <summary>侧栏里的颜色对照表。列宽平分当前说明栏。</summary>
public class PageHelpTableView : Grid
{
    public static readonly DependencyProperty RowsProperty = DependencyProperty.Register(
        nameof(Rows),
        typeof(IEnumerable),
        typeof(PageHelpTableView),
        new PropertyMetadata(null, OnRowsChanged));

    public PageHelpTableView()
    {
        Loaded += (_, _) => Rebuild();
    }

    public IEnumerable? Rows
    {
        get => (IEnumerable?)GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    private static void OnRowsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((PageHelpTableView)d).Rebuild();

    private void Rebuild()
    {
        RowDefinitions.Clear();
        ColumnDefinitions.Clear();
        Children.Clear();
        if (Rows is not IEnumerable source)
            return;

        var rows = new List<PageHelpTableRow>();
        foreach (var item in source)
        {
            if (item is PageHelpTableRow row)
                rows.Add(row);
        }

        if (rows.Count == 0)
            return;

        var columns = 0;
        foreach (var row in rows)
            columns = Math.Max(columns, row.Cells.Count);
        for (var column = 0; column < columns; column++)
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var borderBrush = TryFindResource("BorderBrush") as Brush ?? Brushes.Gray;
        var headerBrush = TryFindResource("SecondaryRegionBrush") as Brush ?? Brushes.Transparent;
        var textBrush = TryFindResource("PrimaryTextBrush") as Brush ?? Brushes.White;

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var row = rows[rowIndex];
            for (var column = 0; column < row.Cells.Count; column++)
            {
                var border = new Border
                {
                    BorderBrush = borderBrush,
                    BorderThickness = new Thickness(0.5),
                    Padding = new Thickness(6, 4, 6, 4),
                    Background = row.IsHeader ? headerBrush : Brushes.Transparent
                };
                border.Child = new PageHelpText
                {
                    Runs = row.Cells[column].Runs,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = textBrush,
                    FontWeight = row.IsHeader ? FontWeights.SemiBold : FontWeights.Normal,
                    FontSize = 12
                };
                SetRow(border, rowIndex);
                SetColumn(border, column);
                Children.Add(border);
            }
        }
    }
}
