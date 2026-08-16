using BenchmarkDotNet.Attributes;
using CommunityToolkit.Mvvm.ComponentModel;
using Kanban.Collector.Core.Models;
using MainAPP.Models;
using System.Collections.ObjectModel;

namespace MainAPP.Benchmarks;

/// <summary>
/// 完整采集循环模拟 —— 模拟 PlcDataAcquisitionService 一轮采集的核心开销
/// （不含 PLC 网络 I/O，仅计算 CPU 侧属性更新 + OEE 计算开销）
/// </summary>
[MemoryDiagnoser]
public class PollingCycleSimulationBenchmark
{
    private const int DeviceCount = 5;
    private const int AlarmsPerDevice = 10;
    private const int DefectsPerDevice = 5;

    private List<Device> _devices = null!;
    private List<DeviceRuntime> _runtimes = null!;
    private int _counter;

    [GlobalSetup]
    public void Setup()
    {
        _devices = new List<Device>();
        _runtimes = new List<DeviceRuntime>();

        for (int i = 0; i < DeviceCount; i++)
        {
            var device = new Device
            {
                Name = $"设备{i + 1}",
                TargetCycle = 60
            };
            for (int j = 0; j < AlarmsPerDevice; j++)
                device.Alarms.Add(new Alarm { Name = $"报警{j + 1}", PlcAddress = $"M{100 + i * 20 + j}" });
            for (int j = 0; j < DefectsPerDevice; j++)
                device.Defects.Add(new Defect { Name = $"缺陷{j + 1}", PlcAddress = $"D{200 + i * 20 + j}" });

            _devices.Add(device);
            _runtimes.Add(new DeviceRuntime(device));
        }
        _counter = 0;
    }

    /// <summary>
    /// 模拟一轮完整采集：读取 OK/NG/Status + 累计 OEE 时间 + 扫描所有报警
    /// </summary>
    [Benchmark]
    public void FullPollingCycle()
    {
        _counter++;

        for (int i = 0; i < DeviceCount; i++)
        {
            var runtime = _runtimes[i];

            // TryReadOkCount
            runtime.OkProduction = _counter * 10;
            runtime.TotalOkProduction = runtime.OkProduction; // 简化：假设基线=0

            // TryReadNgCount
            runtime.NgProduction = _counter;
            runtime.TotalNgProduction = runtime.NgProduction;

            // TryReadStatusWord
            runtime.StatusWord = _counter % 3 + 1;

            // AccumulateOeeTime
            switch (runtime.StatusWord)
            {
                case 1: runtime.RunTime += 0.2; break;
                case 2: runtime.AlarmTime += 0.2; break;
                case 3: runtime.PausedTime += 0.2; break;
            }

            // 触发 OEE 计算属性（会被 BenchmarkDotNet 测量）
            _ = runtime.QualityRate;
            _ = runtime.PerformanceRate;
            _ = runtime.AvailabilityRate;
            _ = runtime.Oee;
        }

        // ScanAlarms: 遍历所有报警（仅属性访问模拟）
        foreach (var device in _devices)
        {
            foreach (var alarm in device.Alarms)
            {
                _ = alarm.Name;
                _ = alarm.PlcAddress;
            }
        }
    }

    /// <summary>
    /// 仅属性写入（不含 OEE 计算属性读取），对比 OEE 计算的开销占比
    /// </summary>
    [Benchmark]
    public void WriteOnly_NoOeeRead()
    {
        _counter++;

        for (int i = 0; i < DeviceCount; i++)
        {
            var runtime = _runtimes[i];
            runtime.OkProduction = _counter * 10;
            runtime.TotalOkProduction = runtime.OkProduction;
            runtime.NgProduction = _counter;
            runtime.TotalNgProduction = runtime.NgProduction;
            runtime.StatusWord = _counter % 3 + 1;
            runtime.RunTime += 0.2;
        }
    }
}
