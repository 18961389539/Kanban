namespace PlcSimulator;

/// <summary>
/// 设备配置（与 MainAPP Device 模型对齐的精简版，用于反序列化 devices.json）。
/// </summary>
public class DeviceConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    // ── PLC 地址 ──
    public string OkCountAddress { get; set; } = "";
    public string NgCountAddress { get; set; } = "";
    public string StatusCountAddress { get; set; } = "";
    public string ProductionResetAddress { get; set; } = "";
    public string RecipeAddress { get; set; } = "";
    public int RecipeValue { get; set; }

    // ── 子项 ──
    public List<AlarmConfig> Alarms { get; set; } = new();
    public List<DefectConfig> Defects { get; set; } = new();
    public List<CountAlarmConfig> CountAlarms { get; set; } = new();
}

public class AlarmConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string PlcAddress { get; set; } = "";
}

public class DefectConfig
{
    public string Name { get; set; } = "";
    public string PlcAddress { get; set; } = "";
}

public class CountAlarmConfig
{
    public string Name { get; set; } = "";
    public string PlcAddress { get; set; } = "";
    public int MaxValue { get; set; }

    /// <summary>
    /// 计数报警类别。未指定时按名称推断（含"停机"→Stop，否则→ConsecutiveNg），
    /// 保持对旧 devices.json 的兼容。
    /// </summary>
    public CountAlarmKind Kind { get; set; } = CountAlarmKind.Auto;
}

/// <summary>计数报警类别：停机次数 / 连续不良。</summary>
public enum CountAlarmKind
{
    /// <summary>按名称推断：含"停机"视为 Stop，否则视为 ConsecutiveNg。</summary>
    Auto = 0,
    /// <summary>停机次数类（Pause/缺料时 +1，归零时保留）。</summary>
    Stop,
    /// <summary>连续不良类（NG 时 +1，OK 时清零，归零时清零）。</summary>
    ConsecutiveNg,
}
