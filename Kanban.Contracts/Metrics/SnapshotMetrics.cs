namespace Kanban.Contracts.Metrics;

/// <summary>
/// 快照展示换算的**全局唯一实现**（单源约定，与 OeeCalculator 同一思路）。
/// WPF（HomeViewModel/ProductionLine 等）与 WASM（Home.razor）共用此静态类，
/// 禁止在两端各自内联公式——改口径（如速度下限、占比算法）只需改这里。
/// 全部方法为纯数值参数：两端从各自模型（DeviceRuntime / DeviceSnapshotDto）提取字段后调用。
/// 注意：OEE 四率已在服务端 OeeCalculator 算好随快照下发，此处不重复实现。
/// </summary>
public static class SnapshotMetrics
{
    /// <summary>速度下限：运行时长短于此（秒）时不计算速度，避免启动瞬间产量/运行小时爆炸。</summary>
    public const double MinRunTimeSecForSpeed = 5;

    /// <summary>实时速度（件/小时）= 总产量 / 运行小时；运行时长过短归 0 防启动失真。</summary>
    public static double RealtimeSpeed(double runTimeSeconds, int ok, int ng)
        => runTimeSeconds >= MinRunTimeSecForSpeed ? (ok + ng) / (runTimeSeconds / 3600.0) : 0;

    /// <summary>总产量（OK + NG）。</summary>
    public static int TotalOutput(int ok, int ng) => ok + ng;

    /// <summary>不良率（NG / 总产量）；总产量为 0 返回 0。</summary>
    public static double NgRate(int ok, int ng)
    {
        var total = ok + ng;
        return total > 0 ? (double)ng / total : 0;
    }

    /// <summary>良品率（OK / 总产量）；总产量为 0 返回 0，避免无产量时显示 100%。</summary>
    public static double QualityRate(int ok, int ng)
    {
        var total = ok + ng;
        return total > 0 ? (double)ok / total : 0;
    }

    /// <summary>
    /// 时长占比 = value / (运行+报警+暂停[+离线])；总时长为 0 返回 0。offlineTime 仅用于展示口径，不参与 OEE。
    /// </summary>
    public static double TimeRatio(double value, double runTime, double alarmTime, double pausedTime, double offlineTime = 0)
    {
        var total = runTime + alarmTime + pausedTime + offlineTime;
        return total > 0 ? value / total : 0;
    }

    /// <summary>节拍（秒/件）= 3600 / 每小时件数；速度≤0 返回 0（UI 显示"—"）。</summary>
    public static double CycleSeconds(double perHour)
        => perHour > 0 ? 3600.0 / perHour : 0;

    /// <summary>
    /// 运行期间平均周期（秒/件）。与 <see cref="RealtimeSpeed"/> 同源，运行不足
    /// <see cref="MinRunTimeSecForSpeed"/> 秒时返回 0。不经性能率 Clamp，超产时快于目标。
    /// </summary>
    public static double AverageCycleSeconds(double runTimeSeconds, int ok, int ng)
        => CycleSeconds(RealtimeSpeed(runTimeSeconds, ok, ng));

    /// <summary>应产件数 = 目标产能（件/小时）× 已过小时；产能或时长无效时返回 0。</summary>
    public static int ExpectedOutput(int targetPcsPerHour, double elapsedHours)
        => targetPcsPerHour > 0 && elapsedHours > 0
            ? (int)Math.Round(targetPcsPerHour * elapsedHours)
            : 0;

    /// <summary>速度达成率 = 实际速度 / 目标产能，Clamp[0,1]；目标≤0 返回 0。</summary>
    public static double AchievementRate(double actualPerHour, double targetPerHour)
        => targetPerHour > 0 ? Math.Clamp(actualPerHour / targetPerHour, 0, 1) : 0;

    /// <summary>OEE 活动窗口时长（运行+报警+暂停，不含离线）。</summary>
    public static double OeeActiveTimeSeconds(double runTime, double alarmTime, double pausedTime)
        => runTime + alarmTime + pausedTime;

    /// <summary>
    /// 运行稳定性（OEE 口径）= 1 − 报警/(运行+报警+暂停)。
    /// 与 A/P/Q 同窗口；展示用 <see cref="TimeRatio"/> 含离线时分母不同。
    /// </summary>
    public static double OperationalStability(double runTime, double alarmTime, double pausedTime)
    {
        var active = OeeActiveTimeSeconds(runTime, alarmTime, pausedTime);
        return active > 0 ? Math.Clamp(1.0 - alarmTime / active, 0, 1) : 0;
    }

    /// <summary>
    /// 设备健康分（0–100）= 100×(0.30×A + 0.20×P + 0.25×Q + 0.25×稳定性)。
    /// A/P/Q 为 OEE 三率；稳定性分母与 OEE 窗口一致（不含离线）。无活动时长返回 0。
    /// </summary>
    public static double DeviceHealthScore(
        double availabilityRate, double performanceRate, double qualityRate,
        double runTime, double alarmTime, double pausedTime)
    {
        if (OeeActiveTimeSeconds(runTime, alarmTime, pausedTime) <= 0) return 0;
        var stability = OperationalStability(runTime, alarmTime, pausedTime);
        var score = 100.0 * (0.30 * availabilityRate + 0.20 * performanceRate
            + 0.25 * qualityRate + 0.25 * stability);
        return Math.Clamp(score, 0, 100);
    }
}
