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

    public NavigationPageModule(
        NavigationPageDefinition definition,
        Func<TView> viewFactory,
        Func<TViewModel> viewModelFactory)
    {
        // ViewModel 延迟到页面进入/可见时创建（NavigationPage 内部 Lazy），
        // 避免 MainWindow 构造时全量实例化 10+ 页面 ViewModel 拖慢启动。
        Page = new NavigationPage(definition, () => viewFactory(), () => viewModelFactory());
    }
}
