using BenchmarkDotNet.Attributes;
using Kanban.Core.Models;
using MainAPP.Models;
using System.Collections.ObjectModel;
using System.Text.Json;

namespace MainAPP.Benchmarks;

/// <summary>
/// JSON 序列化性能 —— DeviceRepository.SaveAll 路径
/// </summary>
[MemoryDiagnoser]
public class JsonSerializationBenchmark
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private List<Device> _devices = null!;

    [Params(1, 5, 10)]
    public int DeviceCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _devices = new List<Device>();
        for (int i = 0; i < DeviceCount; i++)
        {
            var device = new Device
            {
                Name = $"设备{i + 1}",
                TargetCycle = 60,
                OkCountAddress = $"D{100 + i * 10}",
                NgCountAddress = $"D{102 + i * 10}",
                StatusCountAddress = $"D{104 + i * 10}",
                RecipeName = $"配方{i + 1}",
                RecipeValue = i * 100,
                RecipeAddress = $"D{500 + i}"
            };
            for (int j = 0; j < 5; j++)
            {
                device.Alarms.Add(new Alarm
                {
                    Name = $"报警{j + 1}",
                    PlcAddress = $"M{100 + i * 20 + j}",
                    Level = AlarmLevel.Medium
                });
                device.Defects.Add(new Defect
                {
                    Name = $"缺陷{j + 1}",
                    PlcAddress = $"D{200 + i * 20 + j}",
                    Severity = DefectSeverity.Major
                });
            }
            _devices.Add(device);
        }
    }

    [Benchmark]
    public string Serialize()
    {
        return JsonSerializer.Serialize(_devices, JsonOptions);
    }

    [Benchmark]
    public List<Device>? RoundTrip()
    {
        var json = JsonSerializer.Serialize(_devices, JsonOptions);
        return JsonSerializer.Deserialize<List<Device>>(json, JsonOptions);
    }
}
