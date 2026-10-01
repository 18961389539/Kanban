using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using MainAPP.Services;

namespace MainAPP.Controls;

/// <summary>把说明里的控件名加粗。星号在解析时已经去掉。</summary>
public class PageHelpText : TextBlock
{
    public static readonly DependencyProperty RunsProperty = DependencyProperty.Register(
        nameof(Runs),
        typeof(IEnumerable),
        typeof(PageHelpText),
        new PropertyMetadata(null, OnRunsChanged));

    public IEnumerable? Runs
    {
        get => (IEnumerable?)GetValue(RunsProperty);
        set => SetValue(RunsProperty, value);
    }

    private static void OnRunsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var text = (PageHelpText)d;
        text.Inlines.Clear();
        if (e.NewValue is not IEnumerable runs)
            return;

        foreach (var item in runs)
        {
            if (item is not PageHelpRun run)
                continue;
            text.Inlines.Add(new Run(run.Text)
            {
                FontWeight = run.IsBold ? FontWeights.SemiBold : FontWeights.Normal
            });
        }
    }
}
