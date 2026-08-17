namespace PlcSimulator;

/// <summary>
/// 模拟场景配置：控制 NG 率、报警频率、突发不良、节拍漂移、班次曲线等参数。
/// 不同场景预设不同参数值，用于压力测试、演示、故障演练等。
///
/// 参数关系：
/// - 节拍 = (3600 / RecipeValue) / speedMultiplier / shiftFactor × (1 + drift)
///   shiftFactor 越大节拍越短（产出越快）；drift 越大节拍越长（刀具磨损）
/// - NG 率 = Status==Alarm ? NgRateAlarm : (BurstActive ? BurstNgRate : NgRateNormal)
/// </summary>
public class ScenarioConfig
{
    public string Name { get; init; } = "normal";

    // ── NG 率 ──
    /// <summary>正常运行期 NG 率下限（实际 NG 率 = NgRateBase + random × NgRateJitter）</summary>
    public double NgRateBase { get; init; } = 0.01;

    /// <summary>正常运行期 NG 率抖动范围（1-3%）</summary>
    public double NgRateJitter { get; init; } = 0.02;

    /// <summary>报警期 NG 率下限（15-30%）</summary>
    public double NgRateAlarmBase { get; init; } = 0.15;

    /// <summary>报警期 NG 率抖动范围</summary>
    public double NgRateAlarmJitter { get; init; } = 0.15;

    // ── 随机报警 ──
    /// <summary>每个 tick 触发随机报警的概率（0.001 = 每 100s 约 1 次）</summary>
    public double AlarmChancePerTick { get; init; } = 0.001;

    /// <summary>报警持续最短秒数</summary>
    public int AlarmMinSec { get; init; } = 5;

    /// <summary>报警持续最长秒数</summary>
    public int AlarmMaxSec { get; init; } = 30;

    // ── 突发不良期（刀具磨损 / 材料批次问题 / 模具温度异常）──
    /// <summary>每个 tick 进入突发不良期的概率（0.00005 = 约 33 分钟一次）</summary>
    public double BurstChancePerTick { get; init; } = 0.00005;

    /// <summary>突发不良持续最短秒数</summary>
    public int BurstMinSec { get; init; } = 30;

    /// <summary>突发不良持续最长秒数</summary>
    public int BurstMaxSec { get; init; } = 120;

    /// <summary>突发不良期 NG 率（30-50%）</summary>
    public double BurstNgRate { get; init; } = 0.30;

    // ── 节拍漂移（刀具磨损渐慢，报警恢复后微降）──
    /// <summary>节拍最大漂移幅度（0.10 = +10%）</summary>
    public double CycleDriftMax { get; init; } = 0.10;

    /// <summary>每次产出新增的漂移量（0.001 = +0.1%/件）</summary>
    public double CycleDriftStep { get; init; } = 0.001;

    /// <summary>报警恢复时回收漂移的比例（0.5 = 回收 50%）</summary>
    public double DriftRecoverOnAlarm { get; init; } = 0.5;

    // ── 班次曲线（按时段调整节拍）──
    public bool EnableShiftCurve { get; init; } = true;

    // ── 开机预热（冷启动期高 NG 率 + 慢节拍，模拟模具温度未稳定）──
    public bool EnableWarmup { get; init; } = true;

    /// <summary>预热件数：开机后前 N 件使用预热参数（10-30 件）</summary>
    public int WarmupPieces { get; init; } = 20;

    /// <summary>预热期 NG 率（5-10%）</summary>
    public double WarmupNgRate { get; init; } = 0.05;

    /// <summary>预热期节拍倍率（1.2 = 慢 20%）</summary>
    public double WarmupCycleFactor { get; init; } = 1.2;

    // ── 缺料停机（材料将尽或物料流中断，停机等待补料）──
    public bool EnableMaterialShortage { get; init; } = true;

    /// <summary>每个 tick 触发缺料的概率（0.00002 = 每 83 分钟一次，tick 间隔 100ms 即 10tick/s）</summary>
    public double ShortageChancePerTick { get; init; } = 0.00002;

    /// <summary>缺料持续最短秒数</summary>
    public int ShortageMinSec { get; init; } = 30;

    /// <summary>缺料持续最长秒数</summary>
    public int ShortageMaxSec { get; init; } = 180;

    // ── CounterAlarm 阈值触发 ──
    public bool EnableCounterAlarmThreshold { get; init; } = true;

    /// <summary>阈值触发报警的持续秒数（较随机报警更长，模拟需人工干预）</summary>
    public int ThresholdAlarmSec { get; init; } = 60;

    // ── PLC 断线仿真（定期断开 TCP 监听模拟网络中断，验证 MainAPP 断线检测与重连）──
    /// <summary>断线仿真间隔秒数（0=不仿真断线）。每隔 N 秒断开一次 TCP 连接。</summary>
    public int DisconnectIntervalSec { get; init; } = 0;

    /// <summary>每次断线持续秒数（断开后等待 N 秒再恢复监听）。</summary>
    public int DisconnectDurationSec { get; init; } = 10;

    // ── 操作员行为模拟（午休/交接班/换模/抽检导致设备暂停）──
    public bool EnableOperatorBehavior { get; init; } = true;

    /// <summary>午休开始时间（本地时间小时数）</summary>
    public int LunchBreakStartHour { get; init; } = 12;

    /// <summary>午休结束时间（本地时间小时数）</summary>
    public int LunchBreakEndHour { get; init; } = 13;

    /// <summary>早班开始时间（本地时间小时数）</summary>
    public int MorningShiftStartHour { get; init; } = 8;

    /// <summary>晚班开始时间（本地时间小时数）</summary>
    public int EveningShiftStartHour { get; init; } = 20;

    /// <summary>交接班暂停持续秒数</summary>
    public int ShiftChangeDurationSec { get; init; } = 30;

    /// <summary>换模间隔秒数（每 N 秒触发一次换模）</summary>
    public int MoldChangeIntervalSec { get; init; } = 7200;

    /// <summary>换模持续最短秒数</summary>
    public int MoldChangeMinSec { get; init; } = 600;

    /// <summary>换模持续最长秒数</summary>
    public int MoldChangeMaxSec { get; init; } = 1200;

    /// <summary>质量抽检间隔件数</summary>
    public int QualityCheckIntervalPieces { get; init; } = 80;

    /// <summary>质量抽检持续最短秒数</summary>
    public int QualityCheckMinSec { get; init; } = 30;

    /// <summary>质量抽检持续最长秒数</summary>
    public int QualityCheckMaxSec { get; init; } = 60;

    // ── 产量曲线波动（疲劳/卡顿/加速/批次效应）──

    /// <summary>疲劳曲线：连续运行超过阈值后节拍渐慢</summary>
    public bool EnableFatigueCurve { get; init; } = true;

    /// <summary>疲劳曲线生效阈值（小时，超过后开始减速）</summary>
    public int FatigueThresholdHours { get; init; } = 4;

    /// <summary>疲劳曲线最大减速比例（0.10 = 慢 10%）</summary>
    public double FatigueMaxSlowdown { get; init; } = 0.10;

    /// <summary>突发卡顿：偶发连续卡机（脱模不顺），每件耗时倍增</summary>
    public bool EnableBurstStall { get; init; } = true;

    /// <summary>每件产出后触发突发卡顿的概率</summary>
    public double BurstStallChancePerProduce { get; init; } = 0.02;

    /// <summary>突发卡顿最少件数</summary>
    public int BurstStallMinPieces { get; init; } = 3;

    /// <summary>突发卡顿最多件数</summary>
    public int BurstStallMaxPieces { get; init; } = 5;

    /// <summary>突发卡顿时节拍倍数（2.5 = 耗时 2.5 倍）</summary>
    public double BurstStallCycleMultiplier { get; init; } = 2.5;

    /// <summary>下班前加速（临近下班时操作员加快节奏）</summary>
    public bool EnableShiftEndSpeedup { get; init; } = true;

    /// <summary>下班前加速窗口（分钟，下班前 N 分钟开始加速）</summary>
    public int ShiftEndSpeedupWindowMin { get; init; } = 30;

    /// <summary>下班前加速系数（0.95 = 快 5%）</summary>
    public double ShiftEndSpeedupFactor { get; init; } = 0.95;

    /// <summary>批次效应：换模后前 N 件 NG 率突高（新材料未稳定）</summary>
    public bool EnableBatchEffect { get; init; } = true;

    /// <summary>批次效应影响的件数</summary>
    public int BatchEffectPieces { get; init; } = 8;

    /// <summary>批次效应期 NG 率</summary>
    public double BatchEffectNgRate { get; init; } = 0.15;

    // ── 数据写入时序（模拟 PLC 批量更新，OK/NG 计数跳跃式增长）──

    /// <summary>批量更新：OK/NG 计数每 N 件写一次 PLC（而非每件都写）</summary>
    public bool EnableBatchUpdate { get; init; } = true;

    /// <summary>批量更新阈值（累积 N 件后刷新一次 PLC）</summary>
    public int BatchUpdateSize { get; init; } = 3;

    // ── 物料批次波动（每 2-4 小时切换批次，不同批次 NG 率基线不同）──
    public bool EnableMaterialBatchVariance { get; init; } = true;

    /// <summary>物料批次持续最短秒数</summary>
    public int MaterialBatchMinSec { get; init; } = 7200;

    /// <summary>物料批次持续最长秒数</summary>
    public int MaterialBatchMaxSec { get; init; } = 14400;

    /// <summary>物料批次 NG 率下限（0.5%）</summary>
    public double MaterialBatchNgRateMin { get; init; } = 0.005;

    /// <summary>物料批次 NG 率上限（3%）</summary>
    public double MaterialBatchNgRateMax { get; init; } = 0.03;

    // ── 设备老化（累计运行超阈值后 NG 率基线上升、漂移速度加快）──
    public bool EnableEquipmentAging { get; init; } = true;

    /// <summary>设备老化生效阈值（小时，累计运行超此值后开始老化）</summary>
    public int AgingThresholdHours { get; init; } = 24;

    /// <summary>老化 NG 率惩罚（+1%）</summary>
    public double AgingNgRatePenalty { get; init; } = 0.01;

    /// <summary>老化漂移速度倍数（1.5 = 漂移累积速度 ×1.5）</summary>
    public double AgingDriftMultiplier { get; init; } = 1.5;

    // ── 工单赶工效应（接近工单目标时节拍加快但 NG 率上升）──
    public bool EnableProductionPressure { get; init; } = true;

    /// <summary>工单目标件数（周期性目标，达到后重置计数开始下一工单）</summary>
    public int PressureTargetPieces { get; init; } = 100;

    /// <summary>赶工触发阈值（0.9 = 完成度达 90% 后触发）</summary>
    public double PressureThresholdPercent { get; init; } = 0.9;

    /// <summary>赶工时节拍系数（0.95 = 快 5%）</summary>
    public double PressureCycleFactor { get; init; } = 0.95;

    /// <summary>赶工时 NG 率惩罚（+2%）</summary>
    public double PressureNgRatePenalty { get; init; } = 0.02;

    // ── 首件检验（换模/冷启动后强制暂停检验，模拟首件质量把关）──
    public bool EnableFirstArticleInspection { get; init; } = true;

    /// <summary>首件检验持续秒数</summary>
    public int FirstArticleInspectionSec { get; init; } = 60;

    // ── 报警恢复爬坡（报警恢复后前 N 件节拍慢、NG 率略高，模拟设备未稳定）──
    public bool EnablePostAlarmRampup { get; init; } = true;

    /// <summary>报警恢复爬坡件数</summary>
    public int PostAlarmRampupPieces { get; init; } = 5;

    /// <summary>爬坡期节拍倍率（1.15 = 慢 15%）</summary>
    public double PostAlarmRampupCycleFactor { get; init; } = 1.15;

    /// <summary>爬坡期 NG 率惩罚（+5%）</summary>
    public double PostAlarmRampupNgRatePenalty { get; init; } = 0.05;

    // ── 深夜疲劳（凌晨 2-5 点操作员最困倦，NG 率 + 节拍惩罚）──
    public bool EnableDeepNightFatigue { get; init; } = true;

    /// <summary>深夜疲劳开始小时（本地时间）</summary>
    public int DeepNightStartHour { get; init; } = 2;

    /// <summary>深夜疲劳结束小时（本地时间，exclusive）</summary>
    public int DeepNightEndHour { get; init; } = 5;

    /// <summary>深夜 NG 率惩罚（+3%）</summary>
    public double DeepNightNgRatePenalty { get; init; } = 0.03;

    /// <summary>深夜节拍倍率（1.05 = 慢 5%）</summary>
    public double DeepNightCycleFactor { get; init; } = 1.05;

    // ── PLC 通信抖动（偶发篡改 PLC 内存值，模拟电磁干扰导致数据跳变）──
    public bool EnableCommJitter { get; init; } = true;

    /// <summary>每个 tick 触发通信抖动的概率（0.001 = 每 100s 约 1 次）。
    /// 需大于 MainAPP 采集间隔的倒数，确保抖动期间 MainAPP 能读到异常值。</summary>
    public double CommJitterChancePerTick { get; init; } = 0.001;

    /// <summary>通信抖动持续秒数（异常值保持时间，之后由正常写入修正）。
    /// 设为 3 秒确保覆盖 MainAPP 至少 1 个采集周期（采集间隔约 1-2 秒）。</summary>
    public int CommJitterDurationSec { get; init; } = 3;

    // ── 预设场景 ──

    public static readonly Dictionary<string, ScenarioConfig> Presets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["normal"] = new ScenarioConfig(),

        ["stress"] = new ScenarioConfig
        {
            Name = "stress",
            NgRateBase = 0.03, NgRateJitter = 0.05,
            NgRateAlarmBase = 0.30, NgRateAlarmJitter = 0.20,
            AlarmChancePerTick = 0.005,       // ~每 20s 一次
            AlarmMinSec = 10, AlarmMaxSec = 60,
            BurstChancePerTick = 0.0002,      // ~每 8 分钟一次
            BurstMinSec = 60, BurstMaxSec = 180,
            BurstNgRate = 0.50,
            CycleDriftMax = 0.20,
            CycleDriftStep = 0.002,
            WarmupPieces = 30, WarmupNgRate = 0.08, WarmupCycleFactor = 1.25,
            ShortageChancePerTick = 0.00005,  // ~每 33 分钟一次（1/(0.00005×10tick/s)=2000s）
            ShortageMinSec = 60, ShortageMaxSec = 300,
            // 操作员行为：压力测试下换模更频繁、抽检更密
            MoldChangeIntervalSec = 3600,     // 1 小时换模一次
            QualityCheckIntervalPieces = 50,
            // 曲线波动：疲劳来得更快、卡顿更频繁
            FatigueThresholdHours = 3,
            FatigueMaxSlowdown = 0.12,
            BurstStallChancePerProduce = 0.04,
            BatchEffectPieces = 10, BatchEffectNgRate = 0.20,
            // 批量更新：更大的批量（5 件一刷），PLC 计数跳跃更明显
            BatchUpdateSize = 5,
            // 新特性：压力测试下老化更快、赶工更激进、抖动更频繁
            AgingThresholdHours = 12,         // 12h 即开始老化
            AgingNgRatePenalty = 0.02,
            PressureTargetPieces = 80,
            PressureNgRatePenalty = 0.03,
            PostAlarmRampupPieces = 8,
            CommJitterChancePerTick = 0.002,
        },

        ["demo"] = new ScenarioConfig
        {
            Name = "demo",
            NgRateBase = 0.04, NgRateJitter = 0.04,
            NgRateAlarmBase = 0.25, NgRateAlarmJitter = 0.15,
            AlarmChancePerTick = 0.002,
            AlarmMinSec = 8, AlarmMaxSec = 25,
            BurstChancePerTick = 0.0001,
            BurstNgRate = 0.40,
            EnableShiftCurve = true,
            WarmupPieces = 15, WarmupNgRate = 0.06,
            ShortageChancePerTick = 0.00003,  // ~每 33 分钟一次
            // 演示模式：放大操作员行为和曲线波动，便于观察
            MoldChangeIntervalSec = 1800,     // 30 分钟换模一次（演示更频繁）
            QualityCheckIntervalPieces = 30,
            BatchEffectPieces = 12, BatchEffectNgRate = 0.18,
            BurstStallChancePerProduce = 0.03,
            ShiftEndSpeedupWindowMin = 45,    // 加宽窗口便于演示观察
            BatchUpdateSize = 4,
            // 新特性：演示模式下缩短批次切换和首件检验时间，便于观察
            MaterialBatchMinSec = 1800,       // 30 分钟切换一次批次
            MaterialBatchMaxSec = 3600,
            PressureTargetPieces = 50,        // 50 件即触发赶工，便于演示
            FirstArticleInspectionSec = 30,
            PostAlarmRampupPieces = 8,
            CommJitterChancePerTick = 0.0015,
        },

        ["fault"] = new ScenarioConfig
        {
            Name = "fault",
            NgRateBase = 0.02, NgRateJitter = 0.02,
            NgRateAlarmBase = 0.40, NgRateAlarmJitter = 0.20,
            AlarmChancePerTick = 0.001,       // ~每 100s 一次（原 0.004 过高致设备几乎不产出）
            AlarmMinSec = 30, AlarmMaxSec = 120,
            BurstChancePerTick = 0.0003,
            BurstMinSec = 90, BurstMaxSec = 300,
            BurstNgRate = 0.60,
            CycleDriftMax = 0.15,
            CycleDriftStep = 0.0015,
            ThresholdAlarmSec = 120,
            WarmupPieces = 25, WarmupNgRate = 0.10, WarmupCycleFactor = 1.30,
            ShortageChancePerTick = 0.00008,  // ~每 12 分钟一次
            ShortageMinSec = 90, ShortageMaxSec = 360,
            // 故障场景：换模耗时更长、疲劳更严重
            MoldChangeIntervalSec = 5400,     // 1.5 小时换模一次
            MoldChangeMinSec = 900, MoldChangeMaxSec = 1800, // 换模 15-30 分钟
            FatigueThresholdHours = 3,
            FatigueMaxSlowdown = 0.15,
            BurstStallChancePerProduce = 0.03,
            BatchEffectPieces = 10, BatchEffectNgRate = 0.20,
            BatchUpdateSize = 3,
            // 新特性：故障场景下老化更快、爬坡更长、抖动更频繁
            AgingThresholdHours = 8,          // 8h 即开始老化
            AgingNgRatePenalty = 0.015,
            AgingDriftMultiplier = 2.0,
            PostAlarmRampupPieces = 10,       // 报警恢复后爬坡更长
            PostAlarmRampupCycleFactor = 1.20,
            PostAlarmRampupNgRatePenalty = 0.08,
            CommJitterChancePerTick = 0.001,
        },

        // 计数报警专项场景：专门用于触发连续不良/停机次数阈值报警
        // 设计要点：低报警频率（让设备持续运行累积计数）+ 高 NG 率 + 频繁缺料停机（触发停机次数）
        ["counteralarm"] = new ScenarioConfig
        {
            Name = "counteralarm",
            // 高 NG 率：正常期 15-25%，突发期 70%（极易触发连续 NG 阈值 8-10）
            NgRateBase = 0.15, NgRateJitter = 0.10,
            NgRateAlarmBase = 0.30, NgRateAlarmJitter = 0.10,
            // 低报警频率：减少报警干扰，让设备持续运行累积连续 NG
            AlarmChancePerTick = 0.00002,     // ~每 83 分钟一次（1/(0.00002×10tick/s)=5000s）
            AlarmMinSec = 15, AlarmMaxSec = 45,
            // 频繁突发不良：70% NG 率，持续 60-120s
            BurstChancePerTick = 0.00008,     // ~每 20 分钟一次（1/(0.00008×10tick/s)=1250s）
            BurstMinSec = 60, BurstMaxSec = 120,
            BurstNgRate = 0.70,
            CycleDriftMax = 0.10,
            CycleDriftStep = 0.001,
            ThresholdAlarmSec = 90,
            WarmupPieces = 10, WarmupNgRate = 0.08, WarmupCycleFactor = 1.15,
            // 频繁缺料停机：每次缺料递增停机次数，5 次即触发阈值(≤5)
            ShortageChancePerTick = 0.0002,   // ~每 8 分钟一次（1/(0.0002×10tick/s)=500s）
            ShortageMinSec = 20, ShortageMaxSec = 60,
            // 降低质量抽检频率：让设备有更长的连续运行时间累积连续 NG
            QualityCheckIntervalPieces = 30,
            QualityCheckMinSec = 10, QualityCheckMaxSec = 20,
            MoldChangeIntervalSec = 7200,
            FatigueThresholdHours = 8,
            FatigueMaxSlowdown = 0.08,
            BurstStallChancePerProduce = 0.01,
            BatchEffectPieces = 5, BatchEffectNgRate = 0.15,
            BatchUpdateSize = 2,
            // 新特性：计数报警场景下提高物料批次 NG 率基线，便于累积连续 NG
            MaterialBatchNgRateMin = 0.10,    // 批次基线 10-20%
            MaterialBatchNgRateMax = 0.20,
            PressureNgRatePenalty = 0.05,     // 赶工时 NG 率惩罚更大
            EnableCommJitter = false,         // 关闭抖动，避免干扰计数报警测试
        },

        // 断线重连专项场景：定期关闭 TCP 代理，验证 MainAPP 的断线检测、重连冷却、状态追踪
        ["disconnect"] = new ScenarioConfig
        {
            Name = "disconnect",
            NgRateBase = 0.02, NgRateJitter = 0.02,
            AlarmChancePerTick = 0.0005,
            AlarmMinSec = 10, AlarmMaxSec = 30,
            // 每 30 秒断线一次，持续 15 秒（足够 MainAPP 检测断线并进入重连冷却期）
            DisconnectIntervalSec = 30,
            DisconnectDurationSec = 15,
            ThresholdAlarmSec = 60,
            WarmupPieces = 5, WarmupNgRate = 0.05, WarmupCycleFactor = 1.10,
            ShortageChancePerTick = 0.00005,
            MoldChangeIntervalSec = 7200,
            // 新特性：断线场景关闭通信抖动（避免与断线混淆），关闭赶工
            EnableCommJitter = false,
            EnableProductionPressure = false,
        },
    };

    /// <summary>按名称获取场景配置，未知名回退到 normal。</summary>
    public static ScenarioConfig Get(string? name) =>
        string.IsNullOrEmpty(name) ? Presets["normal"]
        : Presets.TryGetValue(name, out var s) ? s
        : Presets["normal"];

    /// <summary>
    /// 校验配置合法性，返回问题描述列表（空表示合法）。
    /// 用于启动时防御自定义场景（如未来 JSON 外置）；内置预设已保证合法。
    /// 审查修复 2026-08-16（M7）：Min&gt;Max 会得负区间、概率越界、关键间隔为 0 会死循环。
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        void Prob(string name, double v) { if (v < 0 || v > 1) errors.Add($"{name}={v} 应在 [0,1]"); }
        void MinMax(string name, int min, int max) { if (min > max) errors.Add($"{name}: Min({min}) > Max({max})"); }
        void Positive(string name, int v) { if (v <= 0) errors.Add($"{name}={v} 应 > 0"); }
        void NonNegative(string name, int v) { if (v < 0) errors.Add($"{name}={v} 应 >= 0"); }

        Prob(nameof(NgRateBase), NgRateBase);
        Prob(nameof(NgRateJitter), NgRateJitter);
        Prob(nameof(NgRateAlarmBase), NgRateAlarmBase);
        Prob(nameof(NgRateAlarmJitter), NgRateAlarmJitter);
        Prob(nameof(AlarmChancePerTick), AlarmChancePerTick);
        Prob(nameof(BurstChancePerTick), BurstChancePerTick);
        Prob(nameof(BurstNgRate), BurstNgRate);
        Prob(nameof(CycleDriftMax), CycleDriftMax);
        Prob(nameof(CycleDriftStep), CycleDriftStep);
        Prob(nameof(DriftRecoverOnAlarm), DriftRecoverOnAlarm);
        Prob(nameof(WarmupNgRate), WarmupNgRate);
        Prob(nameof(ShortageChancePerTick), ShortageChancePerTick);
        Prob(nameof(BatchEffectNgRate), BatchEffectNgRate);
        Prob(nameof(BurstStallChancePerProduce), BurstStallChancePerProduce);
        Prob(nameof(MaterialBatchNgRateMin), MaterialBatchNgRateMin);
        Prob(nameof(MaterialBatchNgRateMax), MaterialBatchNgRateMax);
        Prob(nameof(AgingNgRatePenalty), AgingNgRatePenalty);
        Prob(nameof(PressureThresholdPercent), PressureThresholdPercent);
        Prob(nameof(PressureNgRatePenalty), PressureNgRatePenalty);
        Prob(nameof(PostAlarmRampupNgRatePenalty), PostAlarmRampupNgRatePenalty);
        Prob(nameof(DeepNightNgRatePenalty), DeepNightNgRatePenalty);
        Prob(nameof(CommJitterChancePerTick), CommJitterChancePerTick);

        MinMax(nameof(AlarmMinSec), AlarmMinSec, AlarmMaxSec);
        MinMax(nameof(BurstMinSec), BurstMinSec, BurstMaxSec);
        MinMax(nameof(ShortageMinSec), ShortageMinSec, ShortageMaxSec);
        MinMax(nameof(MoldChangeMinSec), MoldChangeMinSec, MoldChangeMaxSec);
        MinMax(nameof(QualityCheckMinSec), QualityCheckMinSec, QualityCheckMaxSec);
        MinMax(nameof(BurstStallMinPieces), BurstStallMinPieces, BurstStallMaxPieces);
        MinMax(nameof(MaterialBatchMinSec), MaterialBatchMinSec, MaterialBatchMaxSec);

        Positive(nameof(ThresholdAlarmSec), ThresholdAlarmSec);
        Positive(nameof(QualityCheckIntervalPieces), QualityCheckIntervalPieces);
        Positive(nameof(BatchUpdateSize), BatchUpdateSize);
        Positive(nameof(DisconnectDurationSec), DisconnectDurationSec);
        NonNegative(nameof(WarmupPieces), WarmupPieces);
        NonNegative(nameof(DisconnectIntervalSec), DisconnectIntervalSec);

        if (MaterialBatchNgRateMin > MaterialBatchNgRateMax)
            errors.Add($"{nameof(MaterialBatchNgRateMin)} > {nameof(MaterialBatchNgRateMax)}");
        if (DeepNightStartHour == DeepNightEndHour)
            errors.Add($"{nameof(DeepNightStartHour)} == {nameof(DeepNightEndHour)}，深夜疲劳时段为空");

        return errors;
    }
}
