using System;
using System.Windows.Threading;

namespace MainAPP.Helpers;

/// <summary>
/// 图表重建差分门：记住上一次输入签名，签名无变化时不触发重建（避免每 3s 无意义 new PlotModel）。
/// 解决两个历史问题：① 全量重建 PlotModel 的抖动；② 甘特图高频"改动→重建"风暴。
/// 支持两种模式：
/// - 同步门（debounce 为 null）：输入变化 → 立即执行重建动作；
/// - 防抖门（debounce 非 null）：输入变化 → 防抖调度重建，窗口内再次变化重置计时（一次合并）。
/// 页面门控（_pageActive）语义由调用方在重建动作内自行判断，保持各自页面行为不变。
/// </summary>
public sealed class ChartDiffGate : IDisposable
{
    private readonly Action _rebuild;
    private readonly DispatcherTimer? _debounceTimer;
    private object? _lastSignature;
    private bool _hasSignature;
    private bool _pending;
    private bool _disposed;

    /// <param name="rebuild">输入变化后的重建动作（同步调用或在 UI 线程防抖后调用）。</param>
    /// <param name="debounce">防抖窗口；null 表示同步重建。</param>
    public ChartDiffGate(Action rebuild, TimeSpan? debounce = null)
    {
        _rebuild = rebuild ?? throw new ArgumentNullException(nameof(rebuild));
        if (debounce.HasValue)
        {
            _debounceTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = debounce.Value,
            };
            _debounceTimer.Tick += OnDebounceTick;
        }
    }

    /// <summary>提交本轮输入签名：与上次相同 → 不重建；不同 → 触发重建（同步或防抖调度）。</summary>
    public void Evaluate(object signature)
    {
        if (_disposed) return;
        if (_hasSignature && Equals(signature, _lastSignature))
            return;
        _lastSignature = signature;
        _hasSignature = true;
        RequestRebuild();
    }

    /// <summary>强制重建一轮并刷新缓存签名（忽略历史签名，用于切页进入/设备切换等显式刷新）。</summary>
    public void Force()
    {
        if (_disposed) return;
        _hasSignature = false;
        RequestRebuild();
    }

    /// <summary>丢弃缓存签名与未决重建请求（页面切走时调用，防止退出后重建落地）。</summary>
    public void Reset()
    {
        _hasSignature = false;
        _pending = false;
        _debounceTimer?.Stop();
    }

    private void RequestRebuild()
    {
        if (_debounceTimer != null)
        {
            _pending = true;
            _debounceTimer.Stop();
            _debounceTimer.Start();
        }
        else
        {
            _rebuild();
        }
    }

    private void OnDebounceTick(object? sender, EventArgs e)
    {
        _debounceTimer!.Stop();
        if (_disposed || !_pending) return;
        _pending = false;
        _rebuild();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _debounceTimer?.Stop();
        if (_debounceTimer != null)
            _debounceTimer.Tick -= OnDebounceTick;
    }
}