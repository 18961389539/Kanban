using System.Windows;
using System.Windows.Navigation;

namespace MainAPP.Views;

public partial class HelpWindow : Window
{
    private string? _pendingAnchor;

    public HelpWindow(string title, string html, string? anchor = null)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        NavigateHtml(html, title, anchor);
    }

    public void NavigateHtml(string html, string title, string? anchor = null)
    {
        Title = title;
        TitleText.Text = title;
        _pendingAnchor = anchor;
        Browser.LoadCompleted -= OnBrowserLoadCompleted;
        Browser.LoadCompleted += OnBrowserLoadCompleted;
        Browser.NavigateToString(html);
    }

    private void OnBrowserLoadCompleted(object? sender, NavigationEventArgs e)
    {
        if (Browser.Document is null)
            return;
        Browser.LoadCompleted -= OnBrowserLoadCompleted;
        if (string.IsNullOrEmpty(_pendingAnchor))
            return;

        try
        {
            Browser.InvokeScript("scrollToAnchor", _pendingAnchor);
        }
        catch
        {
            // 手册已经打开。锚点滚不到时停在文首。
        }
    }
}
