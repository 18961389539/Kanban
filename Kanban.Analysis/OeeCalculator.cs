namespace Kanban.Analysis;

/// <summary>
/// OEE（设备综合效率）计算器——**全仓库唯一实现**（ADR-4 单源约定）。
/// 所有比率（合格率/性能率/可用率/OEE）均限制在 [0, 1] 范围内，避免 > 100% 的异常值。
///
/// 消费方：
/// - Collector/MainAPP：经 Kanban.Collector.Core.Services.OeeCalculator 委托（该门面保留
///   实体相关的 CalculateStateDurations，四率公式全部转发本类）
/// - Kanban.Web：直接调用（WASM 无法引用 Core，此前在 OeeAnalysis 复制，已收敛为本类）
/// </summary>
public static class OeeCalculator
{
    /// <summary>将比率限制在 [0, 1] 范围内</summary>
    private static double Clamp(double value) =>
        value < 0 ? 0 : (value > 1 ? 1 : value);

    /// <summary>合格率 = OK / (OK + NG)</summary>
    public static double CalculateQualityRate(int ok, int ng)
    {
        var total = ok + ng;
        return total > 0 ? Clamp((double)ok / total) : 0;
    }

    /// <summary>性能率 = 累计实际产量 / 理想产量；理想产量 = 目标产能(件/小时) × 运行时间(小时)</summary>
    public static double CalculatePerformanceRate(int totalOk, int totalNg, int targetCycle, double runTimeSeconds)
    {
        if (targetCycle <= 0 || runTimeSeconds <= 0) return 0;
        var idealOutput = targetCycle * (runTimeSeconds / 3600.0);
        return idealOutput > 0 ? Clamp((totalOk + totalNg) / idealOutput) : 0;
    }

    /// <summary>可用率 = 运行时间 / (运行时间 + 报警时间)；业务口径不含 PausedTime（见 project_memory.md）</summary>
    public static double CalculateAvailabilityRate(double runTime, double alarmTime)
    {
        var denom = runTime + alarmTime;
        return denom > 0 ? Clamp(runTime / denom) : 0;
    }

    /// <summary>OEE = 合格率 × 性能率 × 可用率</summary>
    public static double CalculateOee(double qualityRate, double performanceRate, double availabilityRate)
    {
        return Clamp(qualityRate * performanceRate * availabilityRate);
    }
}
