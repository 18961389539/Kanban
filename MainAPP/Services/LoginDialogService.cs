using System.Windows;
using MainAPP.Views;
using Microsoft.Extensions.DependencyInjection;

namespace MainAPP.Services;

/// <summary>
/// 用户登录对话框服务。每次调用都从 DI 创建新的登录窗口和 ViewModel，
/// 避免已关闭的 Window 被重复显示，也避免上次登录结果残留。
/// </summary>
public interface ILoginDialogService
{
    /// <summary>显示用户切换对话框；登录成功返回 true，取消返回 false。</summary>
    bool ShowDialog();
}

public sealed class LoginDialogService : ILoginDialogService
{
    private readonly IServiceProvider _serviceProvider;

    public LoginDialogService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public bool ShowDialog()
    {
        var window = _serviceProvider.GetRequiredService<LoginWindow>();
        if (Application.Current?.MainWindow is Window owner)
        {
            window.Owner = owner;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        return window.ShowDialog() == true;
    }
}
