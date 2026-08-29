using Kanban.Contracts.Dtos;
using Kanban.Contracts.Enums;

namespace PlcSimulator;

/// <summary>
/// 单台设备的模拟状态机：独立维护运行/报警/待机状态、按节拍产出、随机触发报警并自动恢复。
///
/// 真实度提升点（v2 改造）：
/// - 节拍基于 RecipeValue（件/h）计算，带 ±10% 随机波动
/// - 班次曲线：按本地时间时段调整节拍（上午高峰/午休/晚间疲劳/夜班），模拟人工产能曲线
/// - 节拍漂移：每次产出后节拍渐慢（模拟刀具磨损），报警恢复时回收部分漂移（模拟维护）
/// - 突发不良期：随机进入 30-120s 的高 NG 率期（模拟材料批次问题），结束后恢复正常
/// - NG 率分级：正常运行 1-3%、报警期 15-30%、突发期 30-60%
/// - CounterAlarm 阈值触发：连续不良/停机次数超 MaxValue 时自动触发报警位
/// - 启动期状态恢复：从 PLC 读取现有产量/状态，不覆盖 MainAPP 已有数据
/// - 内存维护计数器，直接写入绝对值，避免 Read-Modify-Write 竞态
///
/// 状态简化（v3）：注塑机等真实设备通常只有运行/待机/报警三态，无独立暂停状态。
/// 操作员暂停（午休/换模/抽检等）和缺料停机统一使用 Idle(3) 表示，MainAPP 据此显示"待机"。
/// Idle=3 与 MainAPP DeviceStatus.Paused=3 对齐（MainAPP 中 Paused 语义即为"待机"）。
/// </summary>
public class DeviceSimulator
{
    /// <summary>模拟状态枚举，值与 PLC 状态字一致：0=离线, 1=运行, 2=报警, 3=待机</summary>
    public enum SimStatus
    {
        Offline = 0,
        Running = 1,
        Alarm = 2,
        Idle = 3,
    }

    private readonly DeviceConfig _config;
    private readonly ScenarioConfig _scenario;
    /// <summary>
    /// 基础节拍（秒/件），由 RecipeValue 计算：3600 / RecipeValue。
    /// 非 readonly：MainAPP 可通过 WriteRecipeAsync 修改 PLC 中的 RecipeAddress，
    /// Simulator 需在 RestoreFromPlc 和 Tick 中动态读取并更新此值，使节拍随配方变化。
    /// </summary>
    private double _baseCycleSeconds;
    private readonly double _speedMultiplier;
    /// <summary>随机源（默认 Random.Shared；测试注入种子以实现确定性驱动，审查修复 2026-08-16）。</summary>
    private readonly Random _rng;
    /// <summary>
    /// 上次读取配方值的时刻，用于限制 Tick 中的读取频率（避免每 100ms 都读 PLC）。
    /// </summary>
    private DateTime _lastRecipeCheckTime = DateTime.MinValue;

    // IO 回调（由 Program 提供，内部含锁和日志）
    private readonly Action<string, int> _writeInt;
    private readonly Action<string, bool> _writeBool;
    private readonly Action<string, float> _writeFloat;
    private readonly Action<string, string> _writeString;
    private readonly Func<string, int> _readInt;
    private readonly Func<string, bool> _readBool;

    /// <summary>
    /// 状态锁：保护所有可变状态字段，防止命令线程（主线程）与 Tick 线程（后台线程）并发修改。
    /// 锁获取顺序始终为 _stateLock → _ioLock（Tick 持 _stateLock 时通过回调获取 _ioLock），
    /// 不会反向获取，故无死锁风险。
    /// </summary>
    private readonly object _stateLock = new();

    // 状态
    public SimStatus Status { get; private set; } = SimStatus.Idle;
    /// <summary>断线仿真进入离线前保存的运行态，用于恢复时写回真实状态字。</summary>
    private SimStatus? _statusBeforeOffline;
    private DateTime _nextProduceTime;
    private DateTime? _alarmEndTime;
    private DateTime _alarmStartTime;   // 报警开始时间（用于恢复时计算实际持续时长）
    private string? _activeAlarmName;
    /// <summary>当前触发的报警位 PLC 地址（恢复时只清此位，避免遍历 200 个报警位全写 false）。</summary>
    private string? _activeAlarmAddress;

    // 突发不良期状态
    private DateTime? _burstEndTime;

    // 节拍漂移（累积，0 ~ CycleDriftMax）
    private double _cycleDrift;

    // ── 数据源模拟 ──
    private readonly int _registerOffset;
    private int _temperature = 245;              // 温度 ×10（245 = 24.5℃）
    private bool _tempOverLimit;                 // 是否处于模拟越限窗口
    private DateTime _nextTempEventTime = DateTime.MinValue;
    private readonly Dictionary<string, SourceTriggerState> _sourceTriggerStates = new(StringComparer.OrdinalIgnoreCase);

    // 数据源模拟地址约定（三菱 D 地址，MelsecMcServer 任意 D 字可读写）：
    // D(502+off)=温度(×10, Int32)、D(504+off)=湿度(×10, Int32)、D(510+off)=温度采集触发命令字
    // 注意：各值寄存器间隔 2 字——Int32 读会拼接相邻字（低地址=低 16 位），相邻字留空避免值污染。
    private int TemperatureAddress => 502 + _registerOffset;
    private int HumidityAddress => 504 + _registerOffset;
    private int TriggerAddress => 510 + _registerOffset;
    private const int TriggerAckValue = 2;

    // 开机预热：启动后前 WarmupPieces 件使用高 NG 率 + 慢节拍
    private int _warmupProduced;

    // 缺料停机：运行中随机进入缺料期，期间不产出，可选触发缺料报警位
    private DateTime? _shortageEndTime;
    private string? _shortageAlarmName;  // 关联的缺料报警位名称（如有）
    private string? _shortageAlarmAddress;  // 关联的缺料报警位 PLC 地址（恢复时直接清此位）

    // 内存计数器（避免每次产出都 ReadInt，减少 IO 和竞态）
    private int _okCount;
    private int _ngCount;
    private int _consecutiveNg;
    private int _stopCount;
    private bool _stopAlarmTriggered;  // 停机次数达阈值后只触发一次，避免重复报警
    private readonly Dictionary<string, int> _defectCounts = new();

    // ── 操作员行为状态 ──
    private DateTime? _operatorPauseEndTime;
    private string? _operatorPauseReason;
    private DateTime _lastMoldChangeTime;
    private int _piecesSinceLastQualityCheck;
    /// <summary>午休去重键（yyyyMMdd），同一天只触发一次午休。</summary>
    private string _lastLunchKey = "";
    /// <summary>交接班去重键（yyyyMMdd-HH），每个班次开始小时只触发一次。</summary>
    private string _lastShiftChangeKey = "";

    // ── 产量曲线波动 ──
    private DateTime _runSessionStartTime;  // 本次连续运行开始时间（用于疲劳曲线）
    private int _piecesSinceLastMoldChange; // 换模后产出的件数（用于批次效应）
    private int _burstStallRemaining;       // 剩余突发卡顿件数

    // ── 批量更新（PLC 中的值可能滞后于内存真实值）──
    private int _plcOkCount;
    private int _plcNgCount;
    /// <summary>上次写入 PLC 的连续不良值（批量更新，避免每件产品写 100 个计数报警地址）。</summary>
    private int _plcConsecutiveNg;

    // ── 物料批次波动（每 2-4 小时切换批次，不同批次 NG 率基线不同）──
    private DateTime? _materialBatchEndTime;
    private double _materialBatchNgRate;  // 当前批次的 NG 率基线

    // ── 设备老化（累计运行小时数，超阈值后 NG 率上升、漂移加快）──
    private double _totalRunHours;        // 本次会话累计运行小时数
    private DateTime _lastRunHoursUpdateTime;  // 上次累计运行时间更新时刻

    // ── 工单赶工（周期性目标，达到 90% 后节拍加快、NG 率上升）──
    private int _workOrderProduced;       // 当前工单已产出件数（OK+NG）

    // ── 首件检验（换模/冷启动后标记，等待触发强制待机）──
    private bool _pendingFirstArticleInspection;

    // ── 报警恢复爬坡（前 N 件节拍慢、NG 率略高）──
    private int _postAlarmRampupRemaining;

    // ── PLC 通信抖动（偶发篡改 PLC 内存值，模拟电磁干扰）──
    private DateTime? _commJitterEndTime;
    private string? _commJitterAddress;

    /// <summary>日志回调：状态变化、报警触发/恢复、产出等事件通过此回调输出。</summary>
    public event Action<string>? Log;

    public string Name => _config.Name;
    public DeviceConfig Config => _config;
    public int OkCount => _okCount;
    public int NgCount => _ngCount;
    public int ConsecutiveNg => _consecutiveNg;
    public int StopCount => _stopCount;
    public bool BurstActive => _burstEndTime.HasValue && DateTime.UtcNow < _burstEndTime.Value;
    public double CycleDrift => _cycleDrift;
    public bool WarmupActive => _scenario.EnableWarmup && _warmupProduced < _scenario.WarmupPieces;
    public bool ShortageActive => _shortageEndTime.HasValue && DateTime.UtcNow < _shortageEndTime.Value;
    public bool PostAlarmRampupActive => _postAlarmRampupRemaining > 0;
    public bool DeepNightActive => IsDeepNightHour(DateTime.UtcNow.ToLocalTime().Hour);
    public bool ProductionPressureActive => IsProductionPressureActive();
    public double MaterialBatchNgRate => _materialBatchNgRate;
    public double TotalRunHours => _totalRunHours;
    public bool EquipmentAgingActive => _scenario.EnableEquipmentAging && _totalRunHours >= _scenario.AgingThresholdHours;

    private sealed class SourceTriggerState
    {
        public bool AwaitingAck { get; set; }
        public DateTime TriggerSetTime { get; set; }
        public DateTime NextTriggerTime { get; set; } = DateTime.MinValue;
    }

    public DeviceSimulator(DeviceConfig config, ScenarioConfig scenario, double speedMultiplier,
        Action<string, int> writeInt, Action<string, bool> writeBool,
        Func<string, int> readInt, Func<string, bool> readBool, Random? rng = null,
        int registerOffset = 0, Action<string, float>? writeFloat = null,
        Action<string, string>? writeString = null)
    {
        _config = config;
        _scenario = scenario;
        _speedMultiplier = speedMultiplier;
        // 随机源可注入种子（审查修复 2026-08-16，配合单测确定性驱动），默认 Random.Shared
        _rng = rng ?? Random.Shared;
        // 数据源模拟寄存器偏移：多台设备共享同一 PLC 时按设备索引错开地址，避免触发握手互相打架。
        // 约定（三菱 D 地址）：D(502+off)=温度(×10) D(503+off)=湿度(×10) D(510+off)=温度采集触发命令字
        _registerOffset = registerOffset;
        // RecipeValue=50 表示 50件/h，节拍 = 3600/50 = 72秒/件
        _baseCycleSeconds = config.RecipeValue > 0 ? 3600.0 / config.RecipeValue : 60.0;
        _writeInt = writeInt;
        _writeBool = writeBool;
        _writeFloat = writeFloat ?? NoopFloatWrite;
        _writeString = writeString ?? NoopStringWrite;
        _readInt = readInt;
        _readBool = readBool;
    }

    /// <summary>
    /// 初始化 PLC 内存中的所有地址。
    /// 当 <paramref name="restoreFromPlc"/> 为 true 时，从 PLC 读取现有值作为起始状态，
    /// 不覆盖 MainAPP 已写入的数据；读取失败或值为 0 时使用默认值。
    /// </summary>
    public void Initialize(bool restoreFromPlc)
    {
        if (restoreFromPlc)
        {
            RestoreFromPlc();
        }
        else
        {
            InitializeFresh();
        }
    }

    /// <summary>从 PLC 读取现有值恢复状态（启动期状态保持）。</summary>
    private void RestoreFromPlc()
    {
        _okCount = TryReadInt(_config.OkCountAddress);
        _ngCount = TryReadInt(_config.NgCountAddress);
        _consecutiveNg = 0;  // 连续不良不恢复（重启后清零，避免误触发阈值）
        _stopCount = 0;
        _stopAlarmTriggered = false;
        _warmupProduced = 0;       // 重启后重新预热（模具可能已冷却）
        _shortageEndTime = null;
        _shortageAlarmName = null;
        _shortageAlarmAddress = null;

        // 恢复停机次数（从首个停机类 CounterAlarm 读取，多个停机类时仅用第一个，避免互相覆盖）
        var stopCounterAlarm = _config.CounterAlarms.FirstOrDefault(ca => ca.Enabled && IsStopCounterAlarm(ca));
        if (stopCounterAlarm != null)
        {
            _stopCount = TryReadInt(stopCounterAlarm.PlcAddress);
        }

        // 恢复缺陷计数
        _defectCounts.Clear();
        foreach (var d in _config.Defects)
        {
            _defectCounts[d.PlcAddress] = TryReadInt(d.PlcAddress);
        }

        // 恢复状态字（1=运行, 2=报警, 3=待机；0 或其他值视为待机）
        // 修复（2026-08-16，审查 H3）：用可空读区分「读取失败」与「真实值 0」——
        // 读取失败时不能按 0 恢复并回写状态字，否则 client 模式断线会把真实 PLC 状态字改写为待机。
        var statusRead = TryReadIntNullable(_config.StatusCountAddress);
        var statusReadOk = statusRead.HasValue;
        Status = statusRead switch
        {
            (int)SimStatus.Running => SimStatus.Running,
            (int)SimStatus.Alarm => SimStatus.Alarm,
            (int)SimStatus.Offline => SimStatus.Offline,
            (int)SimStatus.Idle => SimStatus.Idle,
            _ => SimStatus.Idle,
        };

        // 报警位状态：如果任意报警位为 ON，强制进入报警态。
        // 修复（2026-08-16，审查 M4）：全量扫描所有报警位，不再仅扫前 10 个——
        // 否则第 11+ 个报警位为 ON 时会误判为待机，且该 M 位因不在扫描范围而永不被清除（孤立报警位）。
        if (statusReadOk && Status != SimStatus.Alarm)
        {
            foreach (var a in _config.Alarms)
            {
                if (TryReadBool(a.PlcAddress))
                {
                    Status = SimStatus.Alarm;
                    _activeAlarmName = a.Name;
                    _activeAlarmAddress = a.PlcAddress;
                    break;
                }
            }
        }

        // 报警态恢复：给一个短恢复时间（10s），避免重启后卡在报警态
        if (Status == SimStatus.Alarm)
        {
            // 同步设置 _alarmStartTime，否则 ProcessExpirations 中 (now - _alarmStartTime) 会算出垃圾时长
            _alarmStartTime = DateTime.UtcNow.AddSeconds(-10);
            _alarmEndTime = DateTime.UtcNow.AddSeconds(10);
        }

        // 恢复配方（防止 MainAPP 已修改配方，Simulator 还在用旧值）；PLC 中无值时写默认值
        TryUpdateRecipeFromPlc(writeDefaultIfZero: true);

        // 操作员行为 / 曲线波动 / 批量更新状态（恢复时从当前时刻开始计）
        _operatorPauseEndTime = null;
        _operatorPauseReason = null;
        _lastMoldChangeTime = DateTime.UtcNow;
        _piecesSinceLastQualityCheck = 0;
        _lastLunchKey = "";
        _lastShiftChangeKey = "";
        _runSessionStartTime = DateTime.UtcNow;
        _piecesSinceLastMoldChange = 0;
        _burstStallRemaining = 0;
        // PLC 中的产量计数与内存同步（恢复时两者一致，后续批量更新才会产生差异）
        _plcOkCount = _okCount;
        _plcNgCount = _ngCount;
        _plcConsecutiveNg = 0;  // 连续不良不恢复（重启后清零）

        // 新特性状态初始化（恢复时从当前时刻开始计）
        StartNewMaterialBatch(DateTime.UtcNow);
        _totalRunHours = 0;
        _lastRunHoursUpdateTime = DateTime.UtcNow;
        _workOrderProduced = 0;
        _pendingFirstArticleInspection = false;  // 恢复时不强制首件检验
        _postAlarmRampupRemaining = 0;
        _commJitterEndTime = null;
        _commJitterAddress = null;

        // 运行态：安排下次产出
        if (Status == SimStatus.Running)
        {
            ScheduleNextProduce(DateTime.UtcNow);
        }

        // 同步状态字（确保 PLC 中状态与 Simulator 一致）
        // 修复（2026-08-16，审查 H3）：仅当状态字读取成功时才回写，避免读取失败时把真实 PLC 状态字改写为待机。
        if (statusReadOk)
        {
            WriteStatus();
        }
        else
        {
            Log?.Invoke($"[{Name}] 状态恢复：PLC 读取失败（{_config.StatusCountAddress}），跳过状态同步");
        }

        Log?.Invoke($"[{Name}] 状态恢复：Status={StatusText(Status)}, OK={_okCount}, NG={_ngCount}, 停机={_stopCount}");
    }

    /// <summary>全新初始化：所有地址写默认值（产量归零、状态待机、报警 OFF）。</summary>
    private void InitializeFresh()
    {
        _okCount = 0;
        _ngCount = 0;
        _consecutiveNg = 0;
        _stopCount = 0;
        _stopAlarmTriggered = false;
        _cycleDrift = 0;
        _burstEndTime = null;
        _warmupProduced = 0;
        _shortageEndTime = null;
        _shortageAlarmName = null;
        _shortageAlarmAddress = null;
        _defectCounts.Clear();
        foreach (var d in _config.Defects)
            _defectCounts[d.PlcAddress] = 0;

        // 操作员行为 / 曲线波动 / 批量更新状态
        _operatorPauseEndTime = null;
        _operatorPauseReason = null;
        _lastMoldChangeTime = DateTime.UtcNow;
        _piecesSinceLastQualityCheck = 0;
        _lastLunchKey = "";
        _lastShiftChangeKey = "";
        _runSessionStartTime = DateTime.UtcNow;
        _piecesSinceLastMoldChange = 0;
        _burstStallRemaining = 0;
        _plcOkCount = 0;
        _plcNgCount = 0;
        _plcConsecutiveNg = 0;

        // 新特性状态初始化（全新初始化时启动首个物料批次）
        StartNewMaterialBatch(DateTime.UtcNow);
        _totalRunHours = 0;
        _lastRunHoursUpdateTime = DateTime.UtcNow;
        _workOrderProduced = 0;
        _pendingFirstArticleInspection = false;
        _postAlarmRampupRemaining = 0;
        _commJitterEndTime = null;
        _commJitterAddress = null;

        // 先设置内存状态，再写 PLC，保证内存与 PLC 一致（与 RestoreFromPlc 顺序统一）
        Status = SimStatus.Idle;
        _alarmEndTime = null;
        _activeAlarmName = null;
        _activeAlarmAddress = null;

        WriteIfNotEmpty(_config.OkCountAddress, 0);
        WriteIfNotEmpty(_config.NgCountAddress, 0);
        WriteIfNotEmpty(_config.RecipeAddress, _config.RecipeValue);
        foreach (var a in _config.Alarms)
            WriteBoolIfNotEmpty(a.PlcAddress, false);
        foreach (var d in _config.Defects)
            WriteIfNotEmpty(d.PlcAddress, 0);
        foreach (var ca in _config.CounterAlarms)
            if (ca.Enabled)
                WriteIfNotEmpty(ca.PlcAddress, 0);

        // 状态字最后写（与 RestoreFromPlc 一致，确保其他地址已就位后再切状态）
        WriteStatus();
    }

    /// <summary>断线仿真：将状态字写为 0（离线），冻结状态机直至 <see cref="ExitSimulatedOffline"/>。</summary>
    public void EnterSimulatedOffline()
    {
        lock (_stateLock)
        {
            if (Status == SimStatus.Offline) return;
            _statusBeforeOffline = Status;
            Status = SimStatus.Offline;
            WriteStatus();
            Log?.Invoke($"[{Name}] 断线仿真 → 状态字=0（离线）");
        }
    }

    /// <summary>断线仿真结束：恢复断线前的状态字（1/2/3）。</summary>
    public void ExitSimulatedOffline(DateTime now)
    {
        lock (_stateLock)
        {
            if (Status != SimStatus.Offline) return;
            var restore = _statusBeforeOffline ?? SimStatus.Idle;
            _statusBeforeOffline = null;
            Status = restore;
            WriteStatus();
            if (restore == SimStatus.Running)
                ScheduleNextProduce(now);
            Log?.Invoke($"[{Name}] 断线恢复 → {StatusText(restore)}（状态字={(int)restore}）");
        }
    }

    /// <summary>启动设备：从待机进入运行，开始按节拍产出。</summary>
    public void Start(DateTime now)
    {
        lock (_stateLock)
        {
            if (Status == SimStatus.Offline || Status == SimStatus.Running) return;
            // 手动启动时清除操作员暂停/缺料停机定时器，避免状态字已改 Running
            // 但定时器仍保留导致 ProcessExpirations 误判"恢复运行"并重复写状态字
            if (_operatorPauseEndTime.HasValue || _shortageEndTime.HasValue)
            {
                _operatorPauseEndTime = null;
                _operatorPauseReason = null;
                _shortageEndTime = null;
                _shortageAlarmName = null;
                _shortageAlarmAddress = null;
            }
            var wasIdle = Status == SimStatus.Idle;
            Status = SimStatus.Running;
            WriteStatus();
            _runSessionStartTime = now;
            // 已完成预热的设备保持原状（如待机→运行），否则重新进入预热
            if (_warmupProduced < _scenario.WarmupPieces)
            {
                _warmupProduced = 0;
            }
            // 冷启动（待机→运行）触发首件检验标记，Tick 中检测到标记后触发强制待机
            if (wasIdle && _scenario.EnableFirstArticleInspection)
            {
                _pendingFirstArticleInspection = true;
            }
            ScheduleNextProduce(now);
            var warmupHint = WarmupActive ? $"，预热期{_scenario.WarmupPieces}件" : "";
            var faiHint = _pendingFirstArticleInspection ? "，待首件检验" : "";
            Log?.Invoke($"[{Name}] 启动 → 运行（节拍 {_baseCycleSeconds / _speedMultiplier:F1}s/件{warmupHint}{faiHint}）");
        }
    }

    /// <summary>暂停设备：运行中→待机，停机次数 +1。用于 stop/pause 命令。</summary>
    public void Pause(DateTime now)
    {
        lock (_stateLock)
        {
            if (Status != SimStatus.Running) return;
            FlushPendingBatch();
            Status = SimStatus.Idle;
            WriteStatus();

            // 停机次数 +1（匹配停机类计数报警，累积不清零）
            foreach (var ca in _config.CounterAlarms)
            {
                if (!ca.Enabled) continue;
                if (IsStopCounterAlarm(ca))
                {
                    _stopCount++;
                    WriteIfNotEmpty(ca.PlcAddress, _stopCount);
                }
            }
            Log?.Invoke($"[{Name}] 运行 → 待机（停机次数={_stopCount}）");

            // 检查停机次数阈值
            if (_scenario.EnableCounterAlarmThreshold)
                CheckStopCountThreshold(now);
        }
    }

    /// <summary>恢复设备：待机→运行。</summary>
    public void Resume(DateTime now)
    {
        lock (_stateLock)
        {
            if (Status != SimStatus.Idle) return;
            // 手动恢复时清除操作员暂停/缺料停机定时器，避免 ProcessExpirations 重复触发恢复逻辑
            if (_operatorPauseEndTime.HasValue || _shortageEndTime.HasValue)
            {
                _operatorPauseEndTime = null;
                _operatorPauseReason = null;
                _shortageEndTime = null;
                _shortageAlarmName = null;
                _shortageAlarmAddress = null;
            }
            Status = SimStatus.Running;
            WriteStatus();
            ScheduleNextProduce(now);
            Log?.Invoke($"[{Name}] 待机 → 运行");
        }
    }

    /// <summary>手动触发报警：运行中→报警，随机选一个报警位 ON。</summary>
    public void TriggerAlarm(DateTime now)
    {
        lock (_stateLock)
        {
            if (Status != SimStatus.Running) return;
            DoTriggerAlarm(now, manual: true);
        }
    }

    /// <summary>归零所有产量/缺陷/连续不良计数（不清零停机次数）。</summary>
    public void ResetCounts()
    {
        lock (_stateLock)
        {
            _okCount = 0;
            _ngCount = 0;
            _plcOkCount = 0;
            _plcNgCount = 0;
            _consecutiveNg = 0;
            _plcConsecutiveNg = 0;
            _defectCounts.Clear();
            foreach (var d in _config.Defects)
                _defectCounts[d.PlcAddress] = 0;
            // 工单赶工计数同步重置（新工单从 0 开始）
            _workOrderProduced = 0;

            WriteIfNotEmpty(_config.OkCountAddress, 0);
            WriteIfNotEmpty(_config.NgCountAddress, 0);
            foreach (var d in _config.Defects)
                WriteIfNotEmpty(d.PlcAddress, 0);
            // 连续不良类计数清零，停机次数不清零
            foreach (var ca in _config.CounterAlarms)
            {
                if (ca.Enabled && !IsStopCounterAlarm(ca))
                    WriteIfNotEmpty(ca.PlcAddress, 0);
            }
            // 清零后重置阈值触发标记，允许再次触发
            _stopAlarmTriggered = false;
            Log?.Invoke($"[{Name}] 产量/缺陷/连续不良已归零（停机次数={_stopCount} 保留）");
        }
    }

    /// <summary>
    /// 主循环 Tick：由 Program 每 100ms 调用一次。
    /// 处理：报警恢复、突发不良期检查/退出、缺料停机进入/退出、随机报警触发、节拍到期产出、阈值检查。
    /// 新特性：物料批次切换、设备老化累积、PLC 通信抖动、首件检验触发、报警恢复爬坡递减。
    /// </summary>
    public void Tick(DateTime now)
    {
        lock (_stateLock)
        {
            if (Status == SimStatus.Offline)
                return;

            // 阶段 1：状态过期/累积（报警恢复、突发期/缺料/操作员暂停退出、批次切换等）
            ProcessExpirations(now);

            // 阶段 1.5：数据源模拟（环境数据不受产线状态影响，独立于缺料/暂停）
            SimulateDataSources(now);

            // 阶段 2：缺料/操作员暂停期间不产出、不触发事件
            if (ShortageActive || (_operatorPauseEndTime.HasValue && now < _operatorPauseEndTime.Value))
                return;

            // 阶段 3：随机事件触发（仅运行态）+ 节拍产出 + 配方同步
            ProcessRunningStateEvents(now);
        }
    }

    /// <summary>
    /// 阶段 1：处理所有到期状态的过期/累积逻辑。
    /// 包含运行小时数累计、通信抖动/物料批次/报警/突发期/缺料/操作员暂停的退出检查。
    /// </summary>
    private void ProcessExpirations(DateTime now)
    {
        // 0. 累计运行小时数（仅运行态，用于设备老化）
        AccumulateRunHours(now);

        // 0.5 PLC 通信抖动到期检查（异常值保持时间结束，将内存正确值写回 PLC 修正）
        // 不依赖"下次正常写入"，因为设备可能在抖动期间进入待机/报警而不再产出，
        // 导致 PLC 中残留异常值，MainAPP 据此误判缺陷/计数报警。
        if (_commJitterEndTime.HasValue && now >= _commJitterEndTime.Value)
        {
            if (!string.IsNullOrEmpty(_commJitterAddress))
            {
                // 区分缺陷计数与计数报警地址，写回对应的内存正确值
                var defect = _config.Defects.FirstOrDefault(d => d.PlcAddress == _commJitterAddress);
                if (defect != null)
                {
                    WriteIfNotEmpty(_commJitterAddress,
                        _defectCounts.TryGetValue(_commJitterAddress, out var cnt) ? cnt : 0);
                }
                else
                {
                    var counterAlarm = _config.CounterAlarms.FirstOrDefault(ca => ca.Enabled && ca.PlcAddress == _commJitterAddress);
                    if (counterAlarm != null)
                    {
                        int correct = IsStopCounterAlarm(counterAlarm) ? _stopCount : _consecutiveNg;
                        WriteIfNotEmpty(_commJitterAddress, correct);
                    }
                }
            }
            _commJitterEndTime = null;
            _commJitterAddress = null;
        }

        // 0.6 物料批次切换检查（到期时切换新批次，重新随机 NG 率基线）
        if (_scenario.EnableMaterialBatchVariance
            && _materialBatchEndTime.HasValue && now >= _materialBatchEndTime.Value)
        {
            StartNewMaterialBatch(now);
            Log?.Invoke($"[{Name}] 物料批次切换 → 新批次 NG 率基线={_materialBatchNgRate * 100:F2}%");
        }

        // 1. 报警恢复检查
        if (Status == SimStatus.Alarm && _alarmEndTime.HasValue && now >= _alarmEndTime)
        {
            // 仅清除当前触发的报警位（避免遍历 200 个报警位全写 false）
            if (!string.IsNullOrEmpty(_activeAlarmAddress))
                WriteBoolIfNotEmpty(_activeAlarmAddress, false);

            // 报警恢复时回收部分节拍漂移（模拟维护后改善）
            _cycleDrift *= (1.0 - _scenario.DriftRecoverOnAlarm);
            if (_cycleDrift < 0) _cycleDrift = 0;

            // 计数报警恢复：清零连续不良计数器（模拟人工干预后恢复）。
            // 触发时不清零是为了让 MainAPP 有时间检测 IsTriggered=true，
            // 报警恢复时清零让 MainAPP 看到 CurrentValue 回落、IsTriggered=false。
            if (_consecutiveNg > 0)
            {
                _consecutiveNg = 0;
                _plcConsecutiveNg = 0;
                WriteConsecutiveNgToCounterAlarms(0);
            }

            Status = SimStatus.Running;
            WriteStatus();
            ScheduleNextProduce(now);
            var duration = (now - _alarmStartTime).TotalSeconds;
            // 报警恢复爬坡：前 N 件节拍慢、NG 率略高（设备未稳定）
            if (_scenario.EnablePostAlarmRampup)
            {
                _postAlarmRampupRemaining = _scenario.PostAlarmRampupPieces;
                Log?.Invoke($"[{Name}] 报警恢复 → 运行（持续 {duration:F0}s，漂移回收至 {_cycleDrift * 100:F1}%，爬坡{_postAlarmRampupRemaining}件）");
            }
            else
            {
                Log?.Invoke($"[{Name}] 报警恢复 → 运行（持续 {duration:F0}s，漂移回收至 {_cycleDrift * 100:F1}%）");
            }
            _alarmEndTime = null;
            _activeAlarmName = null;
            _activeAlarmAddress = null;
        }

        // 2. 突发不良期退出检查
        if (_burstEndTime.HasValue && now >= _burstEndTime.Value)
        {
            _burstEndTime = null;
            Log?.Invoke($"[{Name}] 突发不良期结束 → 恢复正常 NG 率");
        }

        // 3. 缺料停机退出检查
        if (_shortageEndTime.HasValue && now >= _shortageEndTime.Value)
        {
            _shortageEndTime = null;
            // 清除缺料报警位（直接用记录的地址，避免遍历 200 个报警位）
            if (!string.IsNullOrEmpty(_shortageAlarmAddress))
                WriteBoolIfNotEmpty(_shortageAlarmAddress, false);
            _shortageAlarmName = null;
            _shortageAlarmAddress = null;
            // 修复（2026-08-16，审查 H2）：缺料期间可能因停机次数阈值进入报警态
            // （EnterMaterialShortage → CheckStopCountThreshold → DoTriggerThresholdAlarm 置 Status=Alarm）。
            // 此时不能无条件恢复为 Running，否则会覆盖 Alarm 态，导致报警恢复分支
            // （连续不良清零/漂移回收/爬坡）永不执行、_alarmEndTime 残留。仅当仍为 Idle 时才恢复运行。
            if (Status == SimStatus.Idle)
            {
                Status = SimStatus.Running;
                WriteStatus();
                Log?.Invoke($"[{Name}] 缺料恢复 → 继续生产");
                // 缺料恢复后重新安排产出（避免立即产出，给一个节拍的缓冲）
                ScheduleNextProduce(now);
            }
        }

        // 3.5 操作员暂停恢复检查（午休/交接班/换模/抽检/首件检验结束）
        if (_operatorPauseEndTime.HasValue && now >= _operatorPauseEndTime.Value)
        {
            _operatorPauseEndTime = null;
            var reason = _operatorPauseReason;
            _operatorPauseReason = null;
            if (Status == SimStatus.Idle)
            {
                Status = SimStatus.Running;
                WriteStatus();
                // 午休/交接班/换模后操作员已休息，重置疲劳计时
                _runSessionStartTime = now;
                ScheduleNextProduce(now);
                Log?.Invoke($"[{Name}] {reason}结束 → 恢复运行");
            }
        }
    }

    /// <summary>
    /// 阶段 3：仅运行态执行。处理随机事件触发（通信抖动/报警/突发期/缺料/操作员行为）、节拍产出、配方同步。
    /// </summary>
    private void ProcessRunningStateEvents(DateTime now)
    {
        if (Status != SimStatus.Running) return;

        // 4.5 PLC 通信抖动触发检查（偶发篡改 PLC 内存值模拟电磁干扰）
        if (_scenario.EnableCommJitter && !_commJitterEndTime.HasValue
            && _rng.NextDouble() < _scenario.CommJitterChancePerTick)
        {
            TriggerCommJitter(now);
        }

        // 5. 随机报警触发
        if (_rng.NextDouble() < _scenario.AlarmChancePerTick)
        {
            DoTriggerAlarm(now, manual: false);
            return; // 本次 tick 不产出
        }

        // 6. 突发不良期进入检查（当前未在突发期）
        if (!_burstEndTime.HasValue
            && _rng.NextDouble() < _scenario.BurstChancePerTick)
        {
            var burstSec = _scenario.BurstMinSec
                + _rng.NextDouble() * (_scenario.BurstMaxSec - _scenario.BurstMinSec);
            _burstEndTime = now.AddSeconds(burstSec);
            Log?.Invoke($"[{Name}] 进入突发不良期（NG 率={_scenario.BurstNgRate * 100:F0}%，预计 {burstSec:F0}s）");
        }

        // 7. 缺料停机进入检查（当前未在缺料期）
        if (_scenario.EnableMaterialShortage
            && !_shortageEndTime.HasValue
            && _rng.NextDouble() < _scenario.ShortageChancePerTick)
        {
            EnterMaterialShortage(now);
            return;
        }

        // 7.5 操作员行为检查（午休/交接班/换模/抽检/首件检验，当前未在操作员暂停）
        if (_scenario.EnableOperatorBehavior && !_operatorPauseEndTime.HasValue)
        {
            // 首件检验优先：换模/冷启动后标记位触发的强制检验
            if (_pendingFirstArticleInspection && _scenario.EnableFirstArticleInspection)
            {
                _pendingFirstArticleInspection = false;
                EnterOperatorPause(now, "首件检验", _scenario.FirstArticleInspectionSec);
                return;
            }
            CheckOperatorBehavior(now);
            // 触发了操作员暂停则本次不产出
            if (_operatorPauseEndTime.HasValue) return;
        }

        // 8. 节拍到期产出
        if (now >= _nextProduceTime)
        {
            ProduceOne(now);
            ScheduleNextProduce(now);
        }

        // 9. 定期检查 PLC 配方值是否被 MainAPP 修改（每 5 秒读一次）
        //    MainAPP 的 WriteRecipeAsync 会修改 RecipeAddress，Simulator 需感知并更新节拍。
        if ((now - _lastRecipeCheckTime).TotalSeconds >= 5)
        {
            _lastRecipeCheckTime = now;
            TryUpdateRecipeFromPlc(writeDefaultIfZero: false);
        }
    }

    /// <summary>
    /// 从 PLC 读取配方值并按需更新节拍。
    /// MainAPP 的 WriteRecipeAsync 会修改 RecipeAddress，Simulator 需感知并同步节拍。
    /// </summary>
    /// <param name="writeDefaultIfZero">PLC 中读到 0 时是否写入默认 RecipeValue（启动恢复时为 true，Tick 中为 false）。</param>
    private void TryUpdateRecipeFromPlc(bool writeDefaultIfZero)
    {
        var recipeInPlc = TryReadInt(_config.RecipeAddress);
        if (recipeInPlc > 0)
        {
            var newCycle = 3600.0 / recipeInPlc;
            if (Math.Abs(newCycle - _baseCycleSeconds) > 0.01)
            {
                Log?.Invoke($"[{Name}] 配方变更：RecipeValue={recipeInPlc}（节拍 {_baseCycleSeconds:F1}s → {newCycle:F1}s）");
                _baseCycleSeconds = newCycle;
            }
        }
        else if (writeDefaultIfZero)
        {
            WriteIfNotEmpty(_config.RecipeAddress, _config.RecipeValue);
        }
    }

    /// <summary>
    /// 进入缺料停机：运行中→待机，持续 ShortageMinSec~ShortageMaxSec，
    /// 优先触发名称含"缺料"的报警位（如有），并将状态字改为 Idle 让 MainAPP 感知到停机。
    /// </summary>
    private void EnterMaterialShortage(DateTime now)
    {
        var shortageSec = _scenario.ShortageMinSec
            + _rng.NextDouble() * (_scenario.ShortageMaxSec - _scenario.ShortageMinSec);
        _shortageEndTime = now.AddSeconds(shortageSec);

        // 查找名称含"缺料"/"断料"的报警位（仅扫描前 20 个，避免遍历 200 个报警位）
        _shortageAlarmName = null;
        _shortageAlarmAddress = null;
        foreach (var a in _config.Alarms.Take(20))
        {
            if (a.Name.Contains("缺料") || a.Name.Contains("断料"))
            {
                _shortageAlarmName = a.Name;
                _shortageAlarmAddress = a.PlcAddress;
                WriteBoolIfNotEmpty(a.PlcAddress, true);
                break;
            }
        }

        // 无论是否有专用缺料报警位，都改变状态字为 Idle，
        // 让 MainAPP 通过状态字变化感知到停机（而非依赖"产量停止增长"这种不可靠的检测）。
        // 有专用缺料报警位的设备同时触发报警位，MainAPP 可在 alarm_events 中记录具体报警名称。
        FlushPendingBatch();
        Status = SimStatus.Idle;
        WriteStatus();

        // 缺料属于异常停机，递增停机次数计数报警（与手动 Pause 一致）
        foreach (var ca in _config.CounterAlarms)
        {
            if (!ca.Enabled) continue;
            if (IsStopCounterAlarm(ca))
            {
                _stopCount++;
                WriteIfNotEmpty(ca.PlcAddress, _stopCount);
            }
        }
        // 检查停机次数阈值
        if (_scenario.EnableCounterAlarmThreshold)
            CheckStopCountThreshold(now);

        Log?.Invoke($"[{Name}] 进入缺料停机（{_shortageAlarmName ?? "无报警位"}，预计 {shortageSec:F0}s，停机次数={_stopCount}）");
    }

    /// <summary>
    /// 操作员行为检查：按时钟和产出计数触发午休、交接班、换模、质量抽检。
    /// 每种事件有独立的去重键，保证同一时段/同一班次只触发一次。
    /// </summary>
    private void CheckOperatorBehavior(DateTime now)
    {
        var localNow = now.ToLocalTime();

        // 1. 午休：本地时间进入 [LunchBreakStartHour, LunchBreakEndHour) 时触发，每天一次
        if (localNow.Hour >= _scenario.LunchBreakStartHour && localNow.Hour < _scenario.LunchBreakEndHour)
        {
            var key = localNow.ToString("yyyyMMdd");
            if (_lastLunchKey != key)
            {
                _lastLunchKey = key;
                // 计算到午休结束的剩余秒数（避免午休中间启动时暂停超时）
                var lunchEndLocal = localNow.Date.AddHours(_scenario.LunchBreakEndHour);
                var remainingSec = (lunchEndLocal - localNow).TotalSeconds;
                if (remainingSec < 60) remainingSec = 60; // 最少 1 分钟
                EnterOperatorPause(now, "午休", remainingSec);
                return;
            }
        }

        // 2. 交接班：在班次开始小时触发，每个班次一次
        var shiftHour = -1;
        if (localNow.Hour == _scenario.MorningShiftStartHour) shiftHour = _scenario.MorningShiftStartHour;
        else if (localNow.Hour == _scenario.EveningShiftStartHour) shiftHour = _scenario.EveningShiftStartHour;
        if (shiftHour >= 0)
        {
            var key = $"{localNow:yyyyMMdd}-{shiftHour}";
            if (_lastShiftChangeKey != key)
            {
                _lastShiftChangeKey = key;
                EnterOperatorPause(now, "交接班", _scenario.ShiftChangeDurationSec);
                return;
            }
        }

        // 3. 换模：距上次换模超过间隔时触发
        if ((now - _lastMoldChangeTime).TotalSeconds >= _scenario.MoldChangeIntervalSec)
        {
            var moldSec = _scenario.MoldChangeMinSec
                + _rng.NextDouble() * (_scenario.MoldChangeMaxSec - _scenario.MoldChangeMinSec);
            _lastMoldChangeTime = now;
            _piecesSinceLastMoldChange = 0; // 重置批次效应计数
            // 换模后标记首件检验（换模完成后在下个 Tick 触发）
            if (_scenario.EnableFirstArticleInspection)
                _pendingFirstArticleInspection = true;
            EnterOperatorPause(now, "换模", moldSec);
            return;
        }

        // 4. 质量抽检：每产出 N 件触发一次
        if (_piecesSinceLastQualityCheck >= _scenario.QualityCheckIntervalPieces)
        {
            _piecesSinceLastQualityCheck = 0;
            var checkSec = _scenario.QualityCheckMinSec
                + _rng.NextDouble() * (_scenario.QualityCheckMaxSec - _scenario.QualityCheckMinSec);
            EnterOperatorPause(now, "质量抽检", checkSec);
            return;
        }
    }

    /// <summary>进入操作员暂停：刷新批量计数、切换为 Idle 状态、记录恢复时间。</summary>
    private void EnterOperatorPause(DateTime now, string reason, double durationSec)
    {
        _operatorPauseEndTime = now.AddSeconds(durationSec);
        _operatorPauseReason = reason;
        // 暂停前刷新 PLC 中的批量计数，让 MainAPP 看到最新值
        FlushPendingBatch();
        Status = SimStatus.Idle;
        WriteStatus();
        Log?.Invoke($"[{Name}] 进入{reason}待机（预计 {durationSec:F0}s）");
    }

    /// <summary>将内存中累积但尚未写入 PLC 的 OK/NG 计数刷新到 PLC。</summary>
    private void FlushPendingBatch()
    {
        if (_plcOkCount != _okCount)
        {
            _plcOkCount = _okCount;
            WriteIfNotEmpty(_config.OkCountAddress, _plcOkCount);
        }
        if (_plcNgCount != _ngCount)
        {
            _plcNgCount = _ngCount;
            WriteIfNotEmpty(_config.NgCountAddress, _plcNgCount);
        }
    }

    /// <summary>产出一件：根据当前状态决定 OK/NG，更新计数器和 PLC 地址。</summary>
    /// <param name="now">当前 UTC 时间（由 Tick 传入，避免重复获取且保证与节拍判断一致）。</param>
    private void ProduceOne(DateTime now)
    {
        // 产出后递减突发卡顿剩余件数（仅在真正产出时递减，避免恢复场景提前消耗）
        if (_burstStallRemaining > 0)
            _burstStallRemaining--;

        // NG 率优先级：报警期 > 突发期 > 预热期 > 批次效应 > 正常
        // 正常期 NG 率基线 = 物料批次基线（若启用），否则场景 NgRateBase
        double ngRate;
        // 注（审查 M1）：本分支当前不可达（ProduceOne 仅在 ProcessRunningStateEvents 的运行态分支被调用），
        // 保留作防御——若未来从报警期调用产出，应使用报警期 NG 率。
        if (Status == SimStatus.Alarm)
        {
            ngRate = _scenario.NgRateAlarmBase + _rng.NextDouble() * _scenario.NgRateAlarmJitter;
        }
        else if (BurstActive)
        {
            ngRate = _scenario.BurstNgRate;
        }
        else if (WarmupActive)
        {
            // 预热期 NG 率较高（5-10%），加上小抖动
            ngRate = _scenario.WarmupNgRate + _rng.NextDouble() * 0.05;
        }
        else if (_scenario.EnableBatchEffect && _piecesSinceLastMoldChange < _scenario.BatchEffectPieces)
        {
            // 换模后前 N 件 NG 率突高（新材料/模具未稳定）
            ngRate = _scenario.BatchEffectNgRate + _rng.NextDouble() * 0.05;
        }
        else
        {
            // 正常期：使用当前物料批次的 NG 率基线（若启用），否则场景 NgRateBase
            var baseRate = _scenario.EnableMaterialBatchVariance
                ? _materialBatchNgRate
                : _scenario.NgRateBase;
            ngRate = baseRate + _rng.NextDouble() * _scenario.NgRateJitter;
        }

        // 叠加因素性 NG 率惩罚（不影响报警/突发/预热/批次等状态基线，仅叠加到正常及以上状态）
        // - 报警恢复爬坡：前 N 件 NG 率略高（设备未稳定）
        // - 深夜疲劳：凌晨 2-5 点操作员困倦
        // - 工单赶工：接近目标时 NG 率上升
        // - 设备老化：累计运行超阈值后 NG 率基线上升
        if (PostAlarmRampupActive)
            ngRate += _scenario.PostAlarmRampupNgRatePenalty;
        if (DeepNightActive)
            ngRate += _scenario.DeepNightNgRatePenalty;
        if (ProductionPressureActive)
            ngRate += _scenario.PressureNgRatePenalty;
        if (EquipmentAgingActive)
            ngRate += _scenario.AgingNgRatePenalty;

        // 限制 NG 率上限（避免叠加超过 100%）
        ngRate = Math.Min(ngRate, 0.95);

        bool isNg = _rng.NextDouble() < ngRate;

        if (isNg)
        {
            _ngCount++;
            _consecutiveNg++;
            // 批量更新：OK/NG 计数每 N 件写一次 PLC（模拟工业 PLC 批量刷新机制）
            if (!_scenario.EnableBatchUpdate || (_ngCount - _plcNgCount) >= _scenario.BatchUpdateSize)
            {
                _plcNgCount = _ngCount;
                WriteIfNotEmpty(_config.NgCountAddress, _plcNgCount);
            }

            // 缺陷 +1（随机选一个缺陷类型）
            if (_config.Defects.Count > 0)
            {
                var defect = _config.Defects[_rng.Next(_config.Defects.Count)];
                _defectCounts[defect.PlcAddress] = _defectCounts.GetValueOrDefault(defect.PlcAddress) + 1;
                WriteIfNotEmpty(defect.PlcAddress, _defectCounts[defect.PlcAddress]);
            }

            // 连续不良/NG 计数 +1（非停机类）— 批量更新，避免每件 NG 写 100 个计数报警地址
            if (!_scenario.EnableBatchUpdate || (_consecutiveNg - _plcConsecutiveNg) >= _scenario.BatchUpdateSize)
            {
                _plcConsecutiveNg = _consecutiveNg;
                WriteConsecutiveNgToCounterAlarms(_plcConsecutiveNg);
            }

            // 检查连续不良阈值
            if (_scenario.EnableCounterAlarmThreshold)
                CheckConsecutiveNgThreshold(now);
        }
        else
        {
            _okCount++;
            // 连续不良清零：仅在从 NG→OK 转变时写 PLC（连续 OK 时不重复写 0）
            var wasNg = _consecutiveNg > 0;
            _consecutiveNg = 0;
            // 批量更新：OK 计数每 N 件写一次 PLC（模拟工业 PLC 批量刷新机制）
            if (!_scenario.EnableBatchUpdate || (_okCount - _plcOkCount) >= _scenario.BatchUpdateSize)
            {
                _plcOkCount = _okCount;
                WriteIfNotEmpty(_config.OkCountAddress, _plcOkCount);
            }

            // 连续不良/NG 计数清零（仅在 NG→OK 转变时写，避免连续 OK 重复写 100 个 0）
            if (wasNg)
            {
                _plcConsecutiveNg = 0;
                WriteConsecutiveNgToCounterAlarms(0);
            }
        }

        // 预热件数递增
        if (WarmupActive)
        {
            _warmupProduced++;
            // 预热完成时记录日志
            if (_warmupProduced == _scenario.WarmupPieces)
            {
                Log?.Invoke($"[{Name}] 预热完成（{_scenario.WarmupPieces}件）→ 正常生产");
            }
        }

        // 节拍漂移累积（刀具磨损，预热期不累积；设备老化时累积速度加快）
        if (!WarmupActive && _cycleDrift < _scenario.CycleDriftMax)
        {
            var driftStep = _scenario.CycleDriftStep;
            if (EquipmentAgingActive)
                driftStep *= _scenario.AgingDriftMultiplier;
            _cycleDrift = Math.Min(_scenario.CycleDriftMax, _cycleDrift + driftStep);
        }

        // 产出件数计数器递增（用于换模批次效应和质量抽检触发）
        _piecesSinceLastMoldChange++;
        _piecesSinceLastQualityCheck++;

        // 工单赶工计数：周期性目标，达到后重置
        _workOrderProduced++;
        if (_workOrderProduced >= _scenario.PressureTargetPieces)
        {
            _workOrderProduced = 0;
        }

        // 报警恢复爬坡计数递减
        if (_postAlarmRampupRemaining > 0)
        {
            _postAlarmRampupRemaining--;
            if (_postAlarmRampupRemaining == 0)
            {
                Log?.Invoke($"[{Name}] 报警恢复爬坡完成 → 正常生产");
            }
        }

        // 突发卡顿触发：产出后有小概率进入连续卡顿期（脱模不顺/卡料）
        if (_scenario.EnableBurstStall && _burstStallRemaining <= 0
            && _rng.NextDouble() < _scenario.BurstStallChancePerProduce)
        {
            _burstStallRemaining = _rng.Next(
                _scenario.BurstStallMinPieces, _scenario.BurstStallMaxPieces + 1);
            Log?.Invoke($"[{Name}] 突发卡顿（剩余{_burstStallRemaining}件，节拍×{_scenario.BurstStallCycleMultiplier}）");
        }
    }

    /// <summary>
    /// 检查连续不良计数是否超过阈值，超过则触发报警。
    /// 阈值判断使用 &gt;（与 MainAPP 的 IsTriggered = CurrentValue &gt; MaxValue 一致）。
    /// 触发后不清零，保持 PLC 中的值让 MainAPP 有时间检测到 IsTriggered=true；
    /// 报警恢复时由 Tick 中的报警恢复逻辑清零。
    /// </summary>
    private void CheckConsecutiveNgThreshold(DateTime now)
    {
        foreach (var ca in _config.CounterAlarms)
        {
            if (!ca.Enabled) continue;
            if (IsStopCounterAlarm(ca)) continue;
            if (ca.MaxValue <= 0) continue;

            if (_consecutiveNg > ca.MaxValue)
            {
                DoTriggerThresholdAlarm(now, $"连续不良超阈值({ca.Name}={_consecutiveNg}/{ca.MaxValue})");
                // 不清零：保持 PLC 值让 MainAPP 检测到 IsTriggered=true
                // 清零在报警恢复时执行（ResetConsecutiveNgOnAlarmRecover）
                break;
            }
        }
    }

    /// <summary>检查停机次数是否超过阈值，超过则触发报警（仅触发一次，清零后才允许再触发）。</summary>
    private void CheckStopCountThreshold(DateTime now)
    {
        if (_stopAlarmTriggered) return;

        foreach (var ca in _config.CounterAlarms)
        {
            if (!ca.Enabled) continue;
            if (!IsStopCounterAlarm(ca)) continue;
            if (ca.MaxValue <= 0) continue;

            if (_stopCount > ca.MaxValue)
            {
                DoTriggerThresholdAlarm(now, $"停机次数超阈值({ca.Name}={_stopCount}/{ca.MaxValue})");
                _stopAlarmTriggered = true;
                break;
            }
        }
    }

    /// <summary>
    /// 触发阈值类报警（持续时间较长，模拟需人工干预）。
    /// 仅改变设备状态字为 Alarm，不触发任何 M 位报警（计数报警由 MainAPP 通过 D 字值自行检测 IsTriggered）。
    /// 避免错误触发 Alarms[0]（如"高温报警"）导致 MainAPP 记录错误的报警事件。
    /// </summary>
    private void DoTriggerThresholdAlarm(DateTime now, string reason)
    {
        // 若已在报警态，不重复触发
        if (Status == SimStatus.Alarm) return;

        FlushPendingBatch();
        Status = SimStatus.Alarm;
        WriteStatus();
        _alarmStartTime = now;
        _activeAlarmName = null;  // 计数报警不关联 M 位报警名称
        _activeAlarmAddress = null;

        _alarmEndTime = now.AddSeconds(_scenario.ThresholdAlarmSec);
        Log?.Invoke($"[{Name}] 计数报警触发：{reason}（预计 {_scenario.ThresholdAlarmSec}s 后恢复，状态→Alarm）");
    }

    /// <summary>内部报警触发逻辑（手动和随机共用）。</summary>
    private void DoTriggerAlarm(DateTime now, bool manual)
    {
        FlushPendingBatch();
        Status = SimStatus.Alarm;
        WriteStatus();
        _alarmStartTime = now;

        // 随机选一个报警位触发
        if (_config.Alarms.Count > 0)
        {
            var alarm = _config.Alarms[_rng.Next(_config.Alarms.Count)];
            _activeAlarmName = alarm.Name;
            _activeAlarmAddress = alarm.PlcAddress;
            WriteBoolIfNotEmpty(alarm.PlcAddress, true);
        }

        // 报警持续 AlarmMinSec ~ AlarmMaxSec 秒
        var durationSec = _scenario.AlarmMinSec
            + _rng.NextDouble() * (_scenario.AlarmMaxSec - _scenario.AlarmMinSec);
        _alarmEndTime = now.AddSeconds(durationSec);

        Log?.Invoke($"[{Name}] 运行 → 报警[{_activeAlarmName}]（{(manual ? "手动" : "随机")}触发，预计 {durationSec:F0}s 后恢复）");
    }

    /// <summary>
    /// 安排下次产出时间。
    /// 节拍 = baseCycle / speed × warmupFactor × (1 + drift) × fatigueFactor × stallFactor × speedupFactor
    ///        × postAlarmRampupFactor × deepNightFactor × pressureFactor / shiftFactor × (±10% jitter)
    /// - shiftFactor：班次曲线（上午高峰 1.0、午休 0.6、夜班 0.7），值越小节拍越长
    /// - drift：节拍漂移（0~CycleDriftMax），值越大节拍越长（刀具磨损）
    /// - warmupFactor：预热期节拍倍率（1.2 = 慢 20%），预热完成后为 1.0
    /// - fatigueFactor：连续运行超阈值后渐慢（1.0 ~ 1+FatigueMaxSlowdown）
    /// - stallFactor：突发卡顿时倍增（BurstStallCycleMultiplier），正常时为 1.0
    /// - speedupFactor：下班前加速（ShiftEndSpeedupFactor），正常时为 1.0
    /// - postAlarmRampupFactor：报警恢复爬坡期节拍倍率（1.15 = 慢 15%），正常时为 1.0
    /// - deepNightFactor：深夜疲劳期节拍倍率（1.05 = 慢 5%），正常时为 1.0
    /// - pressureFactor：工单赶工期节拍系数（0.95 = 快 5%），正常时为 1.0
    /// </summary>
    private void ScheduleNextProduce(DateTime now)
    {
        var cycleSeconds = _baseCycleSeconds / _speedMultiplier;
        var shiftFactor = ComputeShiftFactor(now);
        var driftFactor = 1.0 + _cycleDrift;
        var warmupFactor = WarmupActive ? _scenario.WarmupCycleFactor : 1.0;

        // 疲劳曲线：连续运行超过阈值后节拍渐慢
        var fatigueFactor = 1.0;
        if (_scenario.EnableFatigueCurve && Status == SimStatus.Running)
        {
            var runHours = (now - _runSessionStartTime).TotalHours;
            if (runHours > _scenario.FatigueThresholdHours)
            {
                var overtime = runHours - _scenario.FatigueThresholdHours;
                // 超过阈值后每小时增加 FatigueMaxSlowdown/2 的减速（渐进），上限为 FatigueMaxSlowdown
                var slowdown = Math.Min(_scenario.FatigueMaxSlowdown, overtime * _scenario.FatigueMaxSlowdown / 2);
                fatigueFactor = 1.0 + slowdown;
            }
        }

        // 突发卡顿：剩余件数 > 0 时节拍倍增（仅读取，递减在 ProduceOne 中执行）
        var stallFactor = 1.0;
        if (_burstStallRemaining > 0)
        {
            stallFactor = _scenario.BurstStallCycleMultiplier;
        }

        // 下班前加速：临近下班时操作员加快节奏
        var speedupFactor = 1.0;
        if (_scenario.EnableShiftEndSpeedup)
        {
            speedupFactor = ComputeShiftEndSpeedupFactor(now);
        }

        // 报警恢复爬坡：前 N 件节拍慢（设备未稳定）
        var postAlarmRampupFactor = PostAlarmRampupActive ? _scenario.PostAlarmRampupCycleFactor : 1.0;

        // 深夜疲劳：凌晨 2-5 点操作员困倦，节拍略慢
        var deepNightFactor = DeepNightActive ? _scenario.DeepNightCycleFactor : 1.0;

        // 工单赶工：接近目标时节拍加快（操作员赶工）
        var pressureFactor = ProductionPressureActive ? _scenario.PressureCycleFactor : 1.0;

        var effectiveCycle = cycleSeconds
            * warmupFactor * driftFactor * fatigueFactor * stallFactor * speedupFactor
            * postAlarmRampupFactor * deepNightFactor * pressureFactor
            / shiftFactor;
        // ±10% 随机波动
        var jitter = effectiveCycle * (0.9 + _rng.NextDouble() * 0.2);
        _nextProduceTime = now.AddSeconds(jitter);
    }

    /// <summary>
    /// 计算下班前加速系数：在下班前 ShiftEndSpeedupWindowMin 分钟内返回 ShiftEndSpeedupFactor，否则 1.0。
    /// 白班（8-20点）下班时间为 EveningShiftStartHour，夜班下班时间为次日 MorningShiftStartHour。
    /// </summary>
    private double ComputeShiftEndSpeedupFactor(DateTime utcNow)
    {
        var localNow = utcNow.ToLocalTime();
        // 判断当前处于白班还是夜班，确定下班时间
        int shiftEndHour;
        if (localNow.Hour >= _scenario.MorningShiftStartHour && localNow.Hour < _scenario.EveningShiftStartHour)
        {
            // 白班，下班时间为 EveningShiftStartHour
            shiftEndHour = _scenario.EveningShiftStartHour;
        }
        else
        {
            // 夜班，下班时间为次日 MorningShiftStartHour
            shiftEndHour = _scenario.MorningShiftStartHour;
        }

        var shiftEnd = localNow.Date.AddHours(shiftEndHour);
        if (shiftEnd <= localNow) shiftEnd = shiftEnd.AddDays(1);

        var minsToEnd = (shiftEnd - localNow).TotalMinutes;
        if (minsToEnd > 0 && minsToEnd <= _scenario.ShiftEndSpeedupWindowMin)
        {
            return _scenario.ShiftEndSpeedupFactor;
        }
        return 1.0;
    }

    /// <summary>
    /// 班次曲线：按本地时间返回产能系数（0.5-1.0）。
    /// 6-8点 ramp-up，8-12点高峰，12-13点午休，13-17点下午，17-20傍晚，20-22疲劳，22-6夜班。
    /// </summary>
    private double ComputeShiftFactor(DateTime utcNow)
    {
        if (!_scenario.EnableShiftCurve) return 1.0;

        var localNow = utcNow.ToLocalTime();
        var hour = localNow.Hour + localNow.Minute / 60.0;

        return hour switch
        {
            >= 6 and < 8 => 0.5 + (hour - 6) * 0.25,   // ramp-up: 6:00=0.5, 7:59≈1.0
            >= 8 and < 12 => 1.0,                        // 上午高峰
            >= 12 and < 13 => 0.6,                       // 午休
            >= 13 and < 17 => 0.95,                      // 下午
            >= 17 and < 20 => 0.90,                      // 傍晚
            >= 20 and < 22 => 0.80,                      // 晚间疲劳
            _ => 0.70,                                    // 夜班 22:00-6:00
        };
    }

    // ──────────── 新特性辅助方法 ────────────

    /// <summary>
    /// 启动新物料批次：随机持续时间和 NG 率基线。
    /// 不同批次模拟不同供应商/批次的原材料质量差异。
    /// </summary>
    private void StartNewMaterialBatch(DateTime now)
    {
        var batchSec = _scenario.MaterialBatchMinSec
            + _rng.NextDouble() * (_scenario.MaterialBatchMaxSec - _scenario.MaterialBatchMinSec);
        _materialBatchEndTime = now.AddSeconds(batchSec);
        _materialBatchNgRate = _scenario.MaterialBatchNgRateMin
            + _rng.NextDouble() * (_scenario.MaterialBatchNgRateMax - _scenario.MaterialBatchNgRateMin);
    }

    /// <summary>
    /// 累计运行小时数（仅运行态），用于设备老化判断。
    /// 每次调用根据距上次的实际时间差累加，避免漏计。
    /// </summary>
    private void AccumulateRunHours(DateTime now)
    {
        if (Status != SimStatus.Running)
        {
            _lastRunHoursUpdateTime = now;
            return;
        }
        if (_lastRunHoursUpdateTime == DateTime.MinValue)
        {
            _lastRunHoursUpdateTime = now;
            return;
        }
        var delta = (now - _lastRunHoursUpdateTime).TotalHours;
        if (delta > 0)
        {
            _totalRunHours += delta;
            _lastRunHoursUpdateTime = now;
        }
    }

    /// <summary>判断当前小时是否处于深夜疲劳时段（本地时间 2-5 点）。</summary>
    private bool IsDeepNightHour(int localHour)
    {
        if (!_scenario.EnableDeepNightFatigue) return false;
        return localHour >= _scenario.DeepNightStartHour && localHour < _scenario.DeepNightEndHour;
    }

    /// <summary>
    /// 判断是否处于工单赶工期：当前工单完成度达到 PressureThresholdPercent。
    /// 完成度 = 已产出件数 / 目标件数。达到目标后重置（周期性工单）。
    /// </summary>
    private bool IsProductionPressureActive()
    {
        if (!_scenario.EnableProductionPressure) return false;
        if (_scenario.PressureTargetPieces <= 0) return false;
        var completion = (double)_workOrderProduced / _scenario.PressureTargetPieces;
        return completion >= _scenario.PressureThresholdPercent;
    }

    /// <summary>
    /// 触发 PLC 通信抖动：随机选一个非关键地址写入异常值，模拟电磁干扰导致 PLC 内存跳变。
    /// Simulator 内存状态不变，抖动期结束后由下次正常写入自动修正。
    /// 仅篡改缺陷计数/计数报警地址，排除关键地址（状态字/产量/配方/报警位）。
    /// </summary>
    private void TriggerCommJitter(DateTime now)
    {
        // 候选地址：仅缺陷计数和计数报警地址（非关键数据，篡改不影响状态机/产量/OEE）。
        // 排除关键地址：状态字（导致虚假状态转换）、OK/NG 计数（导致产量跳变/虚假工单完成）、
        // 配方地址（导致节拍异常）、清零地址、报警位（导致虚假报警触发/恢复）。
        var criticalAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            _config.OkCountAddress,
            _config.NgCountAddress,
            _config.StatusCountAddress,
            _config.ProductionResetAddress,
            _config.RecipeAddress,
        };
        foreach (var a in _config.Alarms)
            criticalAddresses.Add(a.PlcAddress);

        var candidates = new List<string>();
        foreach (var d in _config.Defects)
            if (!string.IsNullOrEmpty(d.PlcAddress) && !criticalAddresses.Contains(d.PlcAddress))
                candidates.Add(d.PlcAddress);
        foreach (var ca in _config.CounterAlarms)
            if (ca.Enabled && !string.IsNullOrEmpty(ca.PlcAddress) && !criticalAddresses.Contains(ca.PlcAddress))
                candidates.Add(ca.PlcAddress);
        if (candidates.Count == 0) return;

        // 修复（2026-08-16，审查 L3）：无候选地址时直接返回，不设置 _commJitterEndTime，
        // 避免「抖动空转」——否则设备状态显示 [通信抖动] 3 秒但无任何实际效果。
        _commJitterEndTime = now.AddSeconds(_scenario.CommJitterDurationSec);
        _commJitterAddress = candidates[_rng.Next(candidates.Count)];

        // 异常值类型：0xFFFF（全 1）、0（清零）、随机大值
        int jitterValue = _rng.Next(3) switch
        {
            0 => 0xFFFF,
            1 => 0,
            _ => _rng.Next(10000, 99999),
        };

        // 直接写 PLC（不修改内存状态，下次正常写入会修正）
        WriteIfNotEmpty(_commJitterAddress, jitterValue);
        Log?.Invoke($"[{Name}] PLC 通信抖动：{_commJitterAddress}={jitterValue}（持续 {_scenario.CommJitterDurationSec}s）");
    }

    private void WriteStatus() => WriteIfNotEmpty(_config.StatusCountAddress, (int)Status);

    /// <summary>
    /// 数据源模拟：写入温度/湿度寄存器 + 维护「触发命令字置 1 → 等 MainAPP 回执 2 → 复位」握手。
    /// 受 <see cref="SimulateDataSources"/> 节拍驱动，与设备状态机解耦。
    /// </summary>
    private void SimulateDataSources(DateTime now)
    {
        if (_config.Sources is null || _config.Sources.Count == 0)
        {
            SimulateLegacyDataSources(now);
            return;
        }

        UpdateTemperature(now);
        for (var index = 0; index < _config.Sources.Count; index++)
        {
            var source = _config.Sources[index];
            if (!source.Enabled) continue;

            foreach (var value in source.Values ?? [])
            {
                if (!value.Enabled || string.IsNullOrWhiteSpace(value.PlcAddress)) continue;
                WriteSourceValue(source, value, now);
            }

            if (!string.IsNullOrWhiteSpace(source.TriggerAddress))
            {
                var key = string.IsNullOrWhiteSpace(source.Id) ? $"index:{index}" : source.Id;
                if (!_sourceTriggerStates.TryGetValue(key, out var state))
                {
                    state = new SourceTriggerState();
                    _sourceTriggerStates[key] = state;
                }
                SimulateTrigger(source.Name, source.TriggerAddress, source.TriggerValue, source.AckValue, state, now);
            }
        }
    }

    private void SimulateLegacyDataSources(DateTime now)
    {
        UpdateTemperature(now);
        WriteIfNotEmpty($"D{TemperatureAddress}", _temperature);
        WriteIfNotEmpty($"D{HumidityAddress}", 540 + _rng.Next(-10, 11));

        if (!_sourceTriggerStates.TryGetValue("legacy", out var state))
        {
            state = new SourceTriggerState();
            _sourceTriggerStates["legacy"] = state;
        }
        SimulateTrigger(Name, $"D{TriggerAddress}", 1, TriggerAckValue, state, now);
    }

    private void UpdateTemperature(DateTime now)
    {
        if (_tempOverLimit)
        {
            if (now >= _nextTempEventTime)
            {
                _tempOverLimit = false;
                _nextTempEventTime = now.AddSeconds(_rng.Next(45, 90));
            }
        }
        else if (now >= _nextTempEventTime)
        {
            _tempOverLimit = true;
            _nextTempEventTime = now.AddSeconds(_rng.Next(6, 10));
        }
        _temperature = _tempOverLimit
            ? _rng.Next(315, 341)
            : 240 + _rng.Next(-2, 7);
    }

    private void WriteSourceValue(DataSourceConfigDto source, DataSourceValueConfigDto value, DateTime now)
    {
        var dataType = Enum.IsDefined(typeof(DataSourceValueType), value.DataType)
            ? (DataSourceValueType)value.DataType
            : DataSourceValueType.Int32;

        switch (dataType)
        {
            case DataSourceValueType.Float32:
                _writeFloat(value.PlcAddress, GetFloatSourceValue(source, value));
                break;
            case DataSourceValueType.Bool:
                _writeBool(value.PlcAddress, GetBoolSourceValue(value));
                break;
            case DataSourceValueType.String:
                _writeString(value.PlcAddress, GetStringSourceValue(source, value));
                break;
            default:
                WriteIfNotEmpty(value.PlcAddress, GetIntSourceValue(source, value, now));
                break;
        }
    }

    private int GetIntSourceValue(DataSourceConfigDto source, DataSourceValueConfigDto value, DateTime now)
    {
        if (value.ExpectedValue.HasValue) return value.ExpectedValue.Value;
        if (IsTemperature(source, value)) return _temperature;
        if (IsHumidity(source, value)) return 540 + _rng.Next(-10, 11);
        if (value.LimitMax > value.LimitMin) return value.LimitMin + (value.LimitMax - value.LimitMin) / 2;
        return 100 + (int)((now - DateTime.UnixEpoch).TotalSeconds % 20);
    }

    private float GetFloatSourceValue(DataSourceConfigDto source, DataSourceValueConfigDto value)
    {
        if (value.FloatExpectedValue.HasValue) return value.FloatExpectedValue.Value;
        if (IsTemperature(source, value)) return _temperature / 10f;
        if (IsHumidity(source, value)) return 54f;
        if (value.FloatLimitMax > value.FloatLimitMin) return value.FloatLimitMin + (value.FloatLimitMax - value.FloatLimitMin) / 2f;
        return 1f;
    }

    private static bool GetBoolSourceValue(DataSourceValueConfigDto value) => value.BoolExpectedValue ?? true;

    private string GetStringSourceValue(DataSourceConfigDto source, DataSourceValueConfigDto value)
    {
        var text = value.StringExpectedValue ?? $"{source.Id}:{value.Id}";
        var length = Math.Clamp(value.StringLength, 1, ushort.MaxValue);
        return text.Length <= length ? text : text[..length];
    }

    private static bool IsTemperature(DataSourceConfigDto source, DataSourceValueConfigDto value) =>
        source.Name.Contains("温度", StringComparison.OrdinalIgnoreCase)
        || value.Name.Contains("温度", StringComparison.OrdinalIgnoreCase)
        || value.Unit.Contains("℃", StringComparison.OrdinalIgnoreCase);

    private static bool IsHumidity(DataSourceConfigDto source, DataSourceValueConfigDto value) =>
        source.Name.Contains("湿度", StringComparison.OrdinalIgnoreCase)
        || value.Name.Contains("湿度", StringComparison.OrdinalIgnoreCase)
        || value.Unit.Contains('%');

    private void SimulateTrigger(string sourceName, string address, int triggerValue, int ackValue,
        SourceTriggerState state, DateTime now)
    {
        if (state.AwaitingAck)
        {
            var current = TryReadInt(address);
            if (current == ackValue || now >= state.TriggerSetTime.AddSeconds(15))
            {
                WriteIfNotEmpty(address, 0);
                state.AwaitingAck = false;
                state.NextTriggerTime = now.AddSeconds(_rng.Next(25, 45));
                Log?.Invoke($"[{Name}] 数据源「{sourceName}」触发复位：{address}=0（回执={current}）");
            }
        }
        else if (now >= state.NextTriggerTime)
        {
            WriteIfNotEmpty(address, triggerValue);
            state.AwaitingAck = true;
            state.TriggerSetTime = now;
            Log?.Invoke($"[{Name}] 数据源「{sourceName}」触发置位：{address}={triggerValue}，等待采集回执");
        }
    }

    private static void NoopFloatWrite(string address, float value)
    {
    }

    private static void NoopStringWrite(string address, string value)
    {
    }

    /// <summary>将连续不良值写入所有非停机类计数报警地址。</summary>
    private void WriteConsecutiveNgToCounterAlarms(int value)
    {
        foreach (var ca in _config.CounterAlarms)
        {
            if (ca.Enabled && !IsStopCounterAlarm(ca))
                WriteIfNotEmpty(ca.PlcAddress, value);
        }
    }

    private void WriteIfNotEmpty(string address, int value)
    {
        if (!string.IsNullOrEmpty(address))
            _writeInt(address, value);
    }

    private void WriteBoolIfNotEmpty(string address, bool value)
    {
        if (!string.IsNullOrEmpty(address))
            _writeBool(address, value);
    }

    /// <summary>读取 PLC 整数，失败返回 0。</summary>
    private int TryReadInt(string address)
    {
        if (string.IsNullOrEmpty(address)) return 0;
        try { return _readInt(address); }
        catch { return 0; }
    }

    /// <summary>读取 PLC 布尔，失败返回 false。</summary>
    private bool TryReadBool(string address)
    {
        if (string.IsNullOrEmpty(address)) return false;
        try { return _readBool(address); }
        catch { return false; }
    }

    /// <summary>读取 PLC 整数，失败返回 null（与 TryReadInt 的「失败返回 0」语义区分，用于恢复阶段的失败检测）。</summary>
    private int? TryReadIntNullable(string address)
    {
        if (string.IsNullOrEmpty(address)) return null;
        try { return _readInt(address); }
        catch { return null; }
    }

    private static string StatusText(SimStatus s) => s switch
    {
        SimStatus.Offline => "离线",
        SimStatus.Idle => "待机",
        SimStatus.Running => "运行",
        SimStatus.Alarm => "报警",
        _ => $"未知({(int)s})",
    };

    /// <summary>
    /// 判断计数报警是否为停机次数类。
    /// 优先用 <see cref="CounterAlarmKind"/> 显式类别；Kind=Auto 时按名称推断（含"停机"→Stop）。
    /// </summary>
    private static bool IsStopCounterAlarm(CounterAlarmConfig ca)
    {
        return ca.Kind switch
        {
            CounterAlarmKind.Stop => true,
            CounterAlarmKind.ConsecutiveNg => false,
            _ => ca.Name.Contains("停机"),
        };
    }

    /// <summary>获取设备当前状态摘要（用于 read/status 命令显示）。</summary>
    public string GetStatusText()
    {
        lock (_stateLock)
        {
            var alarmInfo = Status == SimStatus.Alarm && _alarmEndTime.HasValue
                ? $"(剩余{(_alarmEndTime.Value - DateTime.UtcNow).TotalSeconds:F0}s)"
                : "";
            var burstInfo = BurstActive ? " [突发不良期]" : "";
            var warmupInfo = WarmupActive ? $" [预热{_warmupProduced}/{_scenario.WarmupPieces}]" : "";
            var shortageInfo = ShortageActive && _shortageEndTime.HasValue
                ? $" [缺料剩余{(_shortageEndTime.Value - DateTime.UtcNow).TotalSeconds:F0}s]"
                : "";
            var operatorPauseInfo = _operatorPauseEndTime.HasValue && DateTime.UtcNow < _operatorPauseEndTime.Value
                ? $" [{_operatorPauseReason}剩余{(_operatorPauseEndTime.Value - DateTime.UtcNow).TotalSeconds:F0}s]"
                : "";
            var stallInfo = _burstStallRemaining > 0 ? $" [卡顿×{_scenario.BurstStallCycleMultiplier} 剩{_burstStallRemaining}]" : "";
            var driftInfo = _cycleDrift > 0.001 ? $" 漂移={_cycleDrift * 100:F1}%" : "";
            // 新特性状态
            var rampupInfo = PostAlarmRampupActive ? $" [爬坡剩{_postAlarmRampupRemaining}]" : "";
            var nightInfo = DeepNightActive ? " [深夜疲劳]" : "";
            var pressureInfo = ProductionPressureActive ? $" [赶工{_workOrderProduced}/{_scenario.PressureTargetPieces}]" : "";
            var agingInfo = EquipmentAgingActive ? $" [老化{_totalRunHours:F1}h]" : "";
            var batchInfo = _scenario.EnableMaterialBatchVariance
                ? $" 批次NG={_materialBatchNgRate * 100:F1}%" : "";
            var jitterInfo = _commJitterEndTime.HasValue && DateTime.UtcNow < _commJitterEndTime.Value
                ? " [通信抖动]" : "";
            var status = Status switch
            {
                SimStatus.Offline => "离线",
                SimStatus.Idle => $"待机{operatorPauseInfo}{shortageInfo}",
                SimStatus.Running => $"运行{warmupInfo}{burstInfo}{shortageInfo}{stallInfo}{rampupInfo}{nightInfo}{pressureInfo}{agingInfo}{jitterInfo}",
                SimStatus.Alarm => $"报警{alarmInfo}",
                _ => "未知",
            };
            return $"{Name}: {status} | OK={_okCount} NG={_ngCount} 连续NG={_consecutiveNg} 停机={_stopCount}{driftInfo}{batchInfo}";
        }
    }
}
