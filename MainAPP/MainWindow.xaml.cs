using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Kanban.Collector.Core.Models;
using MainAPP.Models;
using System.Windows.Threading;
using MainAPP.ViewModels;
using MainAPP.Views;

namespace MainAPP;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private bool _isFullscreen;
    private INavigationPageLifecycle? _activePageLifecycle;

    public IReadOnlyList<NavigationPage> Pages { get; }
    private WindowStyle _windowStyleBeforeFullscreen;
    private ResizeMode _resizeModeBeforeFullscreen;
    private WindowState _windowStateBeforeFullscreen;
    private double _leftBeforeFullscreen;
    private double _topBeforeFullscreen;
    private double _widthBeforeFullscreen;
    private double _heightBeforeFullscreen;

    public MainWindow(MainWindowViewModel viewModel, IEnumerable<INavigationPageModule> pageModules)
    {
        Pages = pageModules.Select(module => module.Page)
            .OrderBy(page => page.Definition.Index)
            .ToArray();
        InitializeComponent();

        // 注册 Growl 通知容器：HandyControl 的 Growl.Error/Warning/Success/Info
        // 需要一个 Panel 容器才能在 UI 显示。XAML 中 hc:Growl.GrowlParent 附加属性
        // 在 HC 3.5.1 下 XAML 编译报 MC3074，改用代码方式注册。
        HandyControl.Controls.Growl.SetGrowlParent(GrowlContainer, true);

        DataContext = viewModel;
        viewModel.PropertyChanged += OnMainViewModelPropertyChanged;

        ActivatePage(viewModel.SelectedIndex);

        // 关键：ObservableProperty 在值不变时不触发 PropertyChanged，而构造时 SelectedIndex
        // 已经是默认 0。手动同步一次所有 NavigationPage.IsCurrent，否则启动时所有 host 都
        // 保持 IsCurrent=false（默认）→ Visibility=Collapsed → Content=null → 黑屏。
        foreach (var p in Pages)
        {
            p.IsCurrent = p.Definition.Index == viewModel.SelectedIndex;
        }

        // XAML 已设 WindowStyle=None + ResizeMode=NoResize + WindowState=Maximized，
        // 初始即为无边框全屏，无需 Loaded 时 ToggleFullscreen 切换（避免 Show 期间 HWND 重建导致黑屏）。
        // 初始化"退出全屏"恢复状态，供 F11 → ExitFullscreen 使用。
        _isFullscreen = true;
        _windowStyleBeforeFullscreen = WindowStyle.SingleBorderWindow;
        _resizeModeBeforeFullscreen = ResizeMode.CanResize;
        _windowStateBeforeFullscreen = WindowState.Maximized;

        // ContentRendered 是窗口首帧实际渲染完成的时刻，
        // 从 Show() 到 ContentRendered 的差值即为用户感知的"白屏到可交互"时间
        ContentRendered += OnContentRendered;
        Loaded += OnMainWindowLoaded;

        // SideMenu 已替换为 ListBox，SelectedIndex 通过 XAML 双向绑定到 ViewModel.SelectedIndex，
        // 用户点击 ListBoxItem 时由 Binding 自动同步，不再需要 OnNavSelectionChanged/OnViewModelPropertyChanged。

        // 关闭前未保存确认：仅当设备配置存在未保存修改时拦截，避免误关丢失配置
        Closing += OnMainWindowClosing;
    }

    private void OnMainViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is MainWindowViewModel viewModel)
        {
            if (e.PropertyName == nameof(MainWindowViewModel.SelectedIndex))
            {
                // 忽略 ListBox 初始化时通过 TwoWay Binding 写回的 -1（这是 ListBox 自身的
                // 初始选中态副作用，不应触发页面 IsCurrent 重置）。
                if (viewModel.SelectedIndex < 0) return;
                ActivatePage(viewModel.SelectedIndex);
                foreach (var p in Pages)
                {
                    p.IsCurrent = p.Definition.Index == viewModel.SelectedIndex;
                }
            }
        }
    }

    private void ActivatePage(int index)
    {
        var next = Pages.FirstOrDefault(page => page.Definition.Index == index)?.ViewModel as INavigationPageLifecycle;
        if (ReferenceEquals(next, _activePageLifecycle)) return;
        _activePageLifecycle?.OnPageExit();
        _activePageLifecycle = next;
        _activePageLifecycle?.OnPageEnter();
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.F11)
        {
            ToggleFullscreen();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Escape && _isFullscreen)
        {
            ExitFullscreen();
            e.Handled = true;
        }
    }

    private void ToggleFullscreen()
    {
        if (_isFullscreen)
        {
            ExitFullscreen();
            return;
        }

        _windowStyleBeforeFullscreen = WindowStyle;
        _resizeModeBeforeFullscreen = ResizeMode;
        _windowStateBeforeFullscreen = WindowState;
        _leftBeforeFullscreen = Left;
        _topBeforeFullscreen = Top;
        _widthBeforeFullscreen = Width;
        _heightBeforeFullscreen = Height;
        _isFullscreen = true;
        NavigationBar.Visibility = Visibility.Collapsed;
        FullscreenMenuButton.Visibility = Visibility.Visible;

        var monitorBounds = GetCurrentMonitorBounds();
        WindowState = WindowState.Normal;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Left = monitorBounds.Left;
        Top = monitorBounds.Top;
        Width = monitorBounds.Width;
        Height = monitorBounds.Height;
    }

    private void ExitFullscreen()
    {
        _isFullscreen = false;
        NavigationBar.Visibility = Visibility.Visible;
        FullscreenMenuButton.Visibility = Visibility.Collapsed;
        ResizeMode = _resizeModeBeforeFullscreen;
        WindowStyle = _windowStyleBeforeFullscreen;
        Left = _leftBeforeFullscreen;
        Top = _topBeforeFullscreen;
        Width = _widthBeforeFullscreen;
        Height = _heightBeforeFullscreen;
        WindowState = _windowStateBeforeFullscreen;
    }

    private void OnFullscreenMenuClick(object sender, RoutedEventArgs e)
    {
        if (!_isFullscreen) return;

        NavigationBar.Visibility = Visibility.Visible;
        FullscreenMenuButton.Visibility = Visibility.Collapsed;
        NavListBox.Focus();
    }

    private Rect GetCurrentMonitorBounds()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
            return SystemParameters.WorkArea;

        var monitorInfo = new MonitorInfo { CbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
            return SystemParameters.WorkArea;

        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is not { } target)
            return new Rect(
                monitorInfo.Monitor.Left,
                monitorInfo.Monitor.Top,
                monitorInfo.Monitor.Right - monitorInfo.Monitor.Left,
                monitorInfo.Monitor.Bottom - monitorInfo.Monitor.Top);

        var topLeft = target.TransformFromDevice.Transform(new Point(monitorInfo.Monitor.Left, monitorInfo.Monitor.Top));
        var bottomRight = target.TransformFromDevice.Transform(new Point(monitorInfo.Monitor.Right, monitorInfo.Monitor.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    private const uint MonitorDefaultToNearest = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int CbSize;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>
    /// 主窗口关闭拦截：设备管理页存在未保存修改时弹确认框，
    /// 用户选择"否"取消关闭（留在页面保存），"是"允许关闭（丢弃未保存修改）。
    /// </summary>
    private void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        // Viewer（展示）模式：退出看板需确认，防止车间工人误关大屏。
        if (DataContext is MainWindowViewModel viewerVm && viewerVm.IsViewerMode)
        {
            var confirm = MessageBox.Show(
                "展示终端正在运行，确定要退出看板吗？",
                "退出确认",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        if (DataContext is MainWindowViewModel vm && !vm.DeviceManagerViewModel.TryCloseWithDirtyCheck())
            e.Cancel = true;
        if (!e.Cancel && DataContext is MainWindowViewModel closingViewModel)
            closingViewModel.PropertyChanged -= OnMainViewModelPropertyChanged;
    }

    private bool _restoringNavigation;

    private void OnNavigationSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_restoringNavigation || e.RemovedItems.Count == 0 || e.AddedItems.Count == 0) return;
        if (DataContext is not MainWindowViewModel vm
            || e.RemovedItems[0] is not NavItem oldItem
            || e.AddedItems[0] is not NavItem newItem
            || oldItem.Index != 3
            || newItem.Index == 3)
            return;

        if (vm.DeviceManagerViewModel.TryLeaveWithDirtyCheck()) return;

        _restoringNavigation = true;
        try
        {
            // 脏检查拒绝离开：回退页面索引并恢复侧边栏选中项（SelectedItem 为 NavItem 引用，
            // 不能用视觉 SelectedIndex——列表位置与页面 Index 存在错位，见 MainWindowViewModel.SelectedNavItem）
            vm.SelectedIndex = oldItem.Index;
            NavListBox.SelectedItem = oldItem;
        }
        finally
        {
            _restoringNavigation = false;
        }
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= OnContentRendered;
        Serilog.Log.Information("MainWindow.ContentRendered 首帧渲染完成");
    }

    private void OnMainWindowLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnMainWindowLoaded;
        Serilog.Log.Information("MainWindow.Loaded 事件触发");
        // 不再在此处调用 ToggleFullscreen：XAML 已设 WindowStyle=None + WindowState=Maximized，
        // 初始即为全屏无边框，Loaded 时切换 WindowStyle 会触发 HWND 重建导致黑屏。
        // 只等待当前页面首帧完成；其余页面由 NavigationPageHost 在首次进入时按需创建。
        WarmupViewsAsync().ContinueWith(t =>
        {
            if (t.IsFaulted)
                Serilog.Log.Error(t.Exception, "视图预热失败");
        }, TaskScheduler.Default);
    }

    private async Task WarmupViewsAsync()
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        UpdateLayout();
    }
}
