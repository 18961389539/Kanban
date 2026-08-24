using System.Collections.Concurrent;
using System.IO;
using Kanban.Client;
using Kanban.Contracts.Dtos;
using Kanban.Contracts.Enums;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AlarmEventType = Kanban.Contracts.Enums.AlarmEventType;
using DeviceStatus = Kanban.Contracts.Enums.DeviceStatus;
using AlarmLevel = Kanban.Contracts.Enums.AlarmLevel;
using WorkOrderStatus = Kanban.Contracts.Enums.WorkOrderStatus;
using CoreDataSourceValueType = Kanban.Collector.Core.Models.DataSourceValueType;

namespace MainAPP.Tests.Integration;

/// <summary>
/// RemoteRuntimeSink 契约测试（0% 盲区补位，审查修复 2026-08-13）：
/// 本地 Kestrel 起最小 SignalR Hub（复用 KanbanDataClientIntegrationTests 模式），
/// 验证"Collector 推送 → 桌面端本地运行时"的完整链路：初始快照拉取、快照订阅/合并、
/// tombstone、报警/状态/元数据事件灌入、断线重订阅、以及**每流独立纪元的游标归零**回归。
///
/// 测试设施说明：
/// - Sink 构造函数绑定 Dispatcher.CurrentDispatcher 且 500ms 批量闸靠 DispatcherTimer 泵驱动，
///   故在专用 STA 线程上构造 + Dispatcher.Run() 提供消息泵（不加载 WPF Application/资源字典）。
/// - 服务端 SignalR 配置 MaximumParallelInvocationsPerClient=16（对齐生产 Collector），
///   使同一事件连接上的报警/状态/Meta 三个长驻订阅可并行处理（默认 1 会永久排队）。
/// - Sink 的 Start 是 fire-and-forget 无就绪信号，断言一律"轮询等待 + 超时"。
/// </summary>
[Trait("Category", "Integration")]
[Trait("Speed", "Slow")]
[Trait("Requires", "Network")]
public class RemoteRuntimeSinkTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private string _hubAddress = "";
    private IHubContext<SinkTestHub> _hubContext = null!;

    private string _tempDir = "";
    private AppSettings _appSettings = null!;
    private DatabaseProvider _dbProvider = null!;
    private DeviceRepository _deviceRepo = null!;
    private WorkOrderRepository _workOrderRepo = null!;

    private KanbanDataClient _client = null!;
    private RemoteRuntimeSink? _sink;
    private System.Windows.Threading.Dispatcher? _dispatcher;
    private Thread? _staThread;
    private readonly ManualResetEventSlim _sinkReady = new(false);

    // ──────────── 测试专用最小 Hub（方法名与 IKanbanHubClient/客户端 nameof 对齐） ────────────

    public sealed class SinkTestHub : Hub
    {
        /// <summary>报警流订阅参数记录（断线重订阅断言游标续传/归零）。</summary>
        public static readonly ConcurrentQueue<long> AlarmSubscribeSeqs = new();
        public static readonly ConcurrentQueue<long> StatusSubscribeSeqs = new();
        public static volatile int SnapshotSubscribeCount;
        public static volatile int MetaSubscribeCount;
        public static IReadOnlyList<DeviceSnapshotDto> InitialSnapshots = [];

        public static void ResetStaticState()
        {
            AlarmSubscribeSeqs.Clear();
            StatusSubscribeSeqs.Clear();
            SnapshotSubscribeCount = 0;
            MetaSubscribeCount = 0;
            InitialSnapshots = [];
        }

        public Task<IReadOnlyList<DeviceSnapshotDto>> GetCurrentSnapshotsAsync()
            => Task.FromResult(InitialSnapshots);

        public async Task SubscribeSnapshotsAsync()
        {
            Interlocked.Increment(ref SnapshotSubscribeCount);
            while (true)
                await Task.Delay(1000, Context.ConnectionAborted);
        }

        public async Task SubscribeAlarmEventsAsync(long afterSeq)
        {
            AlarmSubscribeSeqs.Enqueue(afterSeq);
            while (true)
                await Task.Delay(1000, Context.ConnectionAborted);
        }

        public async Task SubscribeStatusEventsAsync(long afterSeq)
        {
            StatusSubscribeSeqs.Enqueue(afterSeq);
            while (true)
                await Task.Delay(1000, Context.ConnectionAborted);
        }

        public async Task SubscribeMetaAsync()
        {
            Interlocked.Increment(ref MetaSubscribeCount);
            while (true)
                await Task.Delay(1000, Context.ConnectionAborted);
        }
    }

    // ──────────── 生命周期 ────────────

    public async ValueTask InitializeAsync()
    {
        SinkTestHub.ResetStaticState();

        // 预分配空闲端口：WebApplication 停后不可重启（Host.StartAsync 抛 OCE），
        // "断线重连"用例需在**同一端口**重建新应用，客户端才能重连到原地址。
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        _port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        _app = CreateApp();
        await _app.StartAsync();
        _hubAddress = $"http://127.0.0.1:{_port}";
        _hubContext = _app.Services.GetRequiredService<IHubContext<SinkTestHub>>();

        _tempDir = Path.Combine(Path.GetTempPath(), "KanbanSinkTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _appSettings = new AppSettings { ConfigDirectory = _tempDir };
        _dbProvider = new DatabaseProvider(_appSettings);
        _dbProvider.EnsureCreatedAll();
        _deviceRepo = new DeviceRepository(_appSettings);
        _workOrderRepo = new WorkOrderRepository(_dbProvider, TestMapper.Instance);
    }

    private int _port;

    private WebApplication CreateApp()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{_port}");
        // 与生产 Collector 协议对齐：AddMessagePackProtocol 配置 NativeDateTimeResolver +
        // StandardResolver 组合（Native 仅覆盖 DateTime 保留 Kind；其余类型回退 Standard），
        // 避免测试环境与生产的 DateTime 语义/可序列化范围不一致。
        builder.Services.AddSignalR(o => o.MaximumParallelInvocationsPerClient = 16)
            .AddMessagePackProtocol(options =>
            {
                options.SerializerOptions = MessagePack.MessagePackSerializerOptions.Standard
                    .WithResolver(MessagePack.Resolvers.CompositeResolver.Create(
                        MessagePack.Resolvers.NativeDateTimeResolver.Instance,
                        MessagePack.Resolvers.ContractlessStandardResolver.Instance));
            });
        var app = builder.Build();
        app.MapHub<SinkTestHub>("/hubs/sink");
        return app;
    }

    public async ValueTask DisposeAsync()
    {
        try { if (_sink != null) await _sink.DisposeAsync(); } catch { /* best-effort */ }
        try { if (_client != null) await _client.DisposeAsync(); } catch { /* best-effort */ }
        try { _dispatcher?.InvokeShutdown(); } catch { /* best-effort */ }
        if (_staThread is { IsAlive: true })
            _staThread.Join(5000);
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { /* best-effort */ }
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    // ──────────── 测试设施 ────────────

    /// <summary>在专用 STA 线程上构造 Sink 并启动消息泵（构造绑定 CurrentDispatcher，批量闸依赖泵）。</summary>
    private async Task StartSinkAsync(CancellationToken cancellationToken)
    {
        _client = new KanbanDataClient($"{_hubAddress}/hubs/sink",
            NullLogger<KanbanDataClient>.Instance, useMessagePack: false);
        await _client.ConnectAsync(cancellationToken);

        _staThread = new Thread(() =>
        {
            _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            _sink = new RemoteRuntimeSink(_client, _deviceRepo, _workOrderRepo,
                NullLoggerFactory.Instance, NullLogger<RemoteRuntimeSink>.Instance);
            _sinkReady.Set();
            System.Windows.Threading.Dispatcher.Run();
        });
        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.IsBackground = true;
        _staThread.Start();

        Assert.True(_sinkReady.Wait(TimeSpan.FromSeconds(10)), "Sink 构造超时");
        _sink!.Start();
    }

    private static void WaitUntil(Func<bool> condition, string what, int timeoutMs = 60000)
    {
        // 上限取 60s：断线重连用例依赖客户端 WithAutomaticReconnect（1s/5s/15s/30s 退避序列），
        // 全量并行跑时 Kestrel 重建与重试可能叠加，30s 内未必收敛。
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"等待超时（{timeoutMs}ms）：{what}");
            Thread.Sleep(50);
        }
    }

    /// <summary>
    /// 模拟网络断连：WebApplication 停后不可重启（Host.StartAsync 抛 OCE），
    /// 且 HubCallerContext.Abort 是服务端主动终止（AllowReconnect=false，客户端不自动重连），
    /// 故在同一端口重建新应用——客户端 WithAutomaticReconnect（1s/5s/…）自行重连并触发重订阅。
    /// </summary>
    private async Task RestartServerAsync(CancellationToken cancellationToken)
    {
        await _app.StopAsync(cancellationToken);
        await _app.DisposeAsync();
        _app = CreateApp();
        await _app.StartAsync(cancellationToken);
        _hubContext = _app.Services.GetRequiredService<IHubContext<SinkTestHub>>();
    }

    private static Device CreateDeviceWithAlarm()
    {
        var device = new Device { Id = "dev-1", Name = "注塑机-1", TargetCycle = 60 };
        // 注意初始化器顺序：PlcAddress 赋值会触发确定性 Id 生成（DeviceId_PlcAddress），
        // 必须放在 Id 之前，否则显式 Id 会被覆盖导致事件按 AlarmId 匹配不到。
        device.Alarms.Add(new Alarm { DeviceId = "dev-1", Name = "高温报警", PlcAddress = "M100", Id = "alm-1" });
        return device;
    }

    private static DeviceSnapshotDto Snapshot(int totalOk = 120, int statusWord = 1, bool removed = false)
        => new()
        {
            DeviceId = "dev-1",
            DeviceName = "注塑机-1",
            Status = removed ? DeviceStatus.Unknown : DeviceStatus.Running,
            StatusWord = statusWord,
            OkProduction = totalOk,
            NgProduction = 3,
            TotalOkProduction = totalOk,
            TotalNgProduction = 3,
            RunTime = 3600,
            QualityRate = 0.975,
            PerformanceRate = 0.8,
            AvailabilityRate = 0.9,
            Oee = 0.702,
            TargetCycle = 60,
            ActiveAlarms = [],
            Removed = removed,
        };

    private static AlarmEventDto AlarmEvent(long epoch, long seq, AlarmEventType type, DateTime time)
        => new()
        {
            Seq = seq,
            ServerEpoch = epoch,
            DeviceId = "dev-1",
            DeviceName = "注塑机-1",
            AlarmId = "alm-1",
            AlarmName = "高温报警",
            PlcAddress = "M100",
            EventType = type,
            Level = AlarmLevel.High,
            EventTime = time,
            ShiftName = "白班",
        };

    private static StatusEventDto StatusEvent(long epoch, long seq, DeviceStatus current)
        => new()
        {
            Seq = seq,
            ServerEpoch = epoch,
            DeviceId = "dev-1",
            DeviceName = "注塑机-1",
            PreviousState = DeviceStatus.Running,
            CurrentState = current,
            EventTime = DateTime.UtcNow,
            ShiftName = "白班",
        };

    // ──────────── 用例 ────────────

    [Fact]
    public async Task Start_InitialSnapshots_AppliedToRepository()
    {
        // 初始拉取走 RefreshAsync（不经 Dispatcher 泵），Start 后快照应灌入 RuntimeMap
        _deviceRepo.ReplaceAll([CreateDeviceWithAlarm()]);
        SinkTestHub.InitialSnapshots = [Snapshot(totalOk: 120)];

        await StartSinkAsync(TestContext.Current.CancellationToken);

        WaitUntil(() => _deviceRepo.RuntimeMap.TryGetValue("dev-1", out var rt) && rt.TotalOkProduction == 120,
            "初始快照灌入 RuntimeMap");
        var runtime = _deviceRepo.RuntimeMap["dev-1"];
        Assert.Equal(120, runtime.TotalOkProduction);
        Assert.Equal(3, runtime.TotalNgProduction);
        Assert.Equal(60, runtime.TargetCycle);
    }

    [Fact]
    public async Task Start_InitialSnapshots_AppliesDataSourceValues()
    {
        var device = CreateDeviceWithAlarm();
        var source = new DataSource { Id = "src-1", Name = "环境", Type = "温湿度" };
        source.Values.Add(new DataSourceValue
        {
            Id = "value-1",
            Name = "温度",
            DataType = CoreDataSourceValueType.Float32,
            PlcAddress = "D300",
            Unit = "°C",
        });
        device.Sources.Add(source);
        _deviceRepo.ReplaceAll([device]);

        var sampledAt = DateTime.UtcNow.AddSeconds(-2);
        SinkTestHub.InitialSnapshots = [Snapshot(totalOk: 120) with
        {
            SourceValues =
            [
                new DataSourceValueSnapshotDto
                {
                    SourceId = "src-1",
                    SourceName = "环境",
                    SourceType = "温湿度",
                    ValueId = "value-1",
                    ValueName = "温度",
                    PlcAddress = "D300",
                    Unit = "°C",
                    DataType = Kanban.Contracts.Enums.DataSourceValueType.Float32,
                    Float32Value = 23.5f,
                    DisplayText = "23.5",
                    IsValid = true,
                    LastUpdatedAt = sampledAt,
                },
            ],
        }];

        await StartSinkAsync(TestContext.Current.CancellationToken);

        WaitUntil(() => _deviceRepo.GetDeviceById("dev-1")!.Sources[0].Values[0].IsValid,
            "初始快照灌入数据源值");
        var value = _deviceRepo.GetDeviceById("dev-1")!.Sources[0].Values[0];
        Assert.Equal(23.5f, value.CurrentFloatValue);
        Assert.Equal(sampledAt, value.LastUpdatedAt);
        Assert.True(value.IsValid);
    }

    [Fact]
    public async Task SnapshotFailure_UsesSnapshotTimeForReadAttempt_AndPreservesLastValidTime()
    {
        var device = CreateDeviceWithAlarm();
        var source = new DataSource { Id = "src-1", Name = "环境", Type = "温湿度" };
        var value = new DataSourceValue
        {
            Id = "value-1",
            Name = "温度",
            DataType = CoreDataSourceValueType.Float32,
            PlcAddress = "D300",
        };
        var sampledAt = DateTime.UtcNow.AddSeconds(-2);
        value.SetRuntimeValue(
            new DataSourceRuntimeValue(CoreDataSourceValueType.Float32, Float32Value: 23.5f),
            sampledAt);
        source.Values.Add(value);
        device.Sources.Add(source);
        _deviceRepo.ReplaceAll([device]);

        var attemptAt = sampledAt.AddSeconds(1);
        SinkTestHub.InitialSnapshots = [Snapshot(totalOk: 120) with
        {
            Timestamp = attemptAt,
            SourceValues =
            [
                new DataSourceValueSnapshotDto
                {
                    SourceId = "src-1",
                    SourceName = "环境",
                    SourceType = "温湿度",
                    ValueId = "value-1",
                    ValueName = "温度",
                    PlcAddress = "D300",
                    Unit = "",
                    DataType = Kanban.Contracts.Enums.DataSourceValueType.Float32,
                    Float32Value = 23.5f,
                    IsValid = false,
                    LastUpdatedAt = sampledAt,
                },
            ],
        }];

        await StartSinkAsync(TestContext.Current.CancellationToken);

        WaitUntil(() => !_deviceRepo.GetDeviceById("dev-1")!.Sources[0].Values[0].IsValid,
            "失败快照灌入数据源值");
        var updatedValue = _deviceRepo.GetDeviceById("dev-1")!.Sources[0].Values[0];
        Assert.Equal(sampledAt, updatedValue.LastUpdatedAt);
        Assert.Equal(attemptAt, updatedValue.LastReadAttemptAt);
    }

    [Fact]
    public async Task SnapshotSubscription_AppliesUpdates_AndTombstoneRemovesRuntime()
    {
        _deviceRepo.ReplaceAll([CreateDeviceWithAlarm()]);
        await StartSinkAsync(TestContext.Current.CancellationToken);
        WaitUntil(() => SinkTestHub.SnapshotSubscribeCount >= 1, "快照订阅建立");

        // 订阅推送：500ms 批量闸经 Dispatcher 泵应用
        await _hubContext.Clients.All.SendAsync("OnSnapshot", Snapshot(totalOk: 150), TestContext.Current.CancellationToken);
        WaitUntil(() => _deviceRepo.RuntimeMap.TryGetValue("dev-1", out var rt) && rt.TotalOkProduction == 150,
            "订阅快照灌入 RuntimeMap");

        // tombstone：删除广播后从本地移除
        await _hubContext.Clients.All.SendAsync("OnSnapshot", Snapshot(removed: true), TestContext.Current.CancellationToken);
        WaitUntil(() => !_deviceRepo.RuntimeMap.ContainsKey("dev-1"), "tombstone 移除设备运行时");
        Assert.Null(_deviceRepo.GetDeviceById("dev-1"));
        Assert.DoesNotContain(_deviceRepo.Devices, device => device.Id == "dev-1");
    }

    [Fact]
    public async Task AlarmStatusMetaEvents_AppliedToLocalState()
    {
        _deviceRepo.ReplaceAll([CreateDeviceWithAlarm()]);
        await StartSinkAsync(TestContext.Current.CancellationToken);
        WaitUntil(() => !SinkTestHub.AlarmSubscribeSeqs.IsEmpty && !SinkTestHub.StatusSubscribeSeqs.IsEmpty
            && SinkTestHub.MetaSubscribeCount >= 1, "事件连接三订阅建立");

        // 先推一帧快照建运行时（状态事件只更新已有 RuntimeMap 项）
        await _hubContext.Clients.All.SendAsync("OnSnapshot", Snapshot(), TestContext.Current.CancellationToken);
        WaitUntil(() => _deviceRepo.RuntimeMap.ContainsKey("dev-1"), "运行时建立");

        // 报警触发 → StartTime 置位
        var t1 = DateTime.UtcNow.AddMinutes(-5);
        await _hubContext.Clients.All.SendAsync("OnAlarmEvent", AlarmEvent(epoch: 1, seq: 1, AlarmEventType.Triggered, t1), TestContext.Current.CancellationToken);
        WaitUntil(() => _deviceRepo.GetDeviceById("dev-1")!.Alarms[0].StartTime == t1, "报警触发事件应用");

        // 报警恢复 → EndTime 置位
        var t2 = DateTime.UtcNow;
        await _hubContext.Clients.All.SendAsync("OnAlarmEvent", AlarmEvent(epoch: 1, seq: 2, AlarmEventType.Recovered, t2), TestContext.Current.CancellationToken);
        WaitUntil(() => _deviceRepo.GetDeviceById("dev-1")!.Alarms[0].EndTime == t2, "报警恢复事件应用");

        // 状态事件 → StatusWord 更新
        await _hubContext.Clients.All.SendAsync("OnStatusEvent", StatusEvent(epoch: 1, seq: 3, DeviceStatus.Alarm), TestContext.Current.CancellationToken);
        WaitUntil(() => _deviceRepo.RuntimeMap["dev-1"].StatusWord == (int)DeviceStatus.Alarm, "状态事件应用");

        // 元数据 → 工单增量插入
        var meta = new MetaStateDto
        {
            Devices =
            [
                new DeviceWorkOrderDto
                {
                    DeviceId = "dev-1",
                    WorkOrder = new WorkOrderDto
                    {
                        Id = 77,
                        OrderNo = "WO-SINK",
                        ProductCode = "P-1",
                        ProductName = "产品S",
                        DeviceId = "dev-1",
                        DeviceName = "注塑机-1",
                        Status = WorkOrderStatus.Running,
                    },
                },
            ],
        };
        await _hubContext.Clients.All.SendAsync("OnMeta", meta, TestContext.Current.CancellationToken);
        WaitUntil(() => _workOrderRepo.WorkOrders.Any(w => w.OrderNo == "WO-SINK"), "元数据工单插入");
        Assert.Equal("产品S", _workOrderRepo.WorkOrders.First(w => w.OrderNo == "WO-SINK").ProductName);
    }

    [Fact]
    public async Task EpochChange_PerStreamCursorReset_ResubscribesWithZero()
    {
        // 回归（审查修复 2026-08-13）：报警/状态流共享纪元存在"报警先到更新纪元、
        // 状态后到走同纪元分支不归零"的竞态——每流独立纪元后，状态流在自身纪元变化时必须归零游标。
        _deviceRepo.ReplaceAll([CreateDeviceWithAlarm()]);
        await StartSinkAsync(TestContext.Current.CancellationToken);
        WaitUntil(() => !SinkTestHub.AlarmSubscribeSeqs.IsEmpty && !SinkTestHub.StatusSubscribeSeqs.IsEmpty,
            "事件连接报警/状态订阅建立");
        await _hubContext.Clients.All.SendAsync("OnSnapshot", Snapshot(), TestContext.Current.CancellationToken);
        WaitUntil(() => _deviceRepo.RuntimeMap.ContainsKey("dev-1"), "运行时建立");

        // 旧纪元：状态流消费 seq=42（游标推进）
        await _hubContext.Clients.All.SendAsync("OnStatusEvent", StatusEvent(epoch: 100, seq: 42, DeviceStatus.Running), TestContext.Current.CancellationToken);
        WaitUntil(() => _deviceRepo.RuntimeMap["dev-1"].StatusWord == (int)DeviceStatus.Running, "旧纪元状态事件应用");

        // 报警先到（新纪元 400）→ 报警流游标归零；随后状态事件也带新纪元 → 状态流游标必须同样归零
        await _hubContext.Clients.All.SendAsync("OnAlarmEvent", AlarmEvent(epoch: 400, seq: 3, AlarmEventType.Triggered, DateTime.UtcNow), TestContext.Current.CancellationToken);
        await _hubContext.Clients.All.SendAsync("OnStatusEvent", StatusEvent(epoch: 400, seq: 5, DeviceStatus.Alarm), TestContext.Current.CancellationToken);

        // 重启服务端（模拟网络断连）→ 客户端自动重连 → 重订阅：两条流的游标都应为 0（旧实现状态流会是 5）
        await RestartServerAsync(TestContext.Current.CancellationToken);

        WaitUntil(() => SinkTestHub.AlarmSubscribeSeqs.Count >= 2 && SinkTestHub.StatusSubscribeSeqs.Count >= 2,
            "事件连接重连后重订阅");

        Assert.Equal(0, SinkTestHub.AlarmSubscribeSeqs.Last());
        Assert.Equal(0, SinkTestHub.StatusSubscribeSeqs.Last());
    }

    [Fact]
    public async Task Reconnect_ResubscribesSnapshotStream()
    {
        _deviceRepo.ReplaceAll([CreateDeviceWithAlarm()]);
        await StartSinkAsync(TestContext.Current.CancellationToken);
        WaitUntil(() => SinkTestHub.SnapshotSubscribeCount >= 1, "快照订阅建立");

        // 重启服务端（模拟网络断连）→ 自动重连 → OnReconnectedAsync 恢复快照订阅
        await RestartServerAsync(TestContext.Current.CancellationToken);

        WaitUntil(() => SinkTestHub.SnapshotSubscribeCount >= 2, "重连后快照订阅恢复");
    }
}
