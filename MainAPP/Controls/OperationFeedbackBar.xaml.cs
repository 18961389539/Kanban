using System.Windows;
using MainAPP.Models;

namespace MainAPP.Controls;

public partial class OperationFeedbackBar : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty FeedbackProperty = DependencyProperty.Register(
        nameof(Feedback),
        typeof(OperationFeedback),
        typeof(OperationFeedbackBar),
        new PropertyMetadata(null));

    public OperationFeedback? Feedback
    {
        get => (OperationFeedback?)GetValue(FeedbackProperty);
        set => SetValue(FeedbackProperty, value);
    }

    public OperationFeedbackBar()
    {
        InitializeComponent();
    }
}