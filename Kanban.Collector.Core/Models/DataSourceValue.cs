using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace Kanban.Collector.Core.Models;

/// <summary>
/// 枚举取值映射项：非数值型值项的 值 → 显示名（如 0=就绪 1=运行 2=报警）。
/// 仅用于展示归一化与预期值校验参照，不参与采集逻辑。
/// </summary>
public partial class DataSourceEnumValue : ObservableObject
{
    [ObservableProperty]
    private int _value;

    [ObservableProperty]
    private string _displayName = string.Empty;
}

/// <summary>
/// 数据采集源的值项（多值源的子结构，如「温湿度」源的温度/湿度两个值）。
/// 每个值项独立持有：采集地址、单位、判定参数（数值型上下限/非数值型预期值）与运行时值。
/// 触发配置（TriggerAddress/TriggerValue/AckValue）位于源级——一个触发位驱动整个源的全部值项。
/// 告警状态机按值项粒度运行（每个值独立越限/偏离/延时/滞回判定，AlarmId = src:{valueId}）。
/// </summary>
public partial class DataSourceValue : ObservableObject
{
    /// <summary>唯一标识</summary>
    [ObservableProperty]
    private string _id = Guid.NewGuid().ToString("N");

    /// <summary>值项名称（如 温度 / 湿度）</summary>
    [ObservableProperty]
    private string _name = string.Empty;

    /// <summary>采集地址（D 字地址，如 D300）。源触发（或无触发=每轮）时读取此地址。</summary>
    [ObservableProperty]
    private string _plcAddress = string.Empty;

    /// <summary>单位（如 ℃、kWh，仅用于 UI 展示与快照记录）</summary>
    [ObservableProperty]
    private string _unit = string.Empty;

    // ──────────── 判定配置（值项级，各自独立） ────────────

    /// <summary>
    /// 数值型下限（<see cref="LimitMin"/>）与上限（<see cref="LimitMax"/>）。
    /// 两者都配置且 LimitMax &gt; LimitMin 时按数值型判定：越出区间（含滞回）触发越限告警。
    /// </summary>
    [ObservableProperty]
    private int _limitMin;

    [ObservableProperty]
    private int _limitMax;

    /// <summary>滞回：越限后需回落「限值 ∓ 滞回」以内才恢复，防止边界抖动反复报警。</summary>
    [ObservableProperty]
    private int _hysteresis;

    /// <summary>延时确认（秒）：越限持续超过该时长才确认报警，防瞬时尖峰误报。默认 5s。</summary>
    [ObservableProperty]
    private int _confirmSeconds = 5;

    /// <summary>
    /// 非数值型预期值。配置后按「当前值 ≠ 预期值」判偏离（立即触发，不延时）；
    /// 回到预期值即恢复。数值型与非数值型判定互斥：配置上下限优先。
    /// </summary>
    [ObservableProperty]
    private int? _expectedValue;

    /// <summary>
    /// 枚举取值映射（可空，仅展示归一化/预期值参照）。
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<DataSourceEnumValue> _enumValues = new();

    // ──────────── 运行时状态（不持久化） ────────────

    /// <summary>
    /// PLC 当前值（运行时从 PLC 读取，不持久化；快照写入时取此值）。
    /// 使用 [property: ...] 语法确保特性应用到源生成器生成的属性而非字段。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTriggered))]
    [property: JsonIgnore]
    [property: NotMapped]
    private int _currentValue;

    // ──────────── 派生判定属性（计算属性，不持久化） ────────────

    /// <summary>是否配置了数值型上下限（两者均配置且上限大于下限）。</summary>
    [JsonIgnore]
    [NotMapped]
    public bool HasLimits => LimitMax > LimitMin;

    /// <summary>是否配置了非数值型预期值。</summary>
    [JsonIgnore]
    [NotMapped]
    public bool HasExpectedValue => ExpectedValue.HasValue;

    /// <summary>当前值是否越出上下限区间（不含滞回，供扫描状态机判定"进入"时刻）。</summary>
    [JsonIgnore]
    [NotMapped]
    public bool IsOutOfRange => HasLimits && (CurrentValue > LimitMax || CurrentValue < LimitMin);

    /// <summary>当前值是否已回落（含滞回后回到区间内，供状态机判定"恢复"时刻）。</summary>
    [JsonIgnore]
    [NotMapped]
    public bool IsBackInRange
    {
        get
        {
            if (!HasLimits) return true;
            var hysteresis = Math.Max(0, Hysteresis);
            return CurrentValue <= LimitMax - hysteresis && CurrentValue >= LimitMin + hysteresis;
        }
    }

    /// <summary>当前值是否偏离预期值（非数值型）。</summary>
    [JsonIgnore]
    [NotMapped]
    public bool IsDeviatingFromExpected => HasExpectedValue && CurrentValue != ExpectedValue!.Value;

    /// <summary>
    /// 是否处于告警态（派生，供 UI 实时着色）。数值型 = 越出区间；非数值型 = 偏离预期。
    /// 仅反映当前采样值的静态判定，不含延时确认（延时确认由扫描状态机管理）。
    /// </summary>
    [JsonIgnore]
    [NotMapped]
    public bool IsTriggered => IsOutOfRange || IsDeviatingFromExpected;
}