using System.ComponentModel;
using Kanban.Collector.Core.Models;

namespace MainAPP.Models;

public sealed class NavigationPageDefinition
{
    public required string Key { get; init; }
    public required int Index { get; init; }
    public required NavItem NavItem { get; init; }
    public bool ShowInSidebar { get; init; }

    /// <summary>
    /// 访问此页面所需的最低角色。null 表示任何角色（含未登录）均可访问。
    /// 默认 null：展示类页面（首页/产线/报警中心/设备详情）对所有人开放。
    /// </summary>
    public UserRole? RequiredRole { get; init; }
}

public sealed class NavigationPage : INotifyPropertyChanged
{
    private readonly Lazy<object> _view;
    private readonly Lazy<object> _viewModel;
    private bool _isCurrent;

    public NavigationPageDefinition Definition { get; }
    public object? View => _view.IsValueCreated ? _view.Value : null;

    /// <summary>
    /// 页面 ViewModel（懒加载：首次访问时才创建）。
    /// 触发点：NavigationPageHost 可见时（EnsureViewLoaded 赋 DataContext）与 MainWindow.ActivatePage
    /// （导航时取生命周期实例）——两者都只发生在页面进入/可见时，隐藏页不会提前构造 ViewModel。
    /// </summary>
    public object ViewModel => _viewModel.Value;

    /// <summary>View 是否已创建。可与 <see cref="IsCurrent"/> 对照：当前页但尚未创建 = 还在宿主 Loaded 延迟中。</summary>
    public bool IsViewCreated => _view.IsValueCreated;

    public NavigationPage(NavigationPageDefinition definition, Func<object> viewFactory, Func<object> viewModelFactory)
    {
        Definition = definition;
        _view = new Lazy<object>(viewFactory, LazyThreadSafetyMode.ExecutionAndPublication);
        _viewModel = new Lazy<object>(viewModelFactory, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>是否已创建 ViewModel（MainWindowViewModel 取消事件订阅时避免触发懒创建）。</summary>
    public bool IsViewModelCreated => _viewModel.IsValueCreated;

    public object EnsureView() => _view.Value;

    /// <summary>
    /// 是否当前选中页（由 MainWindowViewModel.SelectedIndex 驱动）。host 的 Visibility 直接绑这个属性，
    /// 避免在 DataTemplate / ItemContainerStyle 内部使用 RelativeSource 或 ElementName 跨模板 NameScope 解析。
    /// </summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value) return;
            _isCurrent = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrent)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}