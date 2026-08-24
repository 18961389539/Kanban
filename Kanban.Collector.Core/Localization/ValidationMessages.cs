using System.Globalization;
using System.Resources;

namespace Kanban.Collector.Core.Localization;

/// <summary>
/// 进程间共享的配置校验错误消息（被 Kanban.Collector 和 MainAPP 共同依赖的 Kanban.Collector.Core 使用）。
/// 默认值是中文以保留向后兼容；MainAPP / Collector 启动时根据用户界面语言调用 ApplyLanguage 整体覆盖。
/// en/ja/pt-BR 文案从生成的 Resources/Messages.{en,ja,pt-BR}.resx 卫星程序集读取；
/// 资源源为统一 Localization.csv。
/// 模板中使用 {0}/{1} 占位符，由调用方通过 string.Format 填充。
/// </summary>
public static class ValidationMessages
{
    private static readonly ResourceManager s_rm = new(
        "Kanban.Collector.Core.Resources.Messages",
        typeof(ValidationMessages).Assembly);

    // ─── 默认中文模板（与 Messages.resx 中性资源一致，向后兼容）───
    public const string DefaultPlcIpEmpty = "PLC IP 地址为空";
    public const string DefaultPlcIpInvalid = "PLC IP 地址 '{0}' 不是合法的 IPv4 地址";
    public const string DefaultPlcPortOutOfRange = "PLC 端口 {0} 不在合法范围 (1-65535)";
    public const string DefaultPlcBrandInvalid = "PLC 品牌无效：{0}";
    public const string DefaultPlcTimeoutOutOfRange = "PLC 连接超时 {0}ms 不在合法范围 (100-60000)";
    public const string DefaultConnectionProfileEmpty = "第 {0} 个连接档案为空";
    public const string DefaultConnectionProfileIdEmpty = "第 {0} 个连接档案 Id 不能为空";
    public const string DefaultConnectionProfileIdDuplicate = "连接档案 Id 重复：{0}";
    public const string DefaultConnectionProfileNameEmpty = "连接档案 {0} 名称不能为空";
    public const string DefaultConnectionProfileConfigMissing = "连接档案 {0} 配置不能为空";
    public const string DefaultConnectionProfileIdRequired = "连接档案 Id 不能为空";
    public const string DefaultConnectionProfileEntryNull = "连接档案不能为空。";
    public const string DefaultDeviceConnectionProfileMissing = "设备 {0} 引用了不存在的连接档案 {1}。";
    public const string DefaultConnectionProfileAdapterAmbiguous = "连接档案 {0} 的协议 {1} 与品牌 {2} 注册了多个适配器：{3}";
    public const string DefaultConnectionProfileAdapterNotFound = "连接档案 {0} 未找到协议 {1} / 品牌 {2} 的适配器。";
    public const string DefaultDataSourceProtocolKeyEmpty = "协议键不能为空。";
    public const string DefaultModbusUnitIdOutOfRange = "Modbus UnitId {0} 不在合法范围 (1-247)";
    public const string DefaultModbusRegisterFunctionInvalid = "Modbus 寄存器功能码 {0} 无效，应为 3 或 4";
    public const string DefaultModbusBitFunctionInvalid = "Modbus 位功能码 {0} 无效，应为 1 或 2";
    public const string DefaultModbusDataFormatInvalid = "Modbus 数据格式无效：{0}";
    public const string DefaultSiemensDataFormatInvalid = "Siemens 数据格式无效：{0}";
    public const string DefaultSiemensModelUnsupported = "Siemens 型号不受支持：{0}";
    public const string DefaultSiemensRackOutOfRange = "Siemens Rack {0} 不在合法范围 (0-7)";
    public const string DefaultSiemensSlotOutOfRange = "Siemens Slot {0} 不在合法范围 (0-31)";
    public const string DefaultSiemensBatchInt32LimitOutOfRange = "Siemens 批量 Int32 上限 {0} 不在合法范围 (1-55)";
    public const string DefaultModbusBatchInt32LimitOutOfRange = "Modbus 批量 Int32 上限 {0} 不在合法范围 (1-2000)";
    public const string DefaultPollingIntervalInvalid = "PLC 数据采集轮询间隔 {0}ms 无效，必须在 10-60000ms 之间";
    public const string DefaultHistoryWriteIntervalInvalid = "历史数据写入间隔 {0} 次无效，必须在 1-3600 次之间";
    public const string DefaultDashboardRefreshIntervalInvalid = "主页仪表板刷新间隔 {0}ms 无效，必须 ≥ 1ms";
    public const string DefaultPlcBatchReadMaxLengthInvalid = "PLC 批量读取长度上限 {0} 无效，必须在 1-1024 之间";
    public const string DefaultPlcBatchReadMaxGapSlotsInvalid = "PLC 批量读取空洞上限 {0} 无效，必须在 0-256 之间";
    public const string DefaultNoShiftsConfigured = "未配置任何班次";
    public const string DefaultShiftNameEmpty = "第 {0} 个班次名称为空";
    public const string DefaultShiftStartEndEqual = "班次 '{0}' 起止时间相同（{1}）";

    private static string s_plcIpEmpty = DefaultPlcIpEmpty;
    private static string s_plcIpInvalid = DefaultPlcIpInvalid;
    private static string s_plcPortOutOfRange = DefaultPlcPortOutOfRange;
    private static string s_plcBrandInvalid = DefaultPlcBrandInvalid;
    private static string s_plcTimeoutOutOfRange = DefaultPlcTimeoutOutOfRange;
    private static string s_connectionProfileEmpty = DefaultConnectionProfileEmpty;
    private static string s_connectionProfileIdEmpty = DefaultConnectionProfileIdEmpty;
    private static string s_connectionProfileIdDuplicate = DefaultConnectionProfileIdDuplicate;
    private static string s_connectionProfileNameEmpty = DefaultConnectionProfileNameEmpty;
    private static string s_connectionProfileConfigMissing = DefaultConnectionProfileConfigMissing;
    private static string s_connectionProfileIdRequired = DefaultConnectionProfileIdRequired;
    private static string s_connectionProfileEntryNull = DefaultConnectionProfileEntryNull;
    private static string s_deviceConnectionProfileMissing = DefaultDeviceConnectionProfileMissing;
    private static string s_connectionProfileAdapterAmbiguous = DefaultConnectionProfileAdapterAmbiguous;
    private static string s_connectionProfileAdapterNotFound = DefaultConnectionProfileAdapterNotFound;
    private static string s_dataSourceProtocolKeyEmpty = DefaultDataSourceProtocolKeyEmpty;
    private static string s_modbusUnitIdOutOfRange = DefaultModbusUnitIdOutOfRange;
    private static string s_modbusRegisterFunctionInvalid = DefaultModbusRegisterFunctionInvalid;
    private static string s_modbusBitFunctionInvalid = DefaultModbusBitFunctionInvalid;
    private static string s_modbusDataFormatInvalid = DefaultModbusDataFormatInvalid;
    private static string s_siemensDataFormatInvalid = DefaultSiemensDataFormatInvalid;
    private static string s_siemensModelUnsupported = DefaultSiemensModelUnsupported;
    private static string s_siemensRackOutOfRange = DefaultSiemensRackOutOfRange;
    private static string s_siemensSlotOutOfRange = DefaultSiemensSlotOutOfRange;
    private static string s_siemensBatchInt32LimitOutOfRange = DefaultSiemensBatchInt32LimitOutOfRange;
    private static string s_modbusBatchInt32LimitOutOfRange = DefaultModbusBatchInt32LimitOutOfRange;
    private static string s_pollingIntervalInvalid = DefaultPollingIntervalInvalid;
    private static string s_historyWriteIntervalInvalid = DefaultHistoryWriteIntervalInvalid;
    private static string s_dashboardRefreshIntervalInvalid = DefaultDashboardRefreshIntervalInvalid;
    private static string s_plcBatchReadMaxLengthInvalid = DefaultPlcBatchReadMaxLengthInvalid;
    private static string s_plcBatchReadMaxGapSlotsInvalid = DefaultPlcBatchReadMaxGapSlotsInvalid;
    private static string s_noShiftsConfigured = DefaultNoShiftsConfigured;
    private static string s_shiftNameEmpty = DefaultShiftNameEmpty;
    private static string s_shiftStartEndEqual = DefaultShiftStartEndEqual;

    public static string PlcIpEmpty => s_plcIpEmpty;
    public static string PlcIpInvalid => s_plcIpInvalid;
    public static string PlcPortOutOfRange => s_plcPortOutOfRange;
    public static string PlcBrandInvalid => s_plcBrandInvalid;
    public static string PlcTimeoutOutOfRange => s_plcTimeoutOutOfRange;
    public static string ConnectionProfileEmpty => s_connectionProfileEmpty;
    public static string ConnectionProfileIdEmpty => s_connectionProfileIdEmpty;
    public static string ConnectionProfileIdDuplicate => s_connectionProfileIdDuplicate;
    public static string ConnectionProfileNameEmpty => s_connectionProfileNameEmpty;
    public static string ConnectionProfileConfigMissing => s_connectionProfileConfigMissing;
    public static string ConnectionProfileIdRequired => s_connectionProfileIdRequired;
    public static string ConnectionProfileEntryNull => s_connectionProfileEntryNull;
    public static string DeviceConnectionProfileMissing => s_deviceConnectionProfileMissing;
    public static string ConnectionProfileAdapterAmbiguous => s_connectionProfileAdapterAmbiguous;
    public static string ConnectionProfileAdapterNotFound => s_connectionProfileAdapterNotFound;
    public static string DataSourceProtocolKeyEmpty => s_dataSourceProtocolKeyEmpty;
    public static string ModbusUnitIdOutOfRange => s_modbusUnitIdOutOfRange;
    public static string ModbusRegisterFunctionInvalid => s_modbusRegisterFunctionInvalid;
    public static string ModbusBitFunctionInvalid => s_modbusBitFunctionInvalid;
    public static string ModbusDataFormatInvalid => s_modbusDataFormatInvalid;
    public static string SiemensDataFormatInvalid => s_siemensDataFormatInvalid;
    public static string SiemensModelUnsupported => s_siemensModelUnsupported;
    public static string SiemensRackOutOfRange => s_siemensRackOutOfRange;
    public static string SiemensSlotOutOfRange => s_siemensSlotOutOfRange;
    public static string SiemensBatchInt32LimitOutOfRange => s_siemensBatchInt32LimitOutOfRange;
    public static string ModbusBatchInt32LimitOutOfRange => s_modbusBatchInt32LimitOutOfRange;
    public static string PollingIntervalInvalid => s_pollingIntervalInvalid;
    public static string HistoryWriteIntervalInvalid => s_historyWriteIntervalInvalid;
    public static string DashboardRefreshIntervalInvalid => s_dashboardRefreshIntervalInvalid;
    public static string PlcBatchReadMaxLengthInvalid => s_plcBatchReadMaxLengthInvalid;
    public static string PlcBatchReadMaxGapSlotsInvalid => s_plcBatchReadMaxGapSlotsInvalid;
    public static string NoShiftsConfigured => s_noShiftsConfigured;
    public static string ShiftNameEmpty => s_shiftNameEmpty;
    public static string ShiftStartEndEqual => s_shiftStartEndEqual;

    /// <summary>
    /// 简单语言预设：根据语言文化代码覆盖全部文案。传入 null 还原默认中文。
    /// 非中文文案从生成的 Messages.{en,ja,pt-BR}.resx 卫星程序集读取，避免硬编码副本。
    /// 与 ConnectionStatusMessages.ApplyLanguage 行为一致。
    /// </summary>
    public static void ApplyLanguage(string? langCode)
    {
        if (langCode is null)
        {
            RestoreDefaults();
            return;
        }

        CultureInfo culture;
        try
        {
            culture = CultureInfo.GetCultureInfo(langCode);
        }
        catch (CultureNotFoundException)
        {
            RestoreDefaults();
            return;
        }

        s_plcIpEmpty = s_rm.GetString("PlcIpEmpty", culture) ?? DefaultPlcIpEmpty;
        s_plcIpInvalid = s_rm.GetString("PlcIpInvalid", culture) ?? DefaultPlcIpInvalid;
        s_plcPortOutOfRange = s_rm.GetString("PlcPortOutOfRange", culture) ?? DefaultPlcPortOutOfRange;
        s_plcBrandInvalid = s_rm.GetString("PlcBrandInvalid", culture) ?? DefaultPlcBrandInvalid;
        s_plcTimeoutOutOfRange = s_rm.GetString("PlcTimeoutOutOfRange", culture) ?? DefaultPlcTimeoutOutOfRange;
        s_connectionProfileEmpty = s_rm.GetString("ConnectionProfileEmpty", culture) ?? DefaultConnectionProfileEmpty;
        s_connectionProfileIdEmpty = s_rm.GetString("ConnectionProfileIdEmpty", culture) ?? DefaultConnectionProfileIdEmpty;
        s_connectionProfileIdDuplicate = s_rm.GetString("ConnectionProfileIdDuplicate", culture) ?? DefaultConnectionProfileIdDuplicate;
        s_connectionProfileNameEmpty = s_rm.GetString("ConnectionProfileNameEmpty", culture) ?? DefaultConnectionProfileNameEmpty;
        s_connectionProfileConfigMissing = s_rm.GetString("ConnectionProfileConfigMissing", culture) ?? DefaultConnectionProfileConfigMissing;
        s_connectionProfileIdRequired = s_rm.GetString("ConnectionProfileIdRequired", culture) ?? DefaultConnectionProfileIdRequired;
        s_connectionProfileEntryNull = s_rm.GetString("ConnectionProfileEntryNull", culture) ?? DefaultConnectionProfileEntryNull;
        s_deviceConnectionProfileMissing = s_rm.GetString("DeviceConnectionProfileMissing", culture) ?? DefaultDeviceConnectionProfileMissing;
        s_connectionProfileAdapterAmbiguous = s_rm.GetString("ConnectionProfileAdapterAmbiguous", culture) ?? DefaultConnectionProfileAdapterAmbiguous;
        s_connectionProfileAdapterNotFound = s_rm.GetString("ConnectionProfileAdapterNotFound", culture) ?? DefaultConnectionProfileAdapterNotFound;
        s_dataSourceProtocolKeyEmpty = s_rm.GetString("DataSourceProtocolKeyEmpty", culture) ?? DefaultDataSourceProtocolKeyEmpty;
        s_modbusUnitIdOutOfRange = s_rm.GetString("ModbusUnitIdOutOfRange", culture) ?? DefaultModbusUnitIdOutOfRange;
        s_modbusRegisterFunctionInvalid = s_rm.GetString("ModbusRegisterFunctionInvalid", culture) ?? DefaultModbusRegisterFunctionInvalid;
        s_modbusBitFunctionInvalid = s_rm.GetString("ModbusBitFunctionInvalid", culture) ?? DefaultModbusBitFunctionInvalid;
        s_modbusDataFormatInvalid = s_rm.GetString("ModbusDataFormatInvalid", culture) ?? DefaultModbusDataFormatInvalid;
        s_siemensDataFormatInvalid = s_rm.GetString("SiemensDataFormatInvalid", culture) ?? DefaultSiemensDataFormatInvalid;
        s_siemensModelUnsupported = s_rm.GetString("SiemensModelUnsupported", culture) ?? DefaultSiemensModelUnsupported;
        s_siemensRackOutOfRange = s_rm.GetString("SiemensRackOutOfRange", culture) ?? DefaultSiemensRackOutOfRange;
        s_siemensSlotOutOfRange = s_rm.GetString("SiemensSlotOutOfRange", culture) ?? DefaultSiemensSlotOutOfRange;
        s_siemensBatchInt32LimitOutOfRange = s_rm.GetString("SiemensBatchInt32LimitOutOfRange", culture) ?? DefaultSiemensBatchInt32LimitOutOfRange;
        s_modbusBatchInt32LimitOutOfRange = s_rm.GetString("ModbusBatchInt32LimitOutOfRange", culture) ?? DefaultModbusBatchInt32LimitOutOfRange;
        s_pollingIntervalInvalid = s_rm.GetString("PollingIntervalInvalid", culture) ?? DefaultPollingIntervalInvalid;
        s_historyWriteIntervalInvalid = s_rm.GetString("HistoryWriteIntervalInvalid", culture) ?? DefaultHistoryWriteIntervalInvalid;
        s_dashboardRefreshIntervalInvalid = s_rm.GetString("DashboardRefreshIntervalInvalid", culture) ?? DefaultDashboardRefreshIntervalInvalid;
        s_plcBatchReadMaxLengthInvalid = s_rm.GetString("PlcBatchReadMaxLengthInvalid", culture) ?? DefaultPlcBatchReadMaxLengthInvalid;
        s_plcBatchReadMaxGapSlotsInvalid = s_rm.GetString("PlcBatchReadMaxGapSlotsInvalid", culture) ?? DefaultPlcBatchReadMaxGapSlotsInvalid;
        s_noShiftsConfigured = s_rm.GetString("NoShiftsConfigured", culture) ?? DefaultNoShiftsConfigured;
        s_shiftNameEmpty = s_rm.GetString("ShiftNameEmpty", culture) ?? DefaultShiftNameEmpty;
        s_shiftStartEndEqual = s_rm.GetString("ShiftStartEndEqual", culture) ?? DefaultShiftStartEndEqual;
        ApplyExternalOverrides(culture);
    }

    private static void ApplyExternalOverrides(CultureInfo culture)
    {
        var flags = System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Static;
        foreach (var property in typeof(ValidationMessages).GetProperties(
                     System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
        {
            if (property.PropertyType != typeof(string)
                || !LocalizationOverrideStore.TryGet("Core", property.Name, culture, out var value))
                continue;

            var fieldName = "s_" + char.ToLowerInvariant(property.Name[0]) + property.Name[1..];
            typeof(ValidationMessages).GetField(fieldName, flags)?.SetValue(null, value);
        }
    }

    private static void RestoreDefaults()
    {
        s_plcIpEmpty = DefaultPlcIpEmpty;
        s_plcIpInvalid = DefaultPlcIpInvalid;
        s_plcPortOutOfRange = DefaultPlcPortOutOfRange;
        s_plcBrandInvalid = DefaultPlcBrandInvalid;
        s_plcTimeoutOutOfRange = DefaultPlcTimeoutOutOfRange;
        s_connectionProfileEmpty = DefaultConnectionProfileEmpty;
        s_connectionProfileIdEmpty = DefaultConnectionProfileIdEmpty;
        s_connectionProfileIdDuplicate = DefaultConnectionProfileIdDuplicate;
        s_connectionProfileNameEmpty = DefaultConnectionProfileNameEmpty;
        s_connectionProfileConfigMissing = DefaultConnectionProfileConfigMissing;
        s_connectionProfileIdRequired = DefaultConnectionProfileIdRequired;
        s_connectionProfileEntryNull = DefaultConnectionProfileEntryNull;
        s_deviceConnectionProfileMissing = DefaultDeviceConnectionProfileMissing;
        s_connectionProfileAdapterAmbiguous = DefaultConnectionProfileAdapterAmbiguous;
        s_connectionProfileAdapterNotFound = DefaultConnectionProfileAdapterNotFound;
        s_dataSourceProtocolKeyEmpty = DefaultDataSourceProtocolKeyEmpty;
        s_modbusUnitIdOutOfRange = DefaultModbusUnitIdOutOfRange;
        s_modbusRegisterFunctionInvalid = DefaultModbusRegisterFunctionInvalid;
        s_modbusBitFunctionInvalid = DefaultModbusBitFunctionInvalid;
        s_modbusDataFormatInvalid = DefaultModbusDataFormatInvalid;
        s_siemensDataFormatInvalid = DefaultSiemensDataFormatInvalid;
        s_siemensModelUnsupported = DefaultSiemensModelUnsupported;
        s_siemensRackOutOfRange = DefaultSiemensRackOutOfRange;
        s_siemensSlotOutOfRange = DefaultSiemensSlotOutOfRange;
        s_siemensBatchInt32LimitOutOfRange = DefaultSiemensBatchInt32LimitOutOfRange;
        s_modbusBatchInt32LimitOutOfRange = DefaultModbusBatchInt32LimitOutOfRange;
        s_pollingIntervalInvalid = DefaultPollingIntervalInvalid;
        s_historyWriteIntervalInvalid = DefaultHistoryWriteIntervalInvalid;
        s_dashboardRefreshIntervalInvalid = DefaultDashboardRefreshIntervalInvalid;
        s_plcBatchReadMaxLengthInvalid = DefaultPlcBatchReadMaxLengthInvalid;
        s_plcBatchReadMaxGapSlotsInvalid = DefaultPlcBatchReadMaxGapSlotsInvalid;
        s_noShiftsConfigured = DefaultNoShiftsConfigured;
        s_shiftNameEmpty = DefaultShiftNameEmpty;
        s_shiftStartEndEqual = DefaultShiftStartEndEqual;
    }
}
