using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MainAPP.Models;

namespace MainAPP.Controls;

/// <summary>
/// 导航页宿主：只在本页 <see cref="NavigationPage.IsCurrent"/> 变为可见后延迟创建 View。
/// 唯一入口是 Visibility（绑定 IsCurrent）+ Loaded 优先级延迟，避免 ItemsControl 生成时
/// 默认 Visible 把隐藏页提前实例化。ContentControl.HasContent 可区分「没切到这页」和「切了但 View 还没挂上」。
/// </summary>
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
        DependencyPropertyDescriptor.FromProperty(VisibilityProperty, typeof(NavigationPageHost))
            .AddValueChanged(this, OnVisibilityPropertyChanged);
    }

    /// <summary>
    /// 推迟到绑定把 Visibility 收到最终值之后再加载。
    /// ItemsControl 生成 host 时默认 Visible，此刻立即判断会把折叠页提前创建。
    /// </summary>
    private void ScheduleEnsureViewLoaded()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, EnsureViewLoadedIfVisible);
    }

    private void OnVisibilityPropertyChanged(object? sender, EventArgs e)
        => ScheduleEnsureViewLoaded();

    private static void OnPageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is NavigationPageHost host)
            host.ScheduleEnsureViewLoaded();
    }

    private void EnsureViewLoadedIfVisible()
    {
        if (Visibility != Visibility.Visible)
            return;
        EnsureViewLoaded();
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
