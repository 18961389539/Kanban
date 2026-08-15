namespace MainAPP.Helpers;

/// <summary>
/// UI 线程调度助手：在 WPF Dispatcher 线程执行 action；
/// 无 Dispatcher 或已关闭（单元测试/后台）时同步执行。
/// 供 ViewModel/Service 统一封送跨线程 UI 更新，消除各处重复的 DispatchOnUi 样板。
/// </summary>
public static class UiDispatcher
{
    public static void Dispatch(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted)
        {
            action();
            return;
        }
        if (dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }
}
