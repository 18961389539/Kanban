using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kanban.Collector.Core.Models;

/// <summary>
/// 数据采集源（挂载于 Device.Sources）：同一 PLC 上通过寄存器读取的新维度数据（温湿度/能耗等）。
/// 源 = 容器（标识 + 触发配置），采集值在 <see cref="Values"/>（一个源可带多个值项，如温度+湿度）。
/// 两种采集方式（源级触发，驱动全部值项）：
/// - 配置了 <see cref="TriggerAddress"/>：电平触发——每轮读触发寄存器，值 == <see cref="TriggerValue"/> 时
///   执行采集（读全部值项），完成后向同一地址写 <see cref="AckValue"/>（回执），下一轮不再重复触发。
/// - 未配置触发地址：每轮无条件采集全部值项（定时，周期 = 扫描周期）。
/// 判定（数值型上下限 / 非数值型预期值）按值项独立配置与告警（见 <see cref="DataSourceValue"/>）。
/// 数据源告警不参与设备状态机，不污染 OEE。
/// 兼容：旧版单值格式（PlcAddress/Unit/LimitMin 等在源级平铺）经 <see cref="ExtensionData"/> 捕获，
/// 由 <see cref="MigrateLegacySingleValue"/> 迁移为 Values[0]（幂等，加载后调用）。
/// </summary>
public partial class DataSource : ObservableObject
{
    /// <summary>唯一标识</summary>
    [ObservableProperty]
    private string _id = Guid.NewGuid().ToString("N");

    /// <summary>所属设备 Id</summary>
    [ObservableProperty]
    private string _deviceId = string.Empty;

    /// <summary>名称（展示用）</summary>
    [ObservableProperty]
    private string _name = string.Empty;

    /// <summary>类型标识（温湿度 / 电表 等，仅标识与展示）</summary>
    [ObservableProperty]
    private string _type = string.Empty;

    /// <summary>是否启用（停用后采集循环与快照跳过此源）</summary>
    [ObservableProperty]
    private bool _enabled = true;

    /// <summary>描述</summary>
    [ObservableProperty]
    private string _description = string.Empty;

    // ──────────── 触发配置（源级：一个触发位驱动整个源的全部值项） ────────────

    /// <summary>
    /// 触发地址（D 字地址，可选）。配置后为电平触发：每轮读此地址，值 == <see cref="TriggerValue"/> 时采集全部值项，
    /// 完成后向此地址写 <see cref="AckValue"/>（回执地址 = 触发地址，唯一拓扑）。
    /// 未配置 = 每轮无条件采集（定时，周期 = 扫描周期）。
    /// </summary>
    [ObservableProperty]
    private string _triggerAddress = string.Empty;

    /// <summary>触发值：触发寄存器等于此值时执行采集（默认 1）</summary>
    [ObservableProperty]
    private int _triggerValue = 1;

    /// <summary>回执值：采集完成后写入触发地址的值（默认 2；下一轮读到后不再触发）</summary>
    [ObservableProperty]
    private int _ackValue = 2;

    // ──────────── 值项（多值源：一个源一个或多个采集值） ────────────

    /// <summary>
    /// 采集值项列表（private set 防止外部替换集合导致事件订阅丢失）。
    /// </summary>
    [JsonInclude]
    public ObservableCollection<DataSourceValue> Values { get; private set; } = new();

    // ──────────── 旧版单值格式兼容（重构前 devices.json 平铺字段的捕获区） ────────────

    /// <summary>
    /// 旧版单值字段捕获（PlcAddress/Unit/LimitMin/LimitMax/Hysteresis/ConfirmSeconds/ExpectedValue/EnumValues）。
    /// 迁移成功后清空；序列化时无数据则不输出。
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>
    /// 旧版单值配置迁移：Values 为空且扩展区含 PlcAddress 时，构造 Values[0]（幂等）。
    /// 由 DeviceRepository.LoadAll 在反序列化后对每个源调用一次。
    /// </summary>
    public void MigrateLegacySingleValue()
    {
        if (Values.Count > 0 || ExtensionData == null) return;
        if (!ExtensionData.TryGetValue("PlcAddress", out var addr) || string.IsNullOrWhiteSpace(addr.GetString()))
        {
            // 无旧单值数据：扩展区仅残留运行时字段（CurrentValue 等）或空，直接清理
            ExtensionData = null;
            return;
        }

        var value = new DataSourceValue
        {
            Name = "值1",
            PlcAddress = addr.GetString() ?? string.Empty,
        };
        if (ExtensionData.TryGetValue("Unit", out var unit)) value.Unit = unit.GetString() ?? string.Empty;
        if (ExtensionData.TryGetValue("LimitMin", out var min)) value.LimitMin = min.GetInt32();
        if (ExtensionData.TryGetValue("LimitMax", out var max)) value.LimitMax = max.GetInt32();
        if (ExtensionData.TryGetValue("Hysteresis", out var hys)) value.Hysteresis = hys.GetInt32();
        if (ExtensionData.TryGetValue("ConfirmSeconds", out var sec)) value.ConfirmSeconds = sec.GetInt32();
        if (ExtensionData.TryGetValue("ExpectedValue", out var exp) && exp.ValueKind == JsonValueKind.Number)
            value.ExpectedValue = exp.GetInt32();
        if (ExtensionData.TryGetValue("EnumValues", out var enums) && enums.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in enums.EnumerateArray())
            {
                var displayName = item.TryGetProperty("DisplayName", out var dn) ? dn.GetString() : null;
                var v = item.TryGetProperty("Value", out var vv) && vv.ValueKind == JsonValueKind.Number ? vv.GetInt32() : 0;
                value.EnumValues.Add(new DataSourceEnumValue { Value = v, DisplayName = displayName ?? string.Empty });
            }
        }

        Values.Add(value);
        ExtensionData = null;
    }

    /// <summary>是否配置了触发地址（电平触发）。</summary>
    [JsonIgnore]
    public bool HasTrigger => !string.IsNullOrWhiteSpace(TriggerAddress);

    /// <summary>
    /// 源展示名：定时模式（无触发地址）的名称附带「定时采集」字样，与触发采集源区分。
    /// 仅用于展示（源表格/详情），配置名 <see cref="Name"/> 不变（需求 2026-08-17）。
    /// </summary>
    [JsonIgnore]
    public string DisplayName => HasTrigger ? Name : $"{Name}（定时采集）";
}