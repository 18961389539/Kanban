namespace MainAPP.Models;

public interface INavigationPageModule
{
    NavigationPage Page { get; }
}

public interface INavigationPageLifecycle
{
    void OnPageEnter();
    void OnPageExit();
}

public sealed class NavigationPageModule<TView, TViewModel> : INavigationPageModule
    where TView : class
    where TViewModel : class
{
    public NavigationPage Page { get; }

    public NavigationPageModule(NavigationPageDefinition definition, Func<TView> viewFactory, TViewModel viewModel)
    {
        Page = new NavigationPage(definition, () => viewFactory(), viewModel);
    }
}
