using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Kanban.Collector.Core.Models;
using MainAPP.Models;

namespace MainAPP.Controls;

public sealed class NavigationPageHost : ContentControl
{
    public static readonly DependencyProperty PageProperty = DependencyProperty.Register(
        nameof(Page),
        typeof(NavigationPage),
        typeof(NavigationPageHost),
        new PropertyMetadata(null, OnPageChanged));

    public NavigationPage? Page
    {
        get => (NavigationPage?)GetValue(PageProperty);
        set => SetValue(PageProperty, value);
    }

    public NavigationPageHost()
    {
        IsVisibleChanged += OnIsVisibleChanged;
        // 可靠兜底：IsVisibleChanged 依赖 IsVisible 计算链（祖先可见性/布局时序），
        // 页面切换时可能不触发（线上黑屏：选中导航项但页面 Content 始终为 null）。
        // Visibility 是属性级直接变化，绑定更新 Visibility=Visible 时必然触发。
        DependencyPropertyDescriptor.FromProperty(VisibilityProperty, typeof(NavigationPageHost))
            .AddValueChanged(this, OnVisibilityPropertyChanged);
    }

    /// <summary>
    /// 延迟到绑定/布局稳定后再判定是否加载（2026-08-11 懒加载修复）：
    /// ItemsControl 生成 host 并绑定 Page 时，Visibility 绑定（IsCurrent → Collapsed/Visible）
    /// 尚未应用，此刻 host 仍是默认 Visible，立即判断会把隐藏页误判为可见而提前创建
    /// View/ViewModel——破坏页面 VM 懒加载（启动期全量实例化）。
    /// Loaded 优先级回调时绑定已同步，Visibility 是最终值。
    /// </summary>
    private void ScheduleEnsureViewLoaded()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (Visibility == Visibility.Visible)
                EnsureViewLoaded();
        });
    }

    private void OnVisibilityPropertyChanged(object? sender, EventArgs e)
    {
        if (Visibility == Visibility.Visible)
            ScheduleEnsureViewLoaded();
    }

    private static void OnPageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is NavigationPageHost host)
            host.ScheduleEnsureViewLoaded();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
            ScheduleEnsureViewLoaded();
    }

    private void EnsureViewLoaded()
    {
        if (Page is null || Content is not null)
            return;

        var view = Page.EnsureView();
        if (view is FrameworkElement element)
            element.DataContext = Page.ViewModel;
        Content = view;
    }
}
