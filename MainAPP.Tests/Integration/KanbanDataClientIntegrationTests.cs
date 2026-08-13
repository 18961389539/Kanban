using Kanban.Client;
using Kanban.Contracts.Dtos;
using Kanban.Contracts.Enums;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace MainAPP.Tests.Integration;

/// <summary>
/// KanbanDataClient 集成测试：本地 Kestrel 起最小 SignalR Hub，验证**真实连接生命周期**——
/// 连接成功事件、幂等重连、服务端停止触发状态通知（ConnectionStateChanged(false)）、
/// Invoke 调用、Dispose 清理。此前 Kanban.Client 库仅 14.75% 覆盖，未连接路径之外全靠端到端冒烟。
/// </summary>
[Trait("Category", "Integration")]
public class KanbanDataClientIntegrationTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private string _hubAddress = "";

    // ──────────── 测试专用最小 Hub（弱类型，方法名 PascalCase 与客户端 nameof 调用一致） ────────────

    public sealed class TestHub : Hub
    {
        public Task<string> GetServerVersionAsync() => Task.FromResult("test-1.0.0");

        public Task<string> GetTitleAsync() => Task.FromResult("测试看板");

        public Task<int> GetLanguageAsync() => Task.FromResult(1); // En

        /// <summary>长驻订阅（模拟真实 KanbanHub.SubscribeSnapshotsAsync：方法不返回直到连接断开）。</summary>
        public async Task SubscribeSnapshotsAsync()
        {
            // 长驻 await：直到客户端断开
            while (true)
            {
                await Task.Delay(1000, Context.ConnectionAborted);
            }
        }

        public Task<IReadOnlyList<DeviceSnapshotDto>> GetCurrentSnapshotsAsync() => Task.FromResult<IReadOnlyList<DeviceSnapshotDto>>(
            [new DeviceSnapshotDto
            {
                DeviceId = "dev-1",
                DeviceName = "注塑机-1",
                Status = DeviceStatus.Running,
                TotalOkProduction = 120,
                TotalNgProduction = 3,
                RunTime = 3600,
                QualityRate = 0.975,
                PerformanceRate = 0.8,
                AvailabilityRate = 0.9,
                Oee = 0.702,
                TargetCycle = 12,
                ActiveAlarms = [],
            }]);

        public Task<IReadOnlyList<DeviceConfigDto>> GetDevicesAsync() =>
            Task.FromResult<IReadOnlyList<DeviceConfigDto>>([]);

        public Task SaveDevicesAsync(IReadOnlyList<DeviceConfigDto> devices)
        {
            if (devices is null) throw new ArgumentNullException(nameof(devices));
            return Task.CompletedTask;
        }

        public Task<ShiftProgressDto> GetShiftProgressAsync() => Task.FromResult(new ShiftProgressDto
        {
            Name = "白班",
            IsInShift = true,
            Ratio = 0.5,
            Pct = "50%",
            ElapsedText = "2h 0m",
            RemainingText = "2h 0m",
        });

        public Task PingAsync() => Task.CompletedTask;
    }

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0"); // 随机端口
        builder.Services.AddSignalR();
        _app = builder.Build();
        _app.MapHub<TestHub>("/hubs/test");
        _app.MapGet("/healthz", () => Results.Ok());
        await _app.StartAsync();
        _hubAddress = _app.Urls.First();
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private KanbanDataClient CreateClient() =>
        new($"{_hubAddress}/hubs/test", NullLogger<KanbanDataClient>.Instance, useMessagePack: false);

    // ──────────── 用例 ────────────

    [Fact]
    public async Task Connect_Success_IsConnected_AndEventFired()
    {
        await using var client = CreateClient();
        var connectedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionStateChanged += (_, v) => connectedTcs.TrySetResult(v);

        await client.ConnectAsync();

        Assert.True(client.IsConnected);
        var fired = await connectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(fired, "连接成功后应触发 ConnectionStateChanged(true)");
    }

    [Fact]
    public async Task Connect_Twice_Idempotent_NoDuplicateConnection()
    {
        await using var client = CreateClient();
        await client.ConnectAsync();
        await client.ConnectAsync(); // 已连接应直接返回（并发保护 + double-check），不重建连接
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task GetServerVersion_ReturnsServerValue()
    {
        await using var client = CreateClient();
        await client.ConnectAsync();
        var version = await client.GetServerVersionAsync();
        Assert.Equal("test-1.0.0", version);
    }

    [Fact]
    public async Task GetTitle_ReturnsServerValue()
    {
        await using var client = CreateClient();
        await client.ConnectAsync();
        var title = await client.GetTitleAsync();
        Assert.Equal("测试看板", title);
    }

    [Fact]
    public async Task GetLanguage_ReturnsServerValue()
    {
        await using var client = CreateClient();
        await client.ConnectAsync();
        var lang = await client.GetLanguageAsync();
        Assert.Equal(1, lang); // TestHub 返回 En
    }

    [Fact]
    public async Task GetCurrentSnapshots_ReturnsDtoList()
    {
        await using var client = CreateClient();
        await client.ConnectAsync();
        var snapshots = await client.GetCurrentSnapshotsAsync();
        var snap = Assert.Single(snapshots);
        Assert.Equal("dev-1", snap.DeviceId);
        Assert.Equal(120, snap.TotalOkProduction);
        Assert.Equal(0.702, snap.Oee);
    }

    [Fact]
    public async Task InvokeWriteAndQueryMethods_WorkWhenConnected()
    {
        await using var client = CreateClient();
        await client.ConnectAsync();
        var devices = await client.GetDevicesAsync();
        Assert.Empty(devices);
        var shift = await client.GetShiftProgressAsync();
        Assert.True(shift.IsInShift);
        Assert.Equal("白班", shift.Name);
        Assert.Equal("50%", shift.Pct);
    }

    [Fact]
    [Trait("Contract", "SignalROrdering")]
    public async Task ConcurrentInvoke_WhileLongRunningSubscribePending_IsQueued()
    {
        // ⚠️ 架构约束回归测试：SignalR 服务端对同一连接**按序处理 invocation**
        // （DefaultHubDispatcher 顺序 dispatch）——长驻订阅 Invoke 挂起时，同连接后续 Invoke
        // 无限排队（不返回、不报错）。因此客户端设计必须"Invoke 全部在订阅前完成"
        // （WASM DashboardState 已按此修复）。若未来 SignalR 升级为并发 dispatch，
        // 此测试将失败并提示重新评估该约束。
        // Contract=SignalROrdering 标记（审查修复 2026-08-13）：显式声明本用例锁定的
        // 是库行为契约而非缺陷固化，便于 SignalR 升级时定位需重估的用例。
        await using var client = CreateClient();
        await client.ConnectAsync();

        var subscribeTask = client.SubscribeSnapshotsAsync(); // 长驻 Invoke（服务端方法不返回）
        try
        {
            await Task.Delay(500); // 让服务端开始处理长驻订阅
            // 同连接后续 Invoke 应挂起（3s 内不返回）而非立即完成
            var pending = client.GetServerVersionAsync();
            await Assert.ThrowsAsync<TimeoutException>(async () =>
                await pending.WaitAsync(TimeSpan.FromSeconds(3)));
        }
        finally
        {
            await client.DisposeAsync(); // 断开连接以终止长驻订阅
            try { await subscribeTask; } catch { } // 观察：断连后长驻 Invoke fault 属正常
        }
    }

    [Fact]
    public async Task OnSnapshot_RegisteredTwice_HandlerFiresOnce()
    {
        // 审查修复 2026-08-13：回调注册按连接实例去重——部分失败重试路径对同一连接重复注册
        // On* 会导致同一消息双回调（速度趋势队列双写等）；本测试验证重复注册被忽略。
        await using var client = CreateClient();
        await client.ConnectAsync();

        var count = 0;
        void Handler(DeviceSnapshotDto _) => System.Threading.Interlocked.Increment(ref count);
        client.OnSnapshot(Handler);
        client.OnSnapshot(Handler); // 重复注册（模拟重试路径）→ 应被忽略

        var hub = _app.Services.GetRequiredService<Microsoft.AspNetCore.SignalR.IHubContext<TestHub>>();
        var subscribeTask = client.SubscribeSnapshotsAsync();
        try
        {
            await hub.Clients.All.SendAsync("OnSnapshot", new DeviceSnapshotDto
            {
                DeviceId = "dev-1",
                DeviceName = "注塑机-1",
                Status = DeviceStatus.Running,
                TotalOkProduction = 1,
            });

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (System.Threading.Volatile.Read(ref count) < 1 && DateTime.UtcNow < deadline)
                await Task.Delay(50);
            await Task.Delay(200); // 给潜在的第二次回调留出窗口
            Assert.Equal(1, count);
        }
        finally
        {
            await client.DisposeAsync();
            try { await subscribeTask; } catch { }
        }
    }

    [Fact]
    public async Task ServerStop_RaisesConnectionStateChangedFalse()
    {
        await using var client = CreateClient();
        await client.ConnectAsync();
        Assert.True(client.IsConnected);

        var stateChangedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionStateChanged += (_, v) => stateChangedTcs.TrySetResult(v);

        await _app.StopAsync(); // 停服务端 → 客户端进入 Reconnecting（1s 后触发）→ 状态通知 false

        var fired = await stateChangedTcs.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.False(fired, "服务端停止后应触发 ConnectionStateChanged(false)（断线期间徽标不得仍显示实时）");
    }

    [Fact]
    public async Task DisposeAsync_AfterConnect_NoThrow_AndIsConnectedFalse()
    {
        var client = CreateClient();
        await client.ConnectAsync();
        Assert.True(client.IsConnected);

        await client.DisposeAsync();

        Assert.False(client.IsConnected);
        // 二次 Dispose 幂等
        await client.DisposeAsync();
    }

    [Fact]
    public async Task ConnectAsync_UnreachableServer_Throws()
    {
        // 127.0.0.1:1 无监听 → 连接被拒，应快速抛异常（不挂起、不吞掉）；具体类型不定（可能包装为 HttpRequestException）
        await using var client = new KanbanDataClient("http://127.0.0.1:1/hubs/test",
            NullLogger<KanbanDataClient>.Instance, useMessagePack: false);
        await Assert.ThrowsAnyAsync<Exception>(async () => await client.ConnectAsync());
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task EnsureConnectionEstablished_BeforeConnect_Throws()
    {
        await using var client = CreateClient();
        Assert.Throws<InvalidOperationException>(client.EnsureConnectionEstablished);
    }

    [Fact]
    public async Task OnStatusEvent_BeforeConnect_ThrowsInvalidOperation()
    {
        await using var client = CreateClient();
        Assert.Throws<InvalidOperationException>(() => client.OnStatusEvent(_ => { }));
    }

    [Fact]
    public async Task RemainingInvokeMethods_BeforeConnect_FaultWithInvalidOperation()
    {
        // 覆盖未连接路径的其余 Invoke 方法族（EnsureConnected 前置校验统一抛 InvalidOperationException）
        await using var client = CreateClient();
        var calls = new Func<Task>[]
        {
            () => client.SubscribeAlarmEventsAsync(afterSeq: 0),
            () => client.SubscribeStatusEventsAsync(afterSeq: 0),
            () => client.SubscribeMetaAsync(),
            () => client.QueryHistoryAsync(new HistoryQueryRequest { QueryType = HistoryQueryType.ProductionLog }),
            () => client.GetDiagnosticsAsync(),
            () => client.SaveDevicesAsync([]),
            () => client.GetDevicesAsync(),
            () => client.UpsertWorkOrderAsync(new WorkOrderDto
            {
                OrderNo = "WO-TEST",
                ProductCode = "P",
                ProductName = "测试",
                DeviceId = "dev-1",
                DeviceName = "注塑机-1",
                Status = WorkOrderStatus.Pending,
            }),
            () => client.DeleteWorkOrderAsync(1),
            () => client.GetCurrentWorkOrderAsync("dev-1"),
            () => client.GetShiftProgressAsync(),
            () => client.SaveCollectorSettingsAsync(new CollectorSettingsDto()),
            () => client.GetServerVersionAsync(),
            () => client.GetTitleAsync(),
            () => client.GetLanguageAsync(),
        };
        foreach (var call in calls)
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await call());
    }
}
