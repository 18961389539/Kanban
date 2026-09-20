using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MainAPP.ViewModels;

namespace MainAPP.Views;

/// <summary>
/// 报警中心视图：实时活跃报警墙 + 最近事件流 + Top 报警排行。
/// 定时器启动/停止改由 ViewModel 生命周期驱动（审查修复 2026-08-30）：
/// NavigationPageHost 常驻导致 Unloaded 永不触发，原来在 Loaded/Unloaded 里
/// 调 vm.Start()/Stop() 会泄漏定时器；现由 MainWindow.ActivatePage 经
/// INavigationPageLifecycle 调用，切走即停止。
/// </summary>
public partial class AlarmCenterView : UserControl
{
    public AlarmCenterView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 页面级快捷键（Preview 隧道事件）：Ctrl+F 焦点切到报警搜索框
    /// （与设备管理/产线/工单管理页一致，全站搜索统一入口）。
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            AlarmSearchBox.Focus();
            Keyboard.Focus(AlarmSearchBox);
            e.Handled = true;
        }
    }

    /// <summary>
    /// 进入页面（页面切换为当前页时 Visibility 变 Visible）自动聚焦搜索框，
    /// 免点击直接输入关键字过滤报警。
    /// </summary>
    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (IsVisible)
                {
                    AlarmSearchBox.Focus();
                    Keyboard.Focus(AlarmSearchBox);
                }
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }
}
