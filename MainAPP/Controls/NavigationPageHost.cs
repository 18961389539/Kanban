using System.Windows;
using System.Windows.Controls;
using Kanban.Core.Models;
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
    }

    private static void OnPageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is NavigationPageHost host && host.IsVisible)
            host.EnsureViewLoaded();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
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
