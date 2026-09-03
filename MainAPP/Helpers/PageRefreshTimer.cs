using System;
using System.Windows.Threading;

namespace MainAPP.Helpers;

/// <summary>
/// 统一 UI 定时刷新入口：封装 <see cref="DispatcherTimer"/>，固定 <see cref="DispatcherPriority.Background"/>
/// （避免个别页面漏配 Background 导致渲染优先级过高、甘特图/大表格滚动卡顿——历史问题复盘）。
/// 幂等 Start/Stop、tick 防重入、Stop/Dispose 后丢弃迟到回调；退出/释放一律走 Dispose 解绑。
/// 每页保持自己的节拍与页面生命周期（INavigationPageLifecycle）门控语义，仅替换"裸 new DispatcherTimer"。
/// </summary>
public sealed class PageRefreshTimer : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly Action _onTick;
    private bool _tickRunning;
    private bool _disposed;

    /// <param name="interval">刷新节拍。</param>
    /// <param name="onTick">tick 回调（UI 线程）。</param>
    public PageRefreshTimer(TimeSpan interval, Action onTick)
    {
        _onTick = onTick;
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = interval,
        };
        _timer.Tick += HandleTick;
    }

    public TimeSpan Interval
    {
        get => _timer.Interval;
        set => _timer.Interval = value;
    }

    public bool IsEnabled => _timer.IsEnabled;

    /// <summary>启动（幂等：已在运行则无操作）。</summary>
    public void Start()
    {
        if (_disposed || _timer.IsEnabled) return;
        _timer.Start();
    }

    /// <summary>停止（幂等）；已排队但尚未执行的迟到 tick 由 HandleTick 的 disposed 检查丢弃。</summary>
    public void Stop() => _timer.Stop();

    /// <summary>先停后启（节拍内自重启场景，如 KPI 刷新后复位）。</summary>
    public void Restart()
    {
        Stop();
        Start();
    }

    private void HandleTick(object? sender, EventArgs e)
    {
        if (_disposed) return;
        // 防重入：上一 tick 回调尚未返回（极端重入/超时），丢弃本次，避免堆叠
        if (_tickRunning) return;
        _tickRunning = true;
        try
        {
            _onTick?.Invoke();
        }
        finally
        {
            _tickRunning = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= HandleTick;
    }
}