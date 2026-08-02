using System;
using CommunityToolkit.Mvvm.ComponentModel;
using MainAPP.Models;
using Serilog;

namespace MainAPP.Services;

/// <summary>
/// PLC 连接管理器：封装连接/断开/重连冷却期逻辑，驱动方式为外部定时调用 EnsureConnected()。
/// 连接状态通过 [ObservableProperty] 暴露，可在 UI 中绑定。
/// 每次连接前从 AppSettings 读取最新 PLC 配置，确保配置变更后下次连接生效。
///
/// 线程安全（P1-11）：EnsureConnected 由轮询线程高频调用，MarkDisconnected 由轮询线程在读取失败时调用，
/// Disconnect 由 UI 线程（停机/配置变更）调用。三者读写共享状态（_consecutiveFailures/_disconnectedAt/
/// _totalDisconnectCount/_lastConnectAttempt）并对 _driver 发起连接/断开操作，若不加锁会出现：
/// - EnsureConnected 与 Disconnect 并发：刚 Connect 成功就被 Disconnect 断开，状态错乱
/// - MarkDisconnected 与 EnsureConnected 并发：断开计数/重连冷却期计算不一致
/// _stateLock 只保护连接状态机字段；_driver 的 Configure/Connect/Disconnect 由独立的
/// _driverOperationLock 串行化，避免 PLC 网络超时阻塞状态查询和其他状态变更。
/// </summary>
public partial class PlcConnectionManager : ObservableObject
{
    private readonly IPlcDriver _driver;
    private readonly AppSettings _appSettings;
    private readonly IPlcRuntimeProfileProvider? _profileProvider;
    private DateTime _lastConnectAttempt = DateTime.MinValue;
    private int _consecutiveFailures;

    /// <summary>
    /// 保护连接状态字段与 _driver 连接/断开操作的锁，避免轮询线程与 UI 线程并发操作导致状态错乱。
    /// </summary>
    private readonly object _stateLock = new();
    private readonly object _driverOperationLock = new();

    /// <summary>
    /// 断开起始时刻（重连成功后清空）；用于计算断线时长
    /// </summary>
    private DateTime? _disconnectedAt;

    /// <summary>
    /// 累计断开次数（Growl/Badge 展示用）
    /// </summary>
    private int _totalDisconnectCount;
    public int TotalDisconnectCount
    {
        get => _totalDisconnectCount;
        private set => SetProperty(ref _totalDisconnectCount, value);
    }

    /// <summary>
    /// 最近一次断线时长（重连成功后回填）
    /// </summary>
    public TimeSpan? LastDisconnectDuration { get; private set; }

    public DateTime? DisconnectedAt
    {
        get { lock (_stateLock) return _disconnectedAt; }
    }

    public int ConsecutiveFailures
    {
        get { lock (_stateLock) return _consecutiveFailures; }
    }

    /// <summary>
    /// 连接状态边沿事件：仅在 已连↔断开 切换时触发一次（UI 据此弹 Growl）
    /// </summary>
    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;

    /// <summary>
    /// 基础重试间隔
    /// </summary>
    private static readonly TimeSpan BaseCooldown = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 最大重试间隔
    /// </summary>
    private static readonly TimeSpan MaxCooldown = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 当前重试间隔（指数退避：1s → 2s → 4s → ... → 30s）
    /// </summary>
    private TimeSpan CurrentCooldown =>
        TimeSpan.FromSeconds(Math.Min(
            BaseCooldown.TotalSeconds * Math.Pow(2, _consecutiveFailures),
            MaxCooldown.TotalSeconds));

    /// <summary>
    /// 是否已建立连接
    /// </summary>
    [ObservableProperty]
    private bool _isConnected;

    /// <summary>
    /// 连接状态文本（可绑定到 UI）
    /// </summary>
    [ObservableProperty]
    private string _connectionStatus = "未连接";

    public PlcConnectionManager(
        IPlcDriver driver,
        AppSettings appSettings,
        IPlcRuntimeProfileProvider? profileProvider = null)
    {
        _driver = driver;
        _appSettings = appSettings;
        _profileProvider = profileProvider;
    }

    /// <summary>
    /// 确保连接存活。若未连接且冷却期已过则尝试重连。
    /// 由外部采集循环高频调用（如 200ms），内部自动控制重连频率。
    /// 每次连接前应用最新 PLC 配置。
    /// 线程安全：状态判断与更新使用 _stateLock，驱动 IO 使用独立操作锁。
    /// </summary>
    public void EnsureConnected()
    {
        PlcConfig config;
        DateTime? disconnectedAt;
        TimeSpan cooldown;
        ConnectionStateChangedEventArgs? pendingEvent = null;

        // 状态锁只保护状态机；网络配置和连接操作在锁外执行，避免 PLC 超时阻塞状态查询/停机。
        lock (_stateLock)
        {
            if (IsConnected) return;

            var now = DateTime.Now;
            cooldown = CurrentCooldown;
            if ((now - _lastConnectAttempt) < cooldown) return;

            _lastConnectAttempt = now;
            ConnectionStatus = $"正在连接... (第{_consecutiveFailures + 1}次)";
            config = _profileProvider?.Current.Config ?? _appSettings.PlcConfig.CreateSnapshot();
            disconnectedAt = _disconnectedAt;
        }

        PlcOperationResult result;
        try
        {
            lock (_driverOperationLock)
            {
                _driver.Configure(config);
                result = _driver.Connect();
            }
        }
        catch (Exception ex)
        {
            if (ex is OutOfMemoryException or AppDomainUnloadedException or ThreadAbortException)
                throw;
            Log.Warning(ex, "PLC 连接操作异常");
            result = PlcOperationResult.Fail(ex.Message, PlcErrorClassifier.FromException(ex));
        }

        lock (_stateLock)
        {
            IsConnected = result.IsSuccess;
            if (IsConnected)
            {
                _consecutiveFailures = 0;
                ConnectionStatus = "已连接";

                if (disconnectedAt.HasValue && _disconnectedAt.HasValue)
                {
                    var dur = DateTime.Now - _disconnectedAt.Value;
                    LastDisconnectDuration = dur;
                    Log.Information("PLC 重连成功 {Ip}，断线时长 {Dur:F1}s（累计第 {N} 次断开）",
                        config.IpAddress, dur.TotalSeconds, _totalDisconnectCount);
                    pendingEvent = new ConnectionStateChangedEventArgs
                    {
                        IsConnected = true,
                        Timestamp = DateTime.Now,
                        IpAddress = config.IpAddress,
                        DisconnectDuration = dur,
                        DisconnectCount = _totalDisconnectCount,
                    };
                    _disconnectedAt = null;
                }
            }
            else
            {
                _consecutiveFailures++;
                ConnectionStatus = $"未连接 (重试间隔{cooldown.TotalSeconds:F0}s)";
            }
        }

        if (pendingEvent != null)
            ConnectionStateChanged?.Invoke(this, pendingEvent);
    }

    /// <summary>
    /// 当采集过程中发现读取失败时调用，标记为断开。
    /// 线程安全：状态更新使用 _stateLock，不执行阻塞的驱动断开 IO。
    /// </summary>
    /// <param name="reason">断开原因（结构化枚举，便于 UI/日志按类型过滤）。
    /// 默认 ReadFailure 对应"所有设备读取失败"路径；ScanException 对应 TryScan 捕获通信异常路径。</param>
    public void MarkDisconnected(DisconnectionReason reason = DisconnectionReason.ReadFailure)
    {
        // 事件参数在锁内收集，锁外触发，避免订阅者回调重入 _stateLock 导致状态错乱。
        ConnectionStateChangedEventArgs? pendingEvent = null;

        lock (_stateLock)
        {
            if (!IsConnected) return;
            IsConnected = false;
            ConnectionStatus = "连接断开";

            // 下降沿：仅在此处记录断开时刻、累加次数、触发一次事件
            _disconnectedAt = DateTime.Now;
            TotalDisconnectCount++; // 通过 setter 触发 PropertyChanged，让 UI 绑定收到通知

            // 关键修复：同步推进 _lastConnectAttempt 与 _consecutiveFailures，确保下轮 EnsureConnected 走冷却期重连，
            // 避免在 PLC 端口仍接受连接但读取持续失败的"半连接"场景下，每轮（默认 200ms）形成
            // Connect→ReadFail→MarkDisconnected→Connect 重连风暴冲击 PLC。
            // 旧实现注释中称"不重置 _consecutiveFailures/_lastConnectAttempt"是为避免抖动期绕过冷却期，
            // 但 MarkDisconnected 是"断开"语义而非"主动断开"，理应推进冷却期；主动 Disconnect 路径仍保持原约束。
            _lastConnectAttempt = DateTime.Now;
            _consecutiveFailures = Math.Max(1, _consecutiveFailures);

            var ip = _appSettings.PlcConfig.IpAddress;
            Log.Warning("PLC 断开 {Ip}（第 {N} 次，原因：{Reason}）", ip, _totalDisconnectCount, reason);
            // 锁内仅收集事件参数，锁外触发，避免订阅者回调重入 _stateLock
            pendingEvent = new ConnectionStateChangedEventArgs
            {
                IsConnected = false,
                Timestamp = _disconnectedAt.Value,
                IpAddress = ip,
                Reason = reason,
                DisconnectCount = _totalDisconnectCount,
            };
        }

        // 锁外触发事件：订阅者回调可安全调用 EnsureConnected/Disconnect/MarkDisconnected
        if (pendingEvent != null)
            ConnectionStateChanged?.Invoke(this, pendingEvent);
    }

    /// <summary>
    /// Remote 模式桥接：由 KanbanDataClient 连接状态驱动（不执行真实 PLC IO）。
    /// 供 Collector 采集服务进程连接成功后调用，让 MainWindowViewModel 的全局连接状态横幅复用同一状态机。
    /// </summary>
    public void SyncRemoteConnected(string statusText = "Collector 已连接")
    {
        ConnectionStateChangedEventArgs? pendingEvent = null;
        lock (_stateLock)
        {
            if (IsConnected && ConnectionStatus == statusText) return;
            IsConnected = true;
            ConnectionStatus = statusText;
            _consecutiveFailures = 0;
            _disconnectedAt = null;
            pendingEvent = new ConnectionStateChangedEventArgs
            {
                IsConnected = true,
                Timestamp = DateTime.Now,
                IpAddress = _appSettings.CollectorHubUrl,
            };
        }
        if (pendingEvent != null)
            ConnectionStateChanged?.Invoke(this, pendingEvent);
    }

    /// <summary>
    /// 主动断开连接（UI 线程停机/配置变更时调用）。
    /// 线程安全：状态先切换为未连接，驱动断开操作由独立操作锁串行化。
    /// 设计权衡：不重置 _consecutiveFailures / _lastConnectAttempt，
    /// 避免抖动期通过主动 Disconnect 绕过冷却期立即冲击 PLC（见 Flapping_DisconnectDoesNotResetCooldown_PreventsImmediateRetry 测试）。
    /// 配置变更场景下若需立即重连，应在调用 Disconnect 前先修改 _appSettings.PlcConfig，
    /// EnsureConnected 中的 _driver.Configure 会在下次冷却期过后应用新配置。
    /// _driver.Disconnect 异常被吞掉并记录日志，避免 IPlcDriver 实现抛异常时状态不一致
    /// （IsConnected 保持 true、ConnectionStatus 保持"已连接"）。
    /// </summary>
    public void Disconnect()
    {
        lock (_stateLock)
        {
            IsConnected = false;
            ConnectionStatus = "未连接";
        }

        lock (_driverOperationLock)
        {
            try
            {
                _driver.Disconnect();
            }
            catch (Exception ex)
            {
                // 关键异常重抛，避免吞掉 OutOfMemoryException 等导致后续不可预测行为
                if (ex is OutOfMemoryException or AppDomainUnloadedException or ThreadAbortException)
                    throw;
                Log.Warning(ex, "PlcConnectionManager.Disconnect 调用 _driver.Disconnect 抛异常，忽略并继续清理状态");
            }
        }
    }
}

/// <summary>
/// PLC 断开原因枚举（结构化，便于 UI/日志按类型过滤，优于字符串）。
/// </summary>
public enum DisconnectionReason
{
    /// <summary>所有设备读取失败（默认路径，PollingLoopAsync 中 successDevices.Count==0）</summary>
    ReadFailure,

    /// <summary>TryScan 捕获到通信异常（ScanAlarms/ScanDefects/ScanCountAlarms 抛 IOException/SocketException 等）</summary>
    ScanException,
}

/// <summary>
/// PLC 连接状态边沿事件参数
/// </summary>
public class ConnectionStateChangedEventArgs : EventArgs
{
    /// <summary>切换后的连接状态（true=已连，false=断开）</summary>
    public bool IsConnected { get; init; }

    /// <summary>事件发生时刻</summary>
    public DateTime Timestamp { get; init; }

    /// <summary>PLC IP 地址</summary>
    public string IpAddress { get; init; } = "";

    /// <summary>断开原因（结构化枚举；重连事件为默认值 ReadFailure，无意义）</summary>
    public DisconnectionReason Reason { get; init; }

    /// <summary>断线时长（重连成功时回填）</summary>
    public TimeSpan? DisconnectDuration { get; init; }

    /// <summary>累计断开次数</summary>
    public int DisconnectCount { get; init; }
}
