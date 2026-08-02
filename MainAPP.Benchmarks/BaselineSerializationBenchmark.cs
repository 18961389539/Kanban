using System;
using System.Collections.Generic;
using System.IO;
using MainAPP.Services;
using BenchmarkDotNet.Attributes;

namespace MainAPP.Benchmarks;

/// <summary>
/// 产量基线持久化（ProductionBaselineStore.SaveBaselines / Load）性能基准。
/// 每次采集轮询在基线变更时写 baselines.json（原子写入），软件重启时读取。
/// 基线条目数随设备数增长。
/// </summary>
[MemoryDiagnoser]
public class BaselineSerializationBenchmark
{
    [Params(1, 10, 100)]
    public int EntryCount;

    private AppSettings _appSettings = null!;
    private ProductionBaselineStore _store = null!;
    private Dictionary<string, int> _baselines = null!;
    private const string Shift = "白班|08:00:00|20:00:00";

    [GlobalSetup]
    public void Setup()
    {
        var dir = Path.Combine(Path.GetTempPath(), "KanbanBaselineBench_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _appSettings = new AppSettings { ConfigDirectory = dir };
        _store = new ProductionBaselineStore(_appSettings);
        _baselines = new Dictionary<string, int>(EntryCount);
        for (int i = 0; i < EntryCount; i++)
            _baselines["dev-" + i + "_ok_base"] = i * 100;
    }

    [Benchmark]
    public void SaveAndLoad()
    {
        _store.SaveBaselines(_baselines, Shift);
        var loaded = new ProductionBaselineStore(_appSettings);
        loaded.Load();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_appSettings.ConfigDirectory, true); } catch { }
    }
}
