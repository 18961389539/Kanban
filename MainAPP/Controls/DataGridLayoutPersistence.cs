using System.IO;
using System.Text.Json;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using Kanban.Collector.Core.Services;
using MainAPP.Services;

namespace MainAPP.Controls;

/// <summary>
/// Persists DataGrid column widths and sort state in the user's Config directory.
/// The column header text is used as the stable key because these grids are read-only reports.
/// </summary>
public static class DataGridLayoutPersistence
{
    private const string FileName = "ui-layout.json";
    private static readonly object Sync = new();
    private static readonly HashSet<DataGrid> AttachedGrids = [];

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(DataGridLayoutPersistence),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid grid) return;
        if ((bool)e.NewValue)
        {
            grid.Loaded += OnGridLoaded;
            grid.Unloaded += OnGridUnloaded;
            grid.ColumnReordered += OnGridChanged;
            grid.Sorting += OnGridSorting;
        }
        else
        {
            Detach(grid);
        }
    }

    private static void OnGridLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        lock (Sync) AttachedGrids.Add(grid);
        foreach (var column in grid.Columns.OfType<DataGridBoundColumn>())
        {
            if (column.SortMemberPath.Length == 0 && column.Binding is Binding binding)
                column.SortMemberPath = binding.Path?.Path ?? string.Empty;
        }
        Restore(grid);
    }

    private static void OnGridUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is DataGrid grid)
        {
            Save(grid);
            lock (Sync) AttachedGrids.Remove(grid);
        }
    }

    private static void OnGridChanged(object? sender, DataGridColumnEventArgs e)
    {
        if (sender is DataGrid grid) Save(grid);
    }

    private static void OnGridSorting(object? sender, DataGridSortingEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        grid.Dispatcher.BeginInvoke(() => Save(grid), System.Windows.Threading.DispatcherPriority.Background);
    }

    private static void Detach(DataGrid grid)
    {
        grid.Loaded -= OnGridLoaded;
        grid.Unloaded -= OnGridUnloaded;
        grid.ColumnReordered -= OnGridChanged;
        grid.Sorting -= OnGridSorting;
        lock (Sync) AttachedGrids.Remove(grid);
    }

    private static void Restore(DataGrid grid)
    {
        var state = Load().GetValueOrDefault(GetGridKey(grid));
        if (state == null) return;

        foreach (var column in grid.Columns)
        {
            var key = GetColumnKey(column);
            if (state.Columns.TryGetValue(key, out var width) && width > 0)
                column.Width = new DataGridLength(width, DataGridLengthUnitType.Pixel);
        }

        if (!string.IsNullOrWhiteSpace(state.SortColumn))
        {
            var column = grid.Columns.FirstOrDefault(c => GetColumnKey(c) == state.SortColumn);
            if (column != null && column.SortMemberPath is { Length: > 0 } path)
            {
                column.SortDirection = state.SortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending;
                using (grid.Items.DeferRefresh())
                {
                    grid.Items.SortDescriptions.Clear();
                    grid.Items.SortDescriptions.Add(new SortDescription(path, column.SortDirection.Value));
                }
            }
        }
    }

    private static void Save(DataGrid grid)
    {
        var all = Load();
        var state = new GridState();
        foreach (var column in grid.Columns)
        {
            if (column.Width.IsAbsolute && column.Width.Value > 0)
                state.Columns[GetColumnKey(column)] = column.Width.Value;
            if (column.SortDirection is { } direction)
            {
                state.SortColumn = GetColumnKey(column);
                state.SortDescending = direction == ListSortDirection.Descending;
            }
        }
        all[GetGridKey(grid)] = state;

        try
        {
            var path = Path.Combine(AppSettings.DataRoot, "Config", FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            lock (Sync)
                File.WriteAllText(path, JsonSerializer.Serialize(all, AppSettings.JsonOptions));
        }
        catch
        {
            // Layout preferences must never affect the report itself.
        }
    }

    private static Dictionary<string, GridState> Load()
    {
        try
        {
            var path = Path.Combine(AppSettings.DataRoot, "Config", FileName);
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Dictionary<string, GridState>>(File.ReadAllText(path)) ?? [];
        }
        catch
        {
            // Ignore a damaged preference file and recreate it on the next change.
        }
        return [];
    }

    private static string GetGridKey(DataGrid grid)
        => string.Join("|", grid.Columns.Select(GetColumnKey));

    private static string GetColumnKey(DataGridColumn column)
        => column.Header?.ToString() ?? column.DisplayIndex.ToString();

    private sealed class GridState
    {
        public Dictionary<string, double> Columns { get; set; } = [];
        public string SortColumn { get; set; } = string.Empty;
        public bool SortDescending { get; set; }
    }
}
