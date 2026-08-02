namespace MainAPP.Models;

public sealed class NavigationPageDefinition
{
    public required string Key { get; init; }
    public required int Index { get; init; }
    public required NavItem NavItem { get; init; }
    public bool ShowInSidebar { get; init; }
}

public sealed class NavigationPage
{
    private readonly Lazy<object> _view;

    public NavigationPageDefinition Definition { get; }
    public object? View => _view.IsValueCreated ? _view.Value : null;
    public object ViewModel { get; }

    public NavigationPage(NavigationPageDefinition definition, Func<object> viewFactory, object viewModel)
    {
        Definition = definition;
        _view = new Lazy<object>(viewFactory, LazyThreadSafetyMode.ExecutionAndPublication);
        ViewModel = viewModel;
    }

    public object EnsureView() => _view.Value;
}