using BenchmarkDotNet.Attributes;
using MainAPP.Services;

namespace MainAPP.Benchmarks;

/// <summary>
/// PLC 地址解析性能基准。每次 PLC 轮询都会对 OK/NG/状态/报警地址做格式校验，
/// 是采集循环中的高频纯函数路径。
/// </summary>
[MemoryDiagnoser]
public class PlcAddressParserBenchmark
{
    [Params("D12001", "M10001", "X999", "D0")]
    public string Address { get; set; } = "D12001";

    [Benchmark]
    public PlcAddressParseResult Parse() => PlcAddressParser.Parse(Address);

    [Benchmark]
    public bool IsDWord() => PlcAddressParser.IsDWord(Address);

    [Benchmark]
    public bool IsMBit() => PlcAddressParser.IsMBit(Address);
}
