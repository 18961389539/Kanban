using CommunityToolkit.Mvvm.ComponentModel;

namespace Kanban.Core.Models;

public enum PlcBrand
{
    Mitsubishi = 1,
    Siemens = 2,
    ModbusTcp = 3,
    Omron = 4,
}

public enum PlcDataFormat
{
    ABCD = 0,
    BADC = 1,
    CDAB = 2,
    DCBA = 3,
}

/// <summary>
/// PLC连接配置
/// </summary>
public partial class PlcConfig : ObservableObject
{
    private int _lastAutomaticPort = GetDefaultPort(PlcBrand.Mitsubishi);

    /// <summary>共享 PLC 品牌；所有设备使用同一品牌和连接。</summary>
    [ObservableProperty]
    private PlcBrand _brand = PlcBrand.Mitsubishi;

    /// <summary>
    /// PLC IP地址
    /// </summary>
    [ObservableProperty]
    private string _ipAddress = "192.168.1.2";

    /// <summary>
    /// PLC端口
    /// </summary>
    [ObservableProperty]
    private int _port = GetDefaultPort(PlcBrand.Mitsubishi);

    /// <summary>PLC 连接超时（毫秒）。</summary>
    [ObservableProperty]
    private int _timeoutMs = 5000;

    /// <summary>Siemens S7 型号；仅 Siemens 品牌使用。</summary>
    [ObservableProperty]
    private string _siemensModel = "S1200";

    [ObservableProperty]
    private byte _siemensRack;

    [ObservableProperty]
    private byte _siemensSlot = 1;

    /// <summary>Modbus TCP Unit Id；仅 ModbusTcp 品牌使用。</summary>
    [ObservableProperty]
    private byte _modbusUnitId = 1;

    /// <summary>Modbus 地址是否从 0 开始；HslCommunication 默认值为 true。</summary>
    [ObservableProperty]
    private bool _modbusAddressStartWithZero = true;

    /// <summary>Modbus 寄存器功能码：3=Holding Register，4=Input Register。</summary>
    [ObservableProperty]
    private int _modbusRegisterFunction = 3;

    /// <summary>Modbus 线圈功能码：1=Coil，2=Discrete Input。</summary>
    [ObservableProperty]
    private int _modbusBitFunction = 1;

    [ObservableProperty]
    private PlcDataFormat _modbusDataFormat = PlcDataFormat.ABCD;

    [ObservableProperty]
    private PlcDataFormat _siemensDataFormat = PlcDataFormat.ABCD;

    /// <summary>Siemens S7 单次批量读取的最大 Int32 数量；不改变 PLC 实际协商 PDU。</summary>
    [ObservableProperty]
    private int _siemensBatchInt32Limit = 55;

    /// <summary>欧姆龙 FINS 长数据读取切割长度（单位：PLC 字），默认 500。</summary>
    [ObservableProperty]
    private int _omronReadSplits = 500;

    public static int GetDefaultPort(PlcBrand brand) => brand switch
    {
        PlcBrand.Mitsubishi => 4999,
        PlcBrand.Siemens => 102,
        PlcBrand.ModbusTcp => 502,
        PlcBrand.Omron => 9600,
        _ => 4999,
    };

    public PlcConfig CreateSnapshot() => new()
    {
        Brand = Brand,
        IpAddress = IpAddress,
        Port = Port,
        TimeoutMs = TimeoutMs,
        SiemensModel = SiemensModel,
        SiemensRack = SiemensRack,
        SiemensSlot = SiemensSlot,
        SiemensDataFormat = SiemensDataFormat,
        SiemensBatchInt32Limit = SiemensBatchInt32Limit,
        OmronReadSplits = OmronReadSplits,
        ModbusUnitId = ModbusUnitId,
        ModbusAddressStartWithZero = ModbusAddressStartWithZero,
        ModbusRegisterFunction = ModbusRegisterFunction,
        ModbusBitFunction = ModbusBitFunction,
        ModbusDataFormat = ModbusDataFormat,
    };

    partial void OnBrandChanged(PlcBrand value)
    {
        if (Port == _lastAutomaticPort)
            Port = GetDefaultPort(value);
        _lastAutomaticPort = GetDefaultPort(value);
    }
}
