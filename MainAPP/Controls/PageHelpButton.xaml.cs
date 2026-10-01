using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MainAPP.Services;
using MainAPP.ViewModels;

namespace MainAPP.Controls;

/// <summary>页标题旁的问号。点按后在窗口右侧展开该页的短说明。</summary>
public partial class PageHelpButton : UserControl
{
    public static readonly DependencyProperty PageKeyProperty =
        DependencyProperty.Register(
            nameof(PageKey),
            typeof(string),
            typeof(PageHelpButton),
            new PropertyMetadata("", OnPageKeyChanged));

    private PageHelpService? _help;

    public PageHelpButton()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public string PageKey
    {
        get => (string)GetValue(PageKeyProperty);
        set => SetValue(PageKeyProperty, value);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Detach();
        _help = FindHelp();
        if (_help is not null)
            _help.PropertyChanged += OnHelpChanged;
        UpdateVisual();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Detach();

    private void Detach()
    {
        if (_help is null) return;
        _help.PropertyChanged -= OnHelpChanged;
        _help = null;
    }

    private void OnHelpChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PageHelpService.IsOpen) or nameof(PageHelpService.ActiveKey))
        {
            if (Dispatcher.CheckAccess()) UpdateVisual();
            else Dispatcher.Invoke(UpdateVisual);
        }
    }

    private void OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(PageKey)) return;
        FindHelp()?.Toggle(PageKey);
    }

    private static void OnPageKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PageHelpButton button) return;
        button.UpdateVisual();
        if (e.NewValue is not string key || string.IsNullOrEmpty(key)) return;
        var help = button._help ?? button.FindHelp();
        if (help is { IsOpen: true } && (string?)e.OldValue == help.ActiveKey)
            help.Show(key);
    }

    private PageHelpService? FindHelp()
        => Window.GetWindow(this)?.DataContext is MainWindowViewModel vm ? vm.PageHelp : null;

    private void UpdateVisual()
    {
        if (HelpButton is null) return;
        var open = _help is { IsOpen: true } && _help.ActiveKey == PageKey;
        HelpButton.Background = open
            ? (Brush)FindResource("PrimarySoftBrush")
            : Brushes.Transparent;
    }
}
