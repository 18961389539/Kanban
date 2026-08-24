using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.ComponentModel;
using Kanban.Collector.Core.Models;
using MainAPP.Models;
using MainAPP.Services;
using MainAPP.ViewModels;
using Serilog;

namespace MainAPP.Views;

/// <summary>
/// DeviceManagerView.xaml 的交互逻辑
/// </summary>
public partial class DeviceManagerView : UserControl
{
    // 构造时刻：用于测量从构造到首次 Loaded（首次渲染完成）的耗时
    private readonly long _ctorTicks = Stopwatch.GetTimestamp();

    // 拖拽排序临时状态：按下时记录起点与拖拽设备，移动超过阈值才真正发起拖拽
    private Point _dragStartPoint;
    private Device? _draggedDevice;

    public DeviceManagerView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Log.Debug("视图 {View} 构造（DI 单例，应仅出现一次）", "DeviceManagerView");
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is INotifyPropertyChanged oldVm)
            oldVm.PropertyChanged -= OnViewModelPropertyChanged;
        if (e.NewValue is INotifyPropertyChanged newVm)
            newVm.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DeviceManagerViewModel.AddressConflictFocusRequest)) return;
        if (sender is not DeviceManagerViewModel vm || string.IsNullOrWhiteSpace(vm.FocusedAddressConflict)) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            switch (vm.SelectedTabIndex)
            {
                case (int)DeviceManagerTab.Parameters:
                    FindVisualChild<DeviceManagerDeviceParamsTab>(this)?.FocusAddress(vm.FocusedAddressConflict);
                    break;
                case (int)DeviceManagerTab.Alarms:
                    FindVisualChild<DeviceManagerAlarmsTab>(this)?.FocusAddress(vm.FocusedAddressConflict);
                    break;
                case (int)DeviceManagerTab.Defects:
                    FindVisualChild<DeviceManagerDefectsTab>(this)?.FocusAddress(vm.FocusedAddressConflict);
                    break;
                case (int)DeviceManagerTab.CounterAlarms:
                    FindVisualChild<DeviceManagerCounterAlarmsTab>(this)?.FocusAddress(vm.FocusedAddressConflict);
                    break;
                case (int)DeviceManagerTab.Sources:
                    FindVisualChild<DeviceManagerSourcesTab>(this)?.FocusAddress(vm.FocusedAddressConflict);
                    break;
            }
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 首次渲染完成（首次切到设备管理页时触发一次）。
    /// 记录构造→Loaded 的耗时，判断页面首次加载是否是卡顿源。
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        var elapsedMs = (Stopwatch.GetTimestamp() - _ctorTicks) * 1000.0 / Stopwatch.Frequency;
        Log.Debug("视图 {View} 首次 Loaded，构造→Loaded 耗时 {ElapsedMs:F1}ms", "DeviceManagerView", elapsedMs);
    }

    /// <summary>
    /// 内部 TabControl 切换 Tab 时触发。
    /// WPF TabControl 默认懒加载 TabItem 内容，首次切到某 Tab 会实例化内容，是常见卡顿源。
    /// 通过事件路由在 UserControl 级捕获子 TabControl 的 SelectionChanged，无需为 TabControl 命名。
    /// </summary>
    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource is TabControl tc && tc.SelectedItem is TabItem tab)
        {
            var header = tab.Header?.ToString() ?? "?";
            Log.Debug("DeviceManager Tab 切换 → {Header}", header);
        }
    }

    /// <summary>
    /// 全局快捷键：
    /// - Ctrl+S：在设备页内即时保存当前配置。Preview（隧道）事件会从根向焦点元素传递，
    ///   因此即使焦点在子文本框也能捕获到 Ctrl+S；命中后标记事件已处理，避免文本框把 S 当作普通输入。
    ///   仅当保存命令可用时生效。
    /// - Ctrl+F：焦点切到设备搜索框（hc:SearchBar 内部含 TextBox）。
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            if (DataContext is DeviceManagerViewModel vm && vm.SaveCommand.CanExecute(null))
            {
                vm.SaveCommand.Execute(null);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            // hc:SearchBar 内部含 TextBox，用 VisualTreeHelper 找到后聚焦
            if (FindVisualChild<TextBox>(DeviceSearchBar) is { } searchBox)
            {
                searchBox.Focus();
                Keyboard.Focus(searchBox);
                e.Handled = true;
            }
        }
    }

    /// <summary>在可视树中查找指定类型的第一个子元素。</summary>
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

    /// <summary>
    /// "更多"按钮点击：在按钮下方弹出 ContextMenu 承载低频操作（复制/导入/导出/恢复/虚拟）。
    /// ContextMenu 不在视觉树中，DataContext 默认不继承 PlacementTarget，因此显式同步 VM。
    /// </summary>
    private void OnMoreActionsClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.ContextMenu is { } menu)
        {
            menu.PlacementTarget = btn;
            menu.Placement = PlacementMode.Bottom;
            menu.HorizontalOffset = 0;
            menu.VerticalOffset = 4;
            menu.DataContext = btn.DataContext;
            menu.IsOpen = true;
        }
    }

    // ──────────── 设备列表拖拽排序 ────────────

    /// <summary>
    /// 记录拖拽起点与源设备。在列表项上按下左键时记录，松开或移动过阈值才真正发起拖拽，
    /// 避免普通点击选中被误判为拖拽。
    /// </summary>
    private void OnListPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _draggedDevice = FindListBoxItem(e.OriginalSource)?.DataContext as Device;
        if (_draggedDevice != null)
            _dragStartPoint = e.GetPosition(null);
    }

    /// <summary>
    /// 左键按住并移动超过阈值时发起拖拽（DragDrop）。仅当 DataContext 为 DeviceManagerViewModel 时生效。
    /// </summary>
    private void OnListPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_draggedDevice == null) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _draggedDevice = null; // 未按住（如点击后松开再移动）→ 取消拖拽意图
            return;
        }
        var diff = e.GetPosition(null) - _dragStartPoint;
        if (Math.Abs(diff.X) < 5 && Math.Abs(diff.Y) < 5) return; // 阈值内视为点击，不拖拽

        if (sender is ListBox lb && lb.DataContext is DeviceManagerViewModel)
        {
            var data = new DataObject(typeof(Device), _draggedDevice);
            DragDrop.DoDragDrop(lb, data, DragDropEffects.Move);
            _draggedDevice = null;
        }
    }

    /// <summary>
    /// 拖拽悬停时显示"移动"光标。
    /// </summary>
    private void OnListDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(Device)))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }
    }

    /// <summary>
    /// 放置：把被拖拽设备移动到目标设备所在的列表位置（位置即持久化顺序）。
    /// 目标取鼠标下方的列表项；若取不到则退化为当前选中设备。
    /// </summary>
    private void OnListDrop(object sender, DragEventArgs e)
    {
        if (sender is not ListBox lb || lb.DataContext is not DeviceManagerViewModel vm) return;
        if (e.Data.GetData(typeof(Device)) is not Device dragged) return;

        var targetItem = FindListBoxItem(e.OriginalSource);
        // 只接受鼠标实际悬停的设备项；空白区域不应隐式移动到当前选中设备。
        if (targetItem?.DataContext is Device target)
            vm.MoveDevice(dragged, target);
        e.Handled = true;
    }

    /// <summary>
    /// 从拖拽事件的原始源向上回溯到 ListBoxItem。
    /// </summary>
    private static ListBoxItem? FindListBoxItem(object source)
    {
        var dep = source as DependencyObject;
        while (dep != null && dep is not ListBoxItem)
            dep = VisualTreeHelper.GetParent(dep);
        return dep as ListBoxItem;
    }
}
