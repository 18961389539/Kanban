using Kanban.Core.Services;
using MainAPP.Services;

namespace MainAPP.Tests.Unit;

/// <summary>
/// IPlcDriver 的内存测试桩：用 Dictionary 承载各地址的预设值，
/// 完全不依赖 HslCommunication 或真实 PLC。
/// 用于在纯单元测试中模拟 PLC 读写行为：
/// - 预设地址值（SetInt32 / SetBool）后，ReadXxx 返回对应值
/// - 未预设的地址返回失败，模拟"PLC 读取失败"
/// - WriteXxx 记录写入历史，便于断言"是否触发了清零"等行为
/// - 支持按地址配置"读取失败"，模拟 PLC 抖动/超时
/// </summary>
internal sealed class FakePlcDriver : IPlcDriver
{
    /// <summary>DWord 地址 → 当前值（int）</summary>
    private readonly Dictionary<string, int> _intValues = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>M 位地址 → 当前值（bool）</summary>
    private readonly Dictionary<string, bool> _boolValues = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>配置为读取失败的地址集合（按地址模拟故障）</summary>
    private readonly HashSet<string> _failingAddresses = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>写入历史记录（按时间顺序），便于断言 PlcDataAcquisitionService 是否触发了清零等</summary>
    public List<WriteRecord> WriteHistory { get; } = new();

    /// <summary>Connect/Disconnect 调用历史，便于断言连接管理行为</summary>
    public int ConnectCallCount { get; private set; }

    /// <summary>Disconnect 调用次数，便于断言主动断开行为</summary>
    public int DisconnectCallCount { get; private set; }

    /// <summary>Configure 调用次数，便于断言"每次连接前应用最新配置"</summary>
    public int ConfigureCallCount { get; private set; }

    public int ReadBoolCallCount { get; private set; }

    public int ReadBoolBatchCallCount { get; private set; }

    public List<(string Address, ushort Length)> ReadBoolBatchHistory { get; } = [];

    public int ReadInt32CallCount { get; private set; }

    public int ReadInt32BatchCallCount { get; private set; }

    public List<(string Address, ushort Length)> ReadInt32BatchHistory { get; } = [];

    /// <summary>最近一次 Configure 收到的 IP 地址，便于断言配置变更生效</summary>
    public string LastConfiguredIpAddress { get; private set; } = "";

    /// <summary>最近一次 Configure 收到的端口，便于断言配置变更生效</summary>
    public int LastConfiguredPort { get; private set; }

    public bool IsConnected { get; set; } = true;

    /// <summary>设置为 true 时 Connect 返回失败，模拟 PLC 不可达</summary>
    public bool ShouldFailConnect { get; set; }

    /// <summary>
    /// 设置后 Connect() 将抛出指定异常（而非返回 Fail），用于模拟 HslCommunication 透传异常的场景，
    /// 验证 PlcDataAcquisitionService.PollingLoopAsync 外层 catch 的异常分类与不调 MarkDisconnected 行为。
    /// </summary>
    public Exception? ConnectException { get; set; }

    /// <summary>
    /// 设置后 ReadInt32() 将抛出指定异常（而非返回 Fail），用于模拟 HslCommunication
    /// 在 ScanAlarms/ScanDefects/ScanCountAlarms 读取阶段透传通信异常的场景，
    /// 验证 TryScan 的异常分类与 MarkDisconnected(ScanException) 触发逻辑。
    /// 其他读方法（ReadUInt16/ReadBool）不受此属性影响。
    /// </summary>
    public Exception? ReadInt32Exception { get; set; }

    /// <summary>
    /// 设置后 ReadBool() 将抛出指定异常（而非返回 Fail），用于模拟 HslCommunication
    /// 在 ScanAlarms 读取 M 位时透传通信异常的场景。
    /// </summary>
    public Exception? ReadBoolException { get; set; }

    /// <summary>记录一次 PLC 写入操作</summary>
    public sealed record WriteRecord(string Address, object Value, DateTime Timestamp);

    public void Configure(string ipAddress, int port)
    {
        ConfigureCallCount++;
        LastConfiguredIpAddress = ipAddress;
        LastConfiguredPort = port;
    }

    public PlcOperationResult Connect()
    {
        ConnectCallCount++;
        if (ConnectException is not null)
            throw ConnectException;
        if (ShouldFailConnect)
        {
            IsConnected = false;
            return PlcOperationResult.Fail("FakePlcDriver: 配置为连接失败");
        }
        IsConnected = true;
        return PlcOperationResult.Success();
    }

    public PlcOperationResult Disconnect()
    {
        DisconnectCallCount++;
        IsConnected = false;
        return PlcOperationResult.Success();
    }

    public PlcOperationResult<ushort> ReadUInt16(string address)
    {
        if (_failingAddresses.Contains(address))
            return PlcOperationResult<ushort>.Fail($"FakePlcDriver: {address} 配置为读取失败");
        if (_intValues.TryGetValue(address, out var v))
            return PlcOperationResult<ushort>.Success((ushort)v);
        return PlcOperationResult<ushort>.Fail($"FakePlcDriver: {address} 未预设值");
    }

    public PlcOperationResult<int> ReadInt32(string address)
    {
        ReadInt32CallCount++;
        // 异常注入优先于失败标记与值预设：模拟 HslCommunication 在底层 socket 已断时
        // 直接抛 SocketException/ObjectDisposedException 而非返回 OperateResult 的真实行为
        if (ReadInt32Exception is not null)
            throw ReadInt32Exception;
        if (_failingAddresses.Contains(address))
            return PlcOperationResult<int>.Fail($"FakePlcDriver: {address} 配置为读取失败");
        if (_intValues.TryGetValue(address, out var v))
            return PlcOperationResult<int>.Success(v);
        return PlcOperationResult<int>.Fail($"FakePlcDriver: {address} 未预设值");
    }

    public PlcOperationResult<int[]> ReadInt32Batch(string address, ushort length)
    {
        ReadInt32BatchCallCount++;
        ReadInt32BatchHistory.Add((address, length));
        if (ReadInt32Exception is not null)
            throw ReadInt32Exception;
        var parsed = PlcAddressParser.Parse(address);
        if (!parsed.IsValid || parsed.Type != PlcAddressType.DWord)
            return PlcOperationResult<int[]>.Fail($"FakePlcDriver: {address} 不是有效的 D 地址");

        var values = new int[length];
        for (var index = 0; index < length; index++)
        {
            var itemAddress = $"{parsed.AddressGroup}{parsed.AddressOffset + index * parsed.AddressStride}";
            if (_failingAddresses.Contains(itemAddress))
                return PlcOperationResult<int[]>.Fail($"FakePlcDriver: {itemAddress} 配置为读取失败");
            if (!_intValues.TryGetValue(itemAddress, out values[index]))
                return PlcOperationResult<int[]>.Fail($"FakePlcDriver: {itemAddress} 未预设值");
        }
        return PlcOperationResult<int[]>.Success(values);
    }

    public PlcOperationResult<bool> ReadBool(string address)
    {
        ReadBoolCallCount++;
        // 异常注入优先：同 ReadInt32
        if (ReadBoolException is not null)
            throw ReadBoolException;
        if (_failingAddresses.Contains(address))
            return PlcOperationResult<bool>.Fail($"FakePlcDriver: {address} 配置为读取失败");
        if (_boolValues.TryGetValue(address, out var v))
            return PlcOperationResult<bool>.Success(v);
        return PlcOperationResult<bool>.Fail($"FakePlcDriver: {address} 未预设值");
    }

    public PlcOperationResult<bool[]> ReadBoolBatch(string address, ushort length)
    {
        ReadBoolBatchCallCount++;
        ReadBoolBatchHistory.Add((address, length));
        if (ReadBoolException is not null)
            throw ReadBoolException;
        var parsed = PlcAddressParser.Parse(address);
        if (!parsed.IsValid || parsed.Type != PlcAddressType.MBit)
            return PlcOperationResult<bool[]>.Fail($"FakePlcDriver: {address} 不是有效的 M 地址");

        var values = new bool[length];
        for (var index = 0; index < length; index++)
        {
            var itemAddress = $"M{parsed.AddressOffset + index * parsed.AddressStride}";
            if (_failingAddresses.Contains(itemAddress))
                return PlcOperationResult<bool[]>.Fail($"FakePlcDriver: {itemAddress} 配置为读取失败");
            if (!_boolValues.TryGetValue(itemAddress, out values[index]))
                return PlcOperationResult<bool[]>.Fail($"FakePlcDriver: {itemAddress} 未预设值");
        }
        return PlcOperationResult<bool[]>.Success(values);
    }

    public PlcOperationResult WriteUInt16(string address, ushort value)
    {
        _intValues[address] = value;
        WriteHistory.Add(new WriteRecord(address, value, DateTime.Now));
        return PlcOperationResult.Success();
    }

    public PlcOperationResult WriteInt32(string address, int value)
    {
        _intValues[address] = value;
        WriteHistory.Add(new WriteRecord(address, value, DateTime.Now));
        return PlcOperationResult.Success();
    }

    public PlcOperationResult WriteBool(string address, bool value)
    {
        _boolValues[address] = value;
        WriteHistory.Add(new WriteRecord(address, value, DateTime.Now));
        return PlcOperationResult.Success();
    }

    // ──────────── 测试辅助方法 ────────────

    /// <summary>预设 D 字地址的当前值，后续 ReadInt32 返回此值</summary>
    public void SetInt32(string address, int value) => _intValues[address] = value;

    /// <summary>预设 M 位地址的当前值，后续 ReadBool 返回此值</summary>
    public void SetBool(string address, bool value) => _boolValues[address] = value;

    /// <summary>配置指定地址读取时返回失败，模拟 PLC 抖动/超时</summary>
    public void SetFailing(string address) => _failingAddresses.Add(address);

    /// <summary>清除指定地址的失败标记，恢复正常读取</summary>
    public void ClearFailing(string address) => _failingAddresses.Remove(address);

    /// <summary>读取 D 字地址当前值（测试断言用）</summary>
    public int GetInt32(string address) => _intValues.TryGetValue(address, out var v) ? v : 0;

    /// <summary>读取 M 位地址当前值（测试断言用）</summary>
    public bool GetBool(string address) => _boolValues.TryGetValue(address, out var v) && v;

    /// <summary>
    /// IPlcDriver 继承 IDisposable 后必须实现。测试桩无资源需释放，空实现即可。
    /// </summary>
    public void Dispose() { }
}
