using CommunityToolkit.Mvvm.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace Kanban.Core.Models;

/// <summary>
/// 计数报警：从 PLC 读取数值，超过阈值时触发报警。
/// 区别于 Alarm（M 位边沿检测），CountAlarm 基于数值阈值判断。
/// </summary>
public partial class CountAlarm : ObservableObject
{
    /// <summary>唯一标识</summary>
    [ObservableProperty]
    private string _id = Guid.NewGuid().ToString("N");

    /// <summary>所属设备 Id</summary>
    [ObservableProperty]
    private string _deviceId = string.Empty;

    /// <summary>报警名称</summary>
    [ObservableProperty]
    private string _name = string.Empty;

    /// <summary>PLC 地址（D 字地址，如 D300）</summary>
    [ObservableProperty]
    private string _plcAddress = string.Empty;

    /// <summary>阈值上限，当前值超过此值时触发报警</summary>
    [ObservableProperty]
    private int _maxValue;

    // ──────────── 运行时状态（不持久化） ────────────

    /// <summary>
    /// PLC 当前值（运行时从 PLC 读取，不持久化）。
    /// 使用 [property: ...] 语法确保特性应用到源生成器生成的属性而非字段。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTriggered))]
    [property: JsonIgnore]
    [property: NotMapped]
    private int _currentValue;

    /// <summary>
    /// 是否触发（CurrentValue > MaxValue，只读计算属性，不持久化）。
    /// MaxValue &lt;= 0 视为未配置阈值，不触发（避免新加未配置报警因 PLC 当前值非 0 立即误报）。
    /// </summary>
    [JsonIgnore]
    [NotMapped]
    public bool IsTriggered => MaxValue > 0 && CurrentValue > MaxValue;

    // ──────────── 可选辅助属性 ────────────

    /// <summary>是否启用（停用后采集循环跳过此报警）</summary>
    [ObservableProperty]
    private bool _enabled = true;

    /// <summary>报警描述</summary>
    [ObservableProperty]
    private string _description = string.Empty;

    /// <summary>单位（如 个、次、mm，仅用于 UI 显示）</summary>
    [ObservableProperty]
    private string _unit = string.Empty;
}
