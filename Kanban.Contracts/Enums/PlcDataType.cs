namespace Kanban.Contracts.Enums;

/// <summary>
/// PLC 数据类型（配方参数项的读写类型），与 IPlcDriver 支持集对齐。
/// 决定下发时的读写方法、值校验规则与地址类型（DWord 承载 Int32/Float/UInt16/String，MBit 承载 Bool）。
/// 数值已持久化到 recipes.json，禁止调整数值或插入中间值。
/// </summary>
public enum PlcDataType
{
    /// <summary>32 位整数（DWord 字区，默认）</summary>
    Int32 = 0,

    /// <summary>32 位浮点（DWord 字区）</summary>
    Float = 1,

    /// <summary>位（MBit 位区，0/1）</summary>
    Bool = 2,

    /// <summary>字符串（DWord 字区，受协议长度/编码限制）</summary>
    String = 3,

    /// <summary>16 位无符号整数（DWord 字区）</summary>
    UInt16 = 4,
}
