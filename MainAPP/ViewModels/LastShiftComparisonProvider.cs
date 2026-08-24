using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Services;
using MainAPP.Helpers;
using MainAPP.Services;

namespace MainAPP.ViewModels;

/// <summary>
/// 主页上班次对比：读取采集服务的内存上班次缓存，无缓存时按 60 秒退避在后台查询历史库回填。
/// 结果通过回调推送给 HomeViewModel（同步缓存路径 + 异步回填路径均已封送回 UI 线程）。
/// </summary>
public sealed class LastShiftComparisonProvider : IDisposable
{
    private static readonly TimeSpan FallbackRetryInterval = TimeSpan.FromSeconds(60);

    private readonly IPlcDataAcquisitionService _plcService;
    private readonly IRuntimeMode _runtimeMode;
    private readonly ProductionHistoryStore? _historyStore;
    private readonly AppSettings _appSettings;

    private readonly Dictionary<string, DateTime> _fallbackAttemptAtByKey = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;

    public LastShiftComparisonProvider(
        IPlcDataAcquisitionService plcService,
        IRuntimeMode runtimeMode,
        ProductionHistoryStore? historyStore,
        AppSettings appSettings)
    {
        _plcService = plcService;
        _runtimeMode = runtimeMode;
        _historyStore = historyStore;
        _appSettings = appSettings;
    }

    public void Refresh(string? deviceId, Action<LastShiftSnapshot> onResult)
    {
        if (string.IsNullOrEmpty(deviceId) || _plcService == null)
        {
            onResult(new LastShiftSnapshot("", 0, 0));
            return;
        }

        var (ok, ng, name) = _plcService.GetLastShiftSummary(deviceId);
        if (!string.IsNullOrEmpty(name))
        {
            onResult(new LastShiftSnapshot(name, ok, ng));
            return;
        }

        if (_historyStore == null || _runtimeMode.IsRemote)
        {
            onResult(new LastShiftSnapshot("", 0, 0));
            return;
        }

        // 兜底查询在后台线程执行（本地模式 SQLite 24 小时历史查询，不能在 UI 线程同步跑）。
        var now = DateTime.Now;
        var currentShift = ShiftConfigResolver.ResolveCurrentShift(_appSettings.Shifts, now).Shift;
        var fallbackKey = BuildFallbackKey(deviceId, currentShift?.Name);
        if (_fallbackAttemptAtByKey.TryGetValue(fallbackKey, out var lastAttempt)
            && now - lastAttempt < FallbackRetryInterval)
        {
            onResult(new LastShiftSnapshot("", 0, 0));
            return;
        }
        _fallbackAttemptAtByKey[fallbackKey] = now;

        _cts?.Cancel();
        _cts?.Dispose();
        var cts = _cts = new CancellationTokenSource();
        var token = cts.Token;

        Task.Run(() =>
        {
            ProductionLog? lastOther;
            try
            {
                var logs = _historyStore.QueryProductionLogs(now.AddDays(-1), now, deviceId);
                lastOther = FindLastOtherShiftLog(logs, currentShift?.Name);
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "上班次产量回填查询失败（设备 {DeviceId}）", deviceId);
                return;
            }
            if (token.IsCancellationRequested) return;
            UiDispatcher.Dispatch(() =>
            {
                if (token.IsCancellationRequested) return;
                onResult(lastOther != null
                    ? new LastShiftSnapshot(lastOther.ShiftName, lastOther.OkProduction, lastOther.NgProduction)
                    : new LastShiftSnapshot("", 0, 0));
            });
        }, token).Forget();
    }

    /// <summary>取消在途兜底查询（设备切换时调用）。</summary>
    public void Cancel()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose() => Cancel();

    internal static string BuildFallbackKey(string deviceId, string? shiftName)
        => $"{deviceId}|{shiftName ?? string.Empty}";

    /// <summary>
    /// 在日志列表中找"时间上最近的、班次不同于当前班次"的最后一条快照（上班次产量回填用）。
    /// currentShiftName 为 null（无班次配置）时退化为取最近一条。
    /// </summary>
    internal static ProductionLog? FindLastOtherShiftLog(
        IReadOnlyList<ProductionLog> logs,
        string? currentShiftName)
        => logs
            .OrderByDescending(log => log.Timestamp)
            .FirstOrDefault(log => currentShiftName == null || log.ShiftName != currentShiftName);
}

/// <summary>上班次对比结果（无数据时 Name 为空）。</summary>
public sealed record LastShiftSnapshot(string Name, int Ok, int Ng);
