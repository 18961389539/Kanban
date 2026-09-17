using System.Windows.Threading;

namespace MainAPP.Helpers;

/// <summary>
/// UI 线程调度助手：统一封装 WPF Dispatcher 的线程封送，供 ViewModel/Service 使用，
/// 消除各处重复的 DispatchOnUi / Application.Current 样板（审查 2026-09-17 收敛）。
/// 兼容单元测试宿主：无 Application.Current（或 Dispatcher 已关闭）时按各方法语义
/// 内联执行或静默丢弃，保持与原调用点一致的测试行为。
/// </summary>
public static class UiDispatcher
{
    /// <summary>当前 WPF 应用的 Dispatcher（测试宿主/设计期可能为 null）。</summary>
    public static Dispatcher? CurrentDispatcher => System.Windows.Application.Current?.Dispatcher;

    /// <summary>主窗口（无宿主时可能为 null）。供对话框 Owner 归属等 UI 会话绑定使用。</summary>
    public static System.Windows.Window? MainWindow => System.Windows.Application.Current?.MainWindow;

    /// <summary>是否存在 WPF 应用宿主（区分单元测试宿主与真实运行环境）。</summary>
    public static bool HasWpfAppHost => System.Windows.Application.Current is not null;

    /// <summary>是否运行在活跃的 UI 线程上（无宿主/关闭中/后台线程均返回 false）。</summary>
    public static bool IsOnLiveUiThread
        => CurrentDispatcher is { } d && !d.HasShutdownStarted && d.CheckAccess();

    /// <summary>
    /// 在 UI 线程执行 action：已在 UI 线程或无宿主（测试）时内联执行，后台线程经 BeginInvoke 封送。
    /// 供"结果何时应用均可接受"的调用点使用。
    /// </summary>
    public static void Dispatch(Action action, DispatcherPriority priority = DispatcherPriority.Normal)
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
            dispatcher.BeginInvoke(action, priority);
    }

    /// <summary>
    /// 总是异步投递到 UI 线程（UI 线程上也会推迟到消息队列），Dispatcher 不可用时内联执行。
    /// 供"先完成当前绘制再重算"的调用点使用。
    /// </summary>
    public static void Post(Action action, DispatcherPriority priority = DispatcherPriority.Normal)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted)
        {
            action();
            return;
        }
        dispatcher.BeginInvoke(action, priority);
    }

    /// <summary>
    /// 总是异步投递到 UI 线程，Dispatcher 不可用/已关闭（应用退出、测试宿主）时静默丢弃。
    /// 供"副作用仅在真实运行环境才需要"的调用点使用（如 OnPageEnter 的按需刷新）。
    /// </summary>
    public static void PostOrDrop(Action action, DispatcherPriority priority = DispatcherPriority.Normal)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted) return;
        dispatcher.BeginInvoke(action, priority);
    }

    /// <summary>
    /// 非 UI 线程时把回调封送回 UI 线程并返回 true（调用方应立即 return，避免重复处理）；
    /// 已在 UI 线程或测试宿主时返回 false（调用方原地处理）。
    /// 用于属性变更等"后台线程触发、线程内无法安全处理"的事件处理器入口。
    /// </summary>
    public static bool MarshalIfNeeded(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.CheckAccess())
            return false;
        _ = dispatcher.InvokeAsync(action);
        return true;
    }

    /// <summary>
    /// 非 UI 线程时把回调封送回 UI 线程，Dispatcher 已关闭/无宿主（应用退出、测试）时静默丢弃。
    /// 供"关闭期不应再触碰 UI"的调用点使用（语义与原 DeviceDetailViewModel.DispatchOnUi 一致）。
    /// </summary>
    public static void DispatchOrDrop(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted) return;
        if (dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }

    /// <summary>请求关闭应用（无宿主时无操作）。供 ViewModel 内应用级生命周期操作使用。</summary>
    public static void RequestShutdown() => System.Windows.Application.Current?.Shutdown();
}