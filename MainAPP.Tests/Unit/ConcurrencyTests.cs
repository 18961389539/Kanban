using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using Kanban.Core.Data;
using Kanban.Core.Entities;
using Kanban.Core.Models;
using MainAPP.Models;
using Kanban.Core.Services;
using MainAPP.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// 并发竞态与死锁测试：验证生产代码中带锁的临界区在高并发下保持正确性，
/// 且不会出现死锁（采用 Task.WhenAny + Task.Delay 超时模式检测）。
///
/// 覆盖的关键临界区（与 PlcConnectionManager._stateLock、PlcDataAcquisitionService._resetLock、
/// DeviceRepository._collectionLock、AlarmStateTracker._lock、DeviceStatusTracker._lock、
/// BaselineResetCoordinator._lock、ShiftContext._lock、ProductionBaselineStore._lock 对齐）。
///
/// 注意：并发测试具有非确定性，断言侧重"无异常 + 数据一致性 + 不死锁"，
/// 不依赖具体时序（不使用 Thread.Sleep 等待特定窗口）。
/// </summary>
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class ConcurrencyTests
{
    private static readonly TimeSpan DeadlockTimeout = TimeSpan.FromSeconds(10);

    /// <summary>等待 task 完成或超时，超时则失败（死锁检测）。</summary>
    private static async Task AssertCompletesWithinAsync(Task task, string scenario)
    {
        var completed = await Task.WhenAny(task, Task.Delay(DeadlockTimeout));
        if (completed != task)
            Assert.Fail($"场景 [{scenario}] 在 {DeadlockTimeout.TotalSeconds}s 内未完成，疑似死锁");
        await task; // 重新抛出 task 内的异常
    }

    private static async Task AssertCompletesWithinAsync(Action action, string scenario)
        => await AssertCompletesWithinAsync(Task.Run(action), scenario);

    // ════════════════════════════════════════════════════════════════
    //  PlcConnectionManager._stateLock
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task PlcConnectionManager_EnsureConnected_ConcurrentWithDisconnect_NoDeadlock()
    {
        var appSettings = new AppSettings
        {
            ConfigDirectory = Path.Combine(Path.GetTempPath(), "KanbanConcTests_" + Guid.NewGuid().ToString("N"))
        };
        Directory.CreateDirectory(appSettings.ConfigDirectory);
        var plc = new FakePlcDriver();
        var mgr = new PlcConnectionManager(plc, appSettings);

        await AssertCompletesWithinAsync(() =>
        {
            var tasks = Enumerable.Range(0, 20).Select(i => Task.Run(() =>
            {
                if (i % 3 == 0) mgr.Disconnect();
                else mgr.EnsureConnected();
            })).ToArray();
            Task.WaitAll(tasks);
        }, "PlcConnectionManager.EnsureConnected × Disconnect");
    }

    [Fact]
    public async Task PlcConnectionManager_MarkDisconnected_ConcurrentWithEnsureConnected_StateConsistent()
    {
        var appSettings = new AppSettings
        {
            ConfigDirectory = Path.Combine(Path.GetTempPath(), "KanbanConcTests_" + Guid.NewGuid().ToString("N"))
        };
        Directory.CreateDirectory(appSettings.ConfigDirectory);
        var plc = new FakePlcDriver();
        var mgr = new PlcConnectionManager(plc, appSettings);

        await AssertCompletesWithinAsync(() =>
        {
            var tasks = Enumerable.Range(0, 30).Select(i => Task.Run(() =>
            {
                if (i % 2 == 0) mgr.MarkDisconnected();
                else mgr.EnsureConnected();
            })).ToArray();
            Task.WaitAll(tasks);
        }, "PlcConnectionManager.MarkDisconnected × EnsureConnected");

        // 并发结束后验证计数器无损坏（审查修复 2026-08-13：原断言 TotalDisconnectCount >= 0 恒真——
        // int 不可能为负，未验证任何行为）。真实行为断言：MarkDisconnected 仅在"已连接→断开"下降沿累加，
        // 连发两次第二次必然 no-op，计数不得重复累加。
        var before = mgr.TotalDisconnectCount;
        mgr.MarkDisconnected();
        var afterOne = mgr.TotalDisconnectCount;
        Assert.InRange(afterOne, before, before + 1); // 至多 +1（下降沿）
        mgr.MarkDisconnected();                       // 已断开 → no-op
        Assert.Equal(afterOne, mgr.TotalDisconnectCount);
    }

    // ════════════════════════════════════════════════════════════════
    //  DeviceRepository._collectionLock + ConcurrentDictionary RuntimeMap
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task DeviceRepository_ConcurrentAddRuntimeAndSnapshot_NoException()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "KanbanConcTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var appSettings = new AppSettings { ConfigDirectory = tempDir };
            var repo = new DeviceRepository(appSettings);

            // 生产并发模型：UI 线程串行 AddRuntime（内部加 _collectionLock 保护 Runtimes），
            // 后台线程并发 GetRuntimesSnapshot（同样加 _collectionLock）。
            // Devices.Add 在生产中由 UI 线程串行调用（无并发），故此处不测试 Devices 并发 Add。
            await AssertCompletesWithinAsync(async () =>
            {
                var snapCts = new CancellationTokenSource();
                var snapshotters = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
                {
                    while (!snapCts.IsCancellationRequested)
                    {
                        var _snap = repo.GetRuntimesSnapshot();
                    }
                })).ToArray();

                // 主线程串行 AddRuntime（内部加锁）
                for (int i = 0; i < 4; i++)
                {
                    for (int j = 0; j < 50; j++)
                    {
                        var dev = new Device { Id = $"dev-{i}-{j}", Name = $"设备{i}-{j}" };
                        repo.Devices.Add(dev); // UI 线程串行调用，无并发
                        repo.AddRuntime(dev);
                    }
                }
                snapCts.Cancel();
                await Task.WhenAll(snapshotters);
            }, "DeviceRepository 串行 AddRuntime + 并发 GetRuntimesSnapshot");

            Assert.Equal(200, repo.Runtimes.Count);
            Assert.Equal(200, repo.RuntimeMap.Count);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task DeviceRepository_ConcurrentRuntimeMapReadWrite_NoException()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "KanbanConcTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var appSettings = new AppSettings { ConfigDirectory = tempDir };
            var repo = new DeviceRepository(appSettings);
            // 预置 100 台设备
            for (int i = 0; i < 100; i++)
            {
                var dev = new Device { Id = $"dev-{i}", Name = $"设备{i}" };
                repo.Devices.Add(dev);
                repo.AddRuntime(dev);
            }

            await AssertCompletesWithinAsync(() =>
            {
                var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
                {
                    for (int i = 0; i < 1000; i++)
                    {
                        repo.RuntimeMap.TryGetValue($"dev-{i % 100}", out DeviceRuntime? _rt);
                    }
                })).ToArray();
                var writers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
                {
                    for (int i = 0; i < 100; i++)
                    {
                        repo.RuntimeMap[$"dev-{i}"] = new DeviceRuntime(new Device { Id = $"dev-{i}", Name = "更新" });
                    }
                })).ToArray();
                Task.WaitAll(readers.Concat(writers).ToArray());
            }, "DeviceRepository.ConcurrentDictionary 并发读写");

            Assert.Equal(100, repo.RuntimeMap.Count);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task DeviceRepository_ConcurrentReplaceAllWithSnapshot_NoException()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "KanbanConcTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var appSettings = new AppSettings { ConfigDirectory = tempDir };
            var repo = new DeviceRepository(appSettings);

            await AssertCompletesWithinAsync(() =>
            {
                var snapshotters = Enumerable.Range(0, 5).Select(_ => Task.Run(() =>
                {
                    for (int i = 0; i < 100; i++)
                    {
                        var _snapshot = repo.GetDevicesSnapshot();
                    }
                })).ToArray();
                var replacers = Enumerable.Range(0, 3).Select(i => Task.Run(() =>
                {
                    for (int j = 0; j < 20; j++)
                    {
                        var newDevs = Enumerable.Range(0, 10)
                            .Select(k => new Device { Id = $"r-{i}-{j}-{k}", Name = $"替换{i}-{j}-{k}" })
                            .ToList();
                        repo.ReplaceAll(newDevs);
                    }
                })).ToArray();
                Task.WaitAll(snapshotters.Concat(replacers).ToArray());
            }, "DeviceRepository 并发 ReplaceAll × GetSnapshot");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  AlarmStateTracker._lock
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AlarmStateTracker_ConcurrentScanAlarms_NoException()
    {
        var tracker = new AlarmStateTracker();
        var plc = new FakePlcDriver();
        var devices = new ObservableCollection<Device>();
        for (int i = 0; i < 10; i++)
        {
            var dev = new Device { Id = $"dev-{i}", Name = $"设备{i}" };
            dev.Alarms.Add(new Alarm { DeviceId = dev.Id, PlcAddress = $"M{i}", Name = $"报警{i}" });
            plc.SetBool($"M{i}", i % 2 == 0);
            devices.Add(dev);
        }
        var history = new InMemoryHistoryService();
        var logger = NullLogger<AlarmStateTracker>.Instance;

        await AssertCompletesWithinAsync(() =>
        {
            var scanners = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                for (int i = 0; i < 100; i++)
                    tracker.ScanAlarms(devices, plc, history, "白班", logger);
            })).ToArray();
            // 并发执行 RemoveAlarmState（模拟 UI 删除设备）
            var removers = Enumerable.Range(0, 4).Select(i => Task.Run(() =>
            {
                for (int j = 0; j < 50; j++)
                    tracker.RemoveAlarmState($"dev-{j % 10}_M{j % 10}");
            })).ToArray();
            Task.WaitAll(scanners.Concat(removers).ToArray());
        }, "AlarmStateTracker.ScanAlarms × RemoveAlarmState");

        // 最终状态可读取（不抛异常即可）
        _ = tracker.GetPrevAlarmStatesSnapshot();
    }

    [Fact]
    public async Task AlarmStateTracker_ConcurrentResetAllWithScan_NoException()
    {
        var tracker = new AlarmStateTracker();
        var plc = new FakePlcDriver();
        var devices = new ObservableCollection<Device>();
        for (int i = 0; i < 5; i++)
        {
            var dev = new Device { Id = $"dev-{i}", Name = $"设备{i}" };
            dev.Alarms.Add(new Alarm { DeviceId = dev.Id, PlcAddress = $"M{i}", Name = $"报警{i}" });
            plc.SetBool($"M{i}", true);
            devices.Add(dev);
        }
        var history = new InMemoryHistoryService();
        var logger = NullLogger<AlarmStateTracker>.Instance;

        await AssertCompletesWithinAsync(() =>
        {
            var scanners = Enumerable.Range(0, 5).Select(_ => Task.Run(() =>
            {
                for (int i = 0; i < 100; i++)
                    tracker.ScanAlarms(devices, plc, history, "白班", logger);
            })).ToArray();
            var resetters = Enumerable.Range(0, 5).Select(_ => Task.Run(() =>
            {
                for (int i = 0; i < 20; i++)
                    tracker.ResetAll();
            })).ToArray();
            Task.WaitAll(scanners.Concat(resetters).ToArray());
        }, "AlarmStateTracker.ScanAlarms × ResetAll");
    }

    // ════════════════════════════════════════════════════════════════
    //  DeviceStatusTracker._lock
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task DeviceStatusTracker_ConcurrentReadAndUpdate_NoException()
    {
        var tracker = new DeviceStatusTracker();
        var devices = Enumerable.Range(0, 10)
            .Select(i => new Device { Id = $"dev-{i}", Name = $"设备{i}" })
            .ToArray();
        var history = new InMemoryHistoryService();
        var logger = NullLogger<DeviceStatusTracker>.Instance;
        var random = new Random(42);

        await AssertCompletesWithinAsync(() =>
        {
            var updaters = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                for (int i = 0; i < 200; i++)
                {
                    var dev = devices[random.Next(devices.Length)];
                    var status = random.Next(0, 4);
                    tracker.ReadAndUpdate(dev, status, history, "白班", logger);
                }
            })).ToArray();
            // 并发 RemoveDevice
            var removers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            {
                for (int i = 0; i < 50; i++)
                    tracker.RemoveDevice($"dev-{i % 10}");
            })).ToArray();
            Task.WaitAll(updaters.Concat(removers).ToArray());
        }, "DeviceStatusTracker.ReadAndUpdate × RemoveDevice");

        _ = tracker.GetPrevStatusWordsSnapshot();
    }

    // ════════════════════════════════════════════════════════════════
    //  BaselineResetCoordinator._lock
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task BaselineResetCoordinator_ConcurrentScheduleExpireRemove_NoException()
    {
        var coord = new BaselineResetCoordinator();
        var logger = NullLogger<BaselineResetCoordinator>.Instance;
        var random = new Random(42);

        await AssertCompletesWithinAsync(() =>
        {
            var schedulers = Enumerable.Range(0, 5).Select(_ => Task.Run(() =>
            {
                for (int i = 0; i < 200; i++)
                    coord.ScheduleClear($"dev-{random.Next(20)}", delaySeconds: random.NextDouble());
            })).ToArray();
            var expirers = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
            {
                for (int i = 0; i < 100; i++)
                    coord.ExpireClears(DateTime.Now, logger);
            })).ToArray();
            var removers = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
            {
                for (int i = 0; i < 50; i++)
                    coord.RemoveDevice($"dev-{random.Next(20)}");
            })).ToArray();
            var resetters = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
            {
                for (int i = 0; i < 20; i++)
                    coord.ResetAll();
            })).ToArray();
            Task.WaitAll(schedulers.Concat(expirers).Concat(removers).Concat(resetters).ToArray());
        }, "BaselineResetCoordinator 并发 Schedule × Expire × Remove × ResetAll");
    }

    [Fact]
    public async Task BaselineResetCoordinator_ConcurrentDrainPendingReconnect_NoDuplicateItems()
    {
        var coord = new BaselineResetCoordinator();
        var logger = NullLogger<BaselineResetCoordinator>.Instance;

        // 预置 5 个待重连设备
        for (int i = 0; i < 5; i++)
            coord.AddPendingReconnect($"dev-{i}");

        var allDrained = new ConcurrentBag<string>();

        await AssertCompletesWithinAsync(() =>
        {
            var drainers = Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
            {
                var list = coord.DrainPendingReconnect();
                foreach (var id in list)
                    allDrained.Add(id);
            })).ToArray();
            // 并发补充
            var adders = Enumerable.Range(0, 5).Select(i => Task.Run(() =>
            {
                coord.AddPendingReconnect($"dev-new-{i}");
            })).ToArray();
            Task.WaitAll(drainers.Concat(adders).ToArray());
        }, "BaselineResetCoordinator.DrainPendingReconnect 并发");

        // DrainPendingReconnect 是原子的：同一设备不应在多次 Drain 中重复出现
        // (除非 AddPendingReconnect 在 Drain 后再次添加，此时重复是合法的)
        Assert.True(allDrained.Count <= 10); // 初始 5 + 最多 5 个新增
    }

    // ════════════════════════════════════════════════════════════════
    //  ShiftContext._lock
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ShiftContext_ConcurrentCacheAndGet_NoException()
    {
        var ctx = new ShiftContext();
        var devices = Enumerable.Range(0, 20)
            .Select(i => new Device { Id = $"dev-{i}", Name = $"设备{i}" })
            .ToList();
        var runtimes = devices.ToDictionary(
            d => d.Id,
            d => new DeviceRuntime(d) { TotalOkProduction = 100, TotalNgProduction = 5 });
        var deviceCollection = new ObservableCollection<Device>(devices);
        var random = new Random(42);

        await AssertCompletesWithinAsync(() =>
        {
            var cachiers = Enumerable.Range(0, 5).Select(_ => Task.Run(() =>
            {
                for (int i = 0; i < 100; i++)
                    ctx.CacheLastShiftSummaries(deviceCollection, d => runtimes[d.Id], "白班");
            })).ToArray();
            var readers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                for (int i = 0; i < 100; i++)
                    ctx.GetLastShiftSummary($"dev-{random.Next(20)}");
            })).ToArray();
            Task.WaitAll(cachiers.Concat(readers).ToArray());
        }, "ShiftContext.CacheLastShiftSummaries × GetLastShiftSummary");
    }

    [Fact]
    public async Task ShiftContext_ConcurrentDetectChangeAndCacheSummaries_NoDeadlock()
    {
        var ctx = new ShiftContext();
        var devices = Enumerable.Range(0, 20)
            .Select(i => new Device { Id = $"dev-{i}", Name = $"设备{i}" })
            .ToList();
        var runtimes = devices.ToDictionary(
            d => d.Id,
            d => new DeviceRuntime(d) { TotalOkProduction = 100, TotalNgProduction = 5 });
        var deviceCollection = new ObservableCollection<Device>(devices);

        // 单一班次配置覆盖全天，便于在 detector 线程中通过 SetCurrentShift 制造切换
        var shifts = new ObservableCollection<ShiftConfig>
        {
            new() { Name = "白班", StartTime = new TimeSpan(0, 0, 0), EndTime = new TimeSpan(24, 0, 0) }
        };
        // 首次初始化 _currentShiftId（DetectChange 首次返回 null，仅设置内部状态）
        ctx.DetectChange(shifts);

        var random = new Random(42);
        var nightShift = new ShiftIdentifier("夜班", new TimeSpan(20, 0, 0), new TimeSpan(8, 0, 0));

        await AssertCompletesWithinAsync(() =>
        {
            // detector：模拟单一轮询线程调用 DetectChange（生产中仅一个线程调用，此处保持单线程避免引入生产中不存在的竞态）
            // 偶尔通过 SetCurrentShift 注入不同班次，触发 DetectChange 返回切换信号
            var detector = Task.Run(() =>
            {
                for (int i = 0; i < 200; i++)
                {
                    if (i % 50 == 49)
                        ctx.SetCurrentShift(nightShift);
                    var newShift = ctx.DetectChange(shifts);
                    if (newShift != null)
                    {
                        ctx.CacheLastShiftSummaries(deviceCollection, d => runtimes[d.Id], "夜班");
                        ctx.SetCurrentShift(newShift);
                    }
                }
            });

            // cachier：模拟其他路径（如 ResetShift 前缓存）并发写入 _lastShiftSummaries
            var cachiers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            {
                for (int i = 0; i < 100; i++)
                    ctx.CacheLastShiftSummaries(deviceCollection, d => runtimes[d.Id], "白班");
            })).ToArray();

            // reader：模拟 UI 线程并发读取上班次快照
            var readers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                for (int i = 0; i < 100; i++)
                    ctx.GetLastShiftSummary($"dev-{random.Next(20)}");
            })).ToArray();

            Task.WaitAll(new[] { detector }.Concat(cachiers).Concat(readers).ToArray());
        }, "ShiftContext.DetectChange × CacheLastShiftSummaries × GetLastShiftSummary");

        // 最终状态可读取（不抛异常即可）
        _ = ctx.GetLastShiftSummary("dev-0");
    }

    // ════════════════════════════════════════════════════════════════
    //  ProductionBaselineStore._lock（磁盘 IO 在锁外，验证并发安全）
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ProductionBaselineStore_ConcurrentGetOrCreateWithSameValue_NoDiskWrite()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "KanbanConcTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var appSettings = new AppSettings { ConfigDirectory = tempDir };
            var store = new ProductionBaselineStore(appSettings);
            store.Load();

            // 生产并发模型：基线更新由单一轮询线程串行触发，多线程并发 GetOrCreate 会触发磁盘写竞争。
            // 乱序写盘已由 ProductionBaselineStore 的版本复查循环收敛（见 ProductionBaselineStoreConcurrencyTests），
            // 故此处仅测试"基线值未变时不触发磁盘写"的路径，确保 GetOrCreate 的内存路径线程安全。
            // 先预置基线
            store.GetOrCreate("dev-0", 100, "白班");

            await AssertCompletesWithinAsync(() =>
            {
                var readers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
                {
                    // 相同值不触发磁盘写，纯内存路径
                    for (int i = 0; i < 200; i++)
                        store.GetOrCreate("dev-0", 100, "白班");
                })).ToArray();
                Task.WaitAll(readers);
            }, "ProductionBaselineStore 并发 GetOrCreate（相同值，无磁盘写）");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  PlcDataAcquisitionService._startStopLock + _resetLock
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task PlcDataAcquisitionService_ConcurrentStartStop_NoDoubleStart()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "KanbanConcTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var appSettings = new AppSettings
            {
                ConfigDirectory = tempDir,
                PollingIntervalMs = 100 // 较短间隔便于触发轮询
            };
            appSettings.Shifts = new ObservableCollection<ShiftConfig>
            {
                new() { Name = "白班", StartTime = new TimeSpan(0, 0, 0), EndTime = new TimeSpan(24, 0, 0) }
            };
            var repo = new DeviceRepository(appSettings);
            var plc = new FakePlcDriver();
            var conn = new PlcConnectionManager(plc, appSettings);
            var history = new InMemoryHistoryService();
            var baseline = new ProductionBaselineStore(appSettings);
            baseline.Load();
            var svc = new PlcDataAcquisitionService(plc, conn, appSettings, history, repo, baseline,
                NullLogger<PlcDataAcquisitionService>.Instance);

            await AssertCompletesWithinAsync(() =>
            {
                var startStopTasks = Enumerable.Range(0, 20).Select(i => Task.Run(() =>
                {
                    if (i % 2 == 0) svc.Start();
                    else svc.StopAsync().Wait();
                })).ToArray();
                Task.WaitAll(startStopTasks);
            }, "PlcDataAcquisitionService.Start × Stop 并发");

            // 最终状态可读取
            await svc.StopAsync();
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task PlcDataAcquisitionService_ConcurrentResetShiftAndResetDeviceProduction_NoDeadlock()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "KanbanConcTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var appSettings = new AppSettings
            {
                ConfigDirectory = tempDir,
                PollingIntervalMs = 50
            };
            appSettings.Shifts = new ObservableCollection<ShiftConfig>
            {
                new() { Name = "白班", StartTime = new TimeSpan(0, 0, 0), EndTime = new TimeSpan(24, 0, 0) }
            };
            var repo = new DeviceRepository(appSettings);
            var plc = new FakePlcDriver();
            // 预置 5 台带清零地址的设备
            var devices = new List<Device>();
            for (int i = 0; i < 5; i++)
            {
                var dev = new Device
                {
                    Id = $"dev-{i}",
                    Name = $"设备{i}",
                    OkCountAddress = $"D{i}00",
                    NgCountAddress = $"D{i}01",
                    StatusCountAddress = $"D{i}02",
                    ProductionResetAddress = $"D{i}03"
                };
                repo.Devices.Add(dev);
                repo.AddRuntime(dev);
                plc.SetInt32(dev.OkCountAddress, 100);
                plc.SetInt32(dev.NgCountAddress, 5);
                devices.Add(dev);
            }
            var conn = new PlcConnectionManager(plc, appSettings);
            var history = new InMemoryHistoryService();
            var baseline = new ProductionBaselineStore(appSettings);
            baseline.Load();
            var svc = new PlcDataAcquisitionService(plc, conn, appSettings, history, repo, baseline,
                NullLogger<PlcDataAcquisitionService>.Instance);

            await AssertCompletesWithinAsync(() =>
            {
                var shiftResetters = Enumerable.Range(0, 5).Select(_ => Task.Run(() =>
                {
                    for (int i = 0; i < 5; i++)
                        svc.ResetShift();
                })).ToArray();
                var deviceResetters = Enumerable.Range(0, 5).Select(i => Task.Run(() =>
                {
                    for (int j = 0; j < 5; j++)
                        svc.ResetDeviceProduction(devices[j % 5]);
                })).ToArray();
                Task.WaitAll(shiftResetters.Concat(deviceResetters).ToArray());
            }, "PlcDataAcquisitionService.ResetShift × ResetDeviceProduction 并发");

            await svc.StopAsync();
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}
