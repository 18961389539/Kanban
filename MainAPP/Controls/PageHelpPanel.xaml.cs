using System.Collections;
using System.Windows;
using System.Windows.Controls;

namespace MainAPP.Controls;

/// <summary>本页说明正文：开篇始终可见，每个小节可单独展开。</summary>
public partial class PageHelpPanel : UserControl
{
    public static readonly DependencyProperty IntroProperty = DependencyProperty.Register(
        nameof(Intro),
        typeof(IEnumerable),
        typeof(PageHelpPanel),
        new PropertyMetadata(null));

    public static readonly DependencyProperty SectionsProperty = DependencyProperty.Register(
        nameof(Sections),
        typeof(IEnumerable),
        typeof(PageHelpPanel),
        new PropertyMetadata(null));

    public static readonly DependencyProperty ErrorProperty = DependencyProperty.Register(
        nameof(Error),
        typeof(string),
        typeof(PageHelpPanel),
        new PropertyMetadata(null));

    public PageHelpPanel()
    {
        InitializeComponent();
    }

    public IEnumerable? Intro
    {
        get => (IEnumerable?)GetValue(IntroProperty);
        set => SetValue(IntroProperty, value);
    }

    public IEnumerable? Sections
    {
        get => (IEnumerable?)GetValue(SectionsProperty);
        set => SetValue(SectionsProperty, value);
    }

    public string? Error
    {
        get => (string?)GetValue(ErrorProperty);
        set => SetValue(ErrorProperty, value);
    }
}
