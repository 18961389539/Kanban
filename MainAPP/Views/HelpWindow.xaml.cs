using System.Windows;

namespace MainAPP.Views;

public partial class HelpWindow : Window
{
    public HelpWindow(string title, string html)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        NavigateHtml(html, title);
    }

    public void NavigateHtml(string html, string title)
    {
        Title = title;
        TitleText.Text = title;
        Browser.NavigateToString(html);
    }
}
