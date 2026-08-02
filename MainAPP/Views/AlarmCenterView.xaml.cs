using System.Windows.Controls;
using MainAPP.ViewModels;

namespace MainAPP.Views;

/// <summary>
/// 报警中心视图：实时活跃报警墙 + 最近事件流 + Top 报警排行。
/// </summary>
public partial class AlarmCenterView : UserControl
{
    public AlarmCenterView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        // 页面可见时启动定时刷新，减少切走后的无谓 CPU/DB 开销
        if (DataContext is AlarmCenterViewModel vm)
            vm.Start();
    }

    private void OnUnloaded(object sender, System.Windows.RoutedEventArgs e)
    {
        // 页面切走时停止定时刷新
        if (DataContext is AlarmCenterViewModel vm)
            vm.Stop();
    }
}
