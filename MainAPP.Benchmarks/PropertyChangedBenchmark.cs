using BenchmarkDotNet.Attributes;
using CommunityToolkit.Mvvm.ComponentModel;
using MainAPP.Models;
using System.Collections.ObjectModel;

namespace MainAPP.Benchmarks;

/// <summary>
/// INPC 属性变更通知开销 —— 模拟采集循环每轮触发大量属性变更
/// </summary>
[MemoryDiagnoser]
public class PropertyChangedBenchmark
{
    private DeviceRuntime? _runtime;
    private int _counter;

    [GlobalSetup]
    public void Setup()
    {
        var device = new Device
        {
            Name = "BenchDevice",
            TargetCycle = 60,
            OkCountAddress = "D100",
            NgCountAddress = "D102",
            StatusCountAddress = "D104"
        };
        _runtime = new DeviceRuntime(device);
        _counter = 0;
    }

    /// <summary>
    /// 模拟一个设备一次采集周期的属性写入（OK/NG/Status + OEE时间）
    /// </summary>
    [Benchmark]
    public void SingleDevicePollCycle()
    {
        _counter++;
        _runtime!.OkProduction = _counter * 10;
        _runtime.NgProduction = _counter;
        _runtime.StatusWord = _counter % 3 + 1;
        _runtime.RunTime += 0.2;
    }

    /// <summary>
    /// 模拟 5 个设备一次采集周期的属性写入
    /// </summary>
    [Benchmark]
    public void FiveDevicePollCycle()
    {
        for (int i = 0; i < 5; i++)
        {
            _counter++;
            _runtime!.OkProduction = _counter * 10;
            _runtime.NgProduction = _counter;
            _runtime.StatusWord = _counter % 3 + 1;
            _runtime.RunTime += 0.2;
        }
    }
}
