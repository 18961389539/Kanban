using Kanban.Contracts.Dtos;
using Kanban.Contracts.Enums;
using Xunit;

namespace PlcSimulator.Tests.Unit;

/// <summary>
/// DeviceSimulator 状态机单元测试：使用内存字典 IO 回调 + 虚拟时间 + 注入随机种子，确定性驱动。
/// 覆盖状态转换、报警触发、计数归零，以及审查缺陷 H2 的复现（缺料×阈值报警交叉）。
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class DeviceSimulatorTests
{
    // 周一 08:00 UTC（避开周末/时段曲线，本地时区不影响纯状态断言）
    private static readonly DateTime T0 = new(2026, 1, 5, 8, 0, 0, DateTimeKind.Utc);

    private sealed class Harness
    {
        public DeviceConfig Config { get; } = new()
        {
            Id = "dev-1",
            Name = "测试机",
            OkCountAddress = "D100",
            NgCountAddress = "D102",
            StatusCountAddress = "D104",
            ProductionResetAddress = "D106",
            RecipeAddress = "D108",
            RecipeValue = 50,
            Alarms = { new AlarmConfig { Id = "a1", Name = "高温报警", PlcAddress = "M100" } },
            Defects = { new DefectConfig { Name = "毛边", PlcAddress = "D110" } },
            CounterAlarms =
            {
                new CounterAlarmConfig { Name = "停机次数", PlcAddress = "D112", MaxValue = 5, Kind = CounterAlarmKind.Stop },
                new CounterAlarmConfig { Name = "禁用停机次数", PlcAddress = "D113", MaxValue = 1, Enabled = false, Kind = CounterAlarmKind.Stop },
            },
        };

        public Dictionary<string, int> Ints { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, bool> Bools { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, float> Floats { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Strings { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static DeviceSimulator Create(Harness h, ScenarioConfig? scenario = null, Random? rng = null)
        => new(
            h.Config,
            scenario ?? QuietScenario(),
            1.0,
            (a, v) => h.Ints[a] = v,
            (a, v) => h.Bools[a] = v,
            a => h.Ints.TryGetValue(a, out var v) ? v : 0,
            a => h.Bools.TryGetValue(a, out var v) && v,
            rng,
            writeFloat: (a, v) => h.Floats[a] = v,
            writeString: (a, v) => h.Strings[a] = v);

    /// <summary>关闭所有随机特性，保证状态转换测试的确定性。</summary>
    private static ScenarioConfig QuietScenario() => new()
    {
        Name = "quiet",
        AlarmChancePerTick = 0,
        BurstChancePerTick = 0,
        EnableMaterialShortage = false,
        ShortageChancePerTick = 0,
        EnableOperatorBehavior = false,
        EnableFirstArticleInspection = false,
        EnableWarmup = false,
        WarmupPieces = 0,
        EnableCommJitter = false,
        EnableBurstStall = false,
        EnablePostAlarmRampup = false,
        EnableBatchUpdate = false,
    };

    [Fact]
    public void Start_FromIdle_SetsRunning()
    {
        var sim = Create(new Harness());
        sim.Start(T0);
        Assert.Equal(DeviceSimulator.SimStatus.Running, sim.Status);
    }

    [Fact]
    public void Pause_FromRunning_SetsIdleAndIncrementsStopCount()
    {
        var sim = Create(new Harness());
        sim.Start(T0);
        sim.Pause(T0);
        Assert.Equal(DeviceSimulator.SimStatus.Idle, sim.Status);
        Assert.Equal(1, sim.StopCount);
    }

    [Fact]
    public void Resume_FromIdle_SetsRunning()
    {
        var sim = Create(new Harness());
        sim.Start(T0);
        sim.Pause(T0);
        sim.Resume(T0);
        Assert.Equal(DeviceSimulator.SimStatus.Running, sim.Status);
    }

    [Fact]
    public void TriggerAlarm_FromRunning_SetsAlarmAndWritesBit()
    {
        var h = new Harness();
        var sim = Create(h);
        sim.Start(T0);
        sim.TriggerAlarm(T0);
        Assert.Equal(DeviceSimulator.SimStatus.Alarm, sim.Status);
        Assert.True(h.Bools.TryGetValue("M100", out var on) && on);
    }

    [Fact]
    public void ResetCounts_ZeroesCounts_KeepsStopCount()
    {
        var sim = Create(new Harness());
        sim.Start(T0);
        sim.Pause(T0);   // 停机次数 → 1
        sim.Resume(T0);
        sim.ResetCounts();
        Assert.Equal(0, sim.OkCount);
        Assert.Equal(0, sim.NgCount);
        Assert.Equal(1, sim.StopCount);
    }

    /// <summary>
    /// 复现审查缺陷 H2：缺料期间停机次数达阈值触发报警（Status→Alarm），
    /// 若缺料先于报警结束，缺料退出分支不得把 Alarm 覆盖为 Running。
    /// </summary>
    [Fact]
    public void Tick_ShortageEndsDuringThresholdAlarm_DoesNotOverrideAlarmState()
    {
        var cfg = new DeviceConfig
        {
            Id = "dev-1",
            Name = "测试机",
            OkCountAddress = "D100",
            NgCountAddress = "D102",
            StatusCountAddress = "D104",
            ProductionResetAddress = "D106",
            RecipeAddress = "D108",
            RecipeValue = 50,
            CounterAlarms = { new CounterAlarmConfig { Name = "停机次数", PlcAddress = "D112", MaxValue = 1, Kind = CounterAlarmKind.Stop } },
        };
        var ints = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var bools = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var sc = new ScenarioConfig
        {
            Name = "h2-repro",
            EnableMaterialShortage = true,
            ShortageChancePerTick = 1.0,
            ShortageMinSec = 10,
            ShortageMaxSec = 10,
            EnableCounterAlarmThreshold = true,
            ThresholdAlarmSec = 100,
            AlarmChancePerTick = 0,
            BurstChancePerTick = 0,
            EnableOperatorBehavior = false,
            EnableFirstArticleInspection = false,
            EnableWarmup = false,
            WarmupPieces = 0,
            EnableCommJitter = false,
            EnableBurstStall = false,
            EnablePostAlarmRampup = false,
            EnableBatchUpdate = false,
        };
        var sim = new DeviceSimulator(cfg, sc, 1.0,
            (a, v) => ints[a] = v,
            (a, v) => bools[a] = v,
            a => ints.TryGetValue(a, out var v) ? v : 0,
            a => bools.TryGetValue(a, out var v) && v,
            new Random(42));

        sim.Start(T0);
        sim.Pause(T0);          // 停机次数 0→1
        sim.Resume(T0);

        sim.Tick(T0.AddSeconds(1));    // 触发缺料 → 停机次数 1→2 → 超阈值(MaxValue=1) → Alarm
        Assert.Equal(DeviceSimulator.SimStatus.Alarm, sim.Status);

        // 关键断言（H2 回归）：缺料先结束(10s)而报警未结束(100s)，缺料退出分支不得把 Alarm 覆盖为 Running。
        sim.Tick(T0.AddSeconds(11));
        Assert.Equal(DeviceSimulator.SimStatus.Alarm, sim.Status);
        // 注：此处不再推进到报警结束，因 ShortageChancePerTick=1.0 会在恢复 Running 后再次触发缺料，
        // 干扰「恢复→Running」断言；报警恢复路径由 TriggerAlarm_RecoversAfterDuration 单独覆盖。
    }

    [Fact]
    public void TriggerAlarm_RecoversAfterDuration()
    {
        var h = new Harness();
        var sc = new ScenarioConfig
        {
            Name = "alarm-recover",
            AlarmChancePerTick = 0,
            BurstChancePerTick = 0,
            EnableMaterialShortage = false,
            EnableOperatorBehavior = false,
            EnableFirstArticleInspection = false,
            EnableWarmup = false,
            WarmupPieces = 0,
            EnableCommJitter = false,
            EnableBurstStall = false,
            EnablePostAlarmRampup = false,
            EnableBatchUpdate = false,
            AlarmMinSec = 10,
            AlarmMaxSec = 10,
        };
        var sim = Create(h, sc, new Random(42));
        sim.Start(T0);
        sim.TriggerAlarm(T0);
        Assert.Equal(DeviceSimulator.SimStatus.Alarm, sim.Status);

        sim.Tick(T0.AddSeconds(11));   // 报警持续 10s 已结束
        Assert.Equal(DeviceSimulator.SimStatus.Running, sim.Status);
        // 报警恢复后应清除触发的报警位
        Assert.True(!h.Bools.TryGetValue("M100", out var on) || !on);
    }

    [Fact]
    public void ConfiguredSources_WriteTypedValuesToConfiguredAddresses()
    {
        var h = new Harness();
        h.Config.Sources.Add(new DataSourceConfigDto
        {
            Id = "source-typed",
            DeviceId = h.Config.Id,
            Name = "类型源",
            Values = new List<DataSourceValueConfigDto>
            {
                new() { Id = "int", Name = "整数", DataType = DataSourceValueType.Int32, PlcAddress = "D500", ExpectedValue = 7 },
                new() { Id = "float", Name = "浮点", DataType = DataSourceValueType.Float32, PlcAddress = "D520", FloatExpectedValue = 1.25f },
                new() { Id = "bool", Name = "布尔", DataType = DataSourceValueType.Bool, PlcAddress = "M10", BoolExpectedValue = false },
                new() { Id = "string", Name = "文本", DataType = DataSourceValueType.String, PlcAddress = "D540", StringExpectedValue = "READY", StringLength = 16 },
            },
        });
        var sim = Create(h);

        sim.Tick(T0);

        Assert.Equal(7, h.Ints["D500"]);
        Assert.Equal(1.25f, h.Floats["D520"]);
        Assert.False(h.Bools["M10"]);
        Assert.Equal("READY", h.Strings["D540"]);
    }

    [Fact]
    public void ConfiguredSource_TriggerHandshake_IsIndependentAndUsesConfiguredValues()
    {
        var h = new Harness();
        h.Config.Sources.Add(new DataSourceConfigDto
        {
            Id = "source-trigger",
            DeviceId = h.Config.Id,
            Name = "触发源",
            TriggerAddress = "D510",
            TriggerValue = 3,
            AckValue = 4,
            Values = new List<DataSourceValueConfigDto>
            {
                new() { Id = "value", Name = "采样值", DataType = DataSourceValueType.Int32, PlcAddress = "D500", ExpectedValue = 7 },
            },
        });
        h.Config.Sources.Add(new DataSourceConfigDto
        {
            Id = "source-trigger-2",
            DeviceId = h.Config.Id,
            Name = "第二触发源",
            TriggerAddress = "D560",
            TriggerValue = 5,
            AckValue = 6,
            Values = new List<DataSourceValueConfigDto>
            {
                new() { Id = "value-2", Name = "第二采样值", DataType = DataSourceValueType.Int32, PlcAddress = "D502", ExpectedValue = 9 },
            },
        });
        var sim = Create(h);

        sim.Tick(T0);
        Assert.Equal(3, h.Ints["D510"]);
        Assert.Equal(5, h.Ints["D560"]);

        h.Ints["D510"] = 4;
        h.Ints["D560"] = 6;
        sim.Tick(T0.AddSeconds(1));
        Assert.Equal(0, h.Ints["D510"]);
        Assert.Equal(0, h.Ints["D560"]);
    }

    [Fact]
    public void DisabledSourceAndValue_DoNotWritePlc()
    {
        var h = new Harness();
        h.Config.Sources.Add(new DataSourceConfigDto
        {
            Id = "source-disabled",
            DeviceId = h.Config.Id,
            Name = "禁用源",
            Enabled = false,
            Values = new List<DataSourceValueConfigDto>
            {
                new() { Id = "disabled-source-value", Name = "值", PlcAddress = "D600", ExpectedValue = 1 },
            },
        });
        h.Config.Sources.Add(new DataSourceConfigDto
        {
            Id = "source-value-disabled",
            DeviceId = h.Config.Id,
            Name = "部分禁用源",
            Values = new List<DataSourceValueConfigDto>
            {
                new() { Id = "disabled-value", Name = "值", Enabled = false, PlcAddress = "D602", ExpectedValue = 1 },
            },
        });
        var sim = Create(h);

        sim.Tick(T0);

        Assert.False(h.Ints.ContainsKey("D600"));
        Assert.False(h.Ints.ContainsKey("D602"));
    }

    [Fact]
    public void DisabledCounterAlarm_IsNotUpdatedOnPause()
    {
        var h = new Harness();
        var sim = Create(h);

        sim.Start(T0);
        sim.Pause(T0);

        Assert.Equal(1, h.Ints["D112"]);
        Assert.False(h.Ints.ContainsKey("D113"));
    }
}
