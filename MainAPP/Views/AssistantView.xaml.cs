using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MainAPP.ViewModels;

namespace MainAPP.Views;

public partial class AssistantView : UserControl
{
    public AssistantView()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is AssistantViewModel vm)
            vm.Messages.CollectionChanged += OnMessagesChanged;
        Refresh();
    }

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
            Refresh();
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None)
            return;
        if (DataContext is AssistantViewModel vm && vm.SendCommand.CanExecute(null))
            vm.SendCommand.Execute(null);
        e.Handled = true;
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (var item in e.NewItems)
            {
                if (item is AssistantTurn turn)
                    turn.PropertyChanged += OnTurnChanged;
            }
        }

        Transcript.ScrollToEnd();
    }

    private void OnTurnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AssistantTurn.Text))
            Transcript.ScrollToEnd();
    }

    private void Refresh()
    {
        if (DataContext is AssistantViewModel vm)
            vm.RefreshContext();
    }
}
