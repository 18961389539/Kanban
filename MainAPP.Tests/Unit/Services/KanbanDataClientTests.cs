using Kanban.Client;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MainAPP.Tests.Unit.Services;

/// <summary>
/// KanbanDataClient 基础行为测试（不涉及真实网络连接）：
/// 前置校验（EnsureConnected）、数据新鲜度时间戳、失败计数初始状态。
/// 完整连接/订阅行为由 ci/wasm-smoke.js 无头冒烟覆盖（端到端）。
/// </summary>
public class KanbanDataClientTests
{
    private static KanbanDataClient CreateClient() =>
        new("http://127.0.0.1:1/hubs/kanban", NullLogger<KanbanDataClient>.Instance, useMessagePack: false);

    [Fact]
    public void OnSnapshot_BeforeConnect_ThrowsInvalidOperation()
    {
        var client = CreateClient();
        Assert.Throws<InvalidOperationException>(() => client.OnSnapshot(_ => { }));
    }

    [Fact]
    public void OnAlarmEvent_BeforeConnect_ThrowsInvalidOperation()
    {
        var client = CreateClient();
        Assert.Throws<InvalidOperationException>(() => client.OnAlarmEvent(_ => { }));
    }

    [Fact]
    public void OnMeta_BeforeConnect_ThrowsInvalidOperation()
    {
        var client = CreateClient();
        Assert.Throws<InvalidOperationException>(() => client.OnMeta(_ => { }));
    }

    [Fact]
    public async Task SubscribeSnapshotsAsync_BeforeConnect_FaultsWithInvalidOperation()
    {
        var client = CreateClient();
        var task = client.SubscribeSnapshotsAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await task);
    }

    [Fact]
    public async Task GetCurrentSnapshotsAsync_BeforeConnect_FaultsWithInvalidOperation()
    {
        var client = CreateClient();
        var task = client.GetCurrentSnapshotsAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await task);
    }

    [Fact]
    public void MarkDataReceived_UpdatesLastDataReceivedAt()
    {
        var client = CreateClient();
        Assert.Equal(default, client.LastDataReceivedAt);

        client.MarkDataReceived();

        Assert.NotEqual(default, client.LastDataReceivedAt);
    }

    [Fact]
    public void InitialState_IsNotConnected_ZeroFailures()
    {
        var client = CreateClient();
        Assert.False(client.IsConnected);
        Assert.Equal(0, client.ConsecutiveFailures);
    }
}
