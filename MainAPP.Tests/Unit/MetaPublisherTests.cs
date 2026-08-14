using System.IO;
using System.Threading.Channels;
using AutoMapper;
using Kanban.Collector.Services;
using Kanban.Contracts.Dtos;
using Kanban.Core.Data;
using Kanban.Core.Models;
using Kanban.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// MetaPublisher 测试：锁住低频元数据流核心语义（#3 有界化 + DropOldest 背压回归补盲）——
/// - 订阅补发最新元数据包（连接即有数据；无包时空 reader）
/// - 慢/停流订阅者有界 16 + DropOldest：丢最旧保最新、保留段连续无空洞
///   （5s 低频流，容量 16 ≈ 80s 缓冲；无界缓冲可 OOM 拖垮采集进程）
/// - 取消订阅退订后发布不抛、新订阅者不受影响
/// 班次名称作为元数据包的身份标识（ShiftProgressProvider 读 AppSettings.Shifts）。
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class MetaPublisherTests : IDisposable
{
    /// <summary>与 MetaPublisher.SubscribeAsync 的有界容量保持一致（16）。</summary>
    private const int Capacity = 16;

    private readonly string _tempDir;
    private readonly AppSettings _appSettings;
    private readonly MetaPublisher _publisher;
    private readonly ConfigSyncHandler _configSyncHandler;

    public MetaPublisherTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "MetaPubTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _appSettings = new AppSettings { ConfigDirectory = _tempDir };

        var services = new ServiceCollection();
        // 与 ConfigSyncHandlerTests 同模式：DeviceRepository 真实空库（GetWorkOrderSnapshot 直接返回 []），
        // WorkOrderRepository 用 NSubstitute（非虚成员走真实实现：ChangeVersion=0、GetSnapshot=空列表），
        // RecipeApplier 不触达可 null。
        var handler = new ConfigSyncHandler(
            new DeviceRepository(_appSettings),
            Substitute.For<WorkOrderRepository>(
                Substitute.For<DatabaseProvider>(_appSettings),
                Substitute.For<IMapper>()),
            new SnapshotAggregator(),
            _appSettings,
            services.BuildServiceProvider(),
            Substitute.For<ILogger<ConfigSyncHandler>>(),
            Substitute.For<IRecipeStore>(),
            null!); // RecipeApplier：Meta 路径不触达
        _configSyncHandler = handler;

        _publisher = new MetaPublisher(
            handler,
            new ShiftProgressProvider(_appSettings),
            Substitute.For<ILogger<MetaPublisher>>());
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* 忽略清理失败 */ }
    }

    /// <summary>设置当前班次名（元数据包身份标识；全天班次保证任意时刻 GetProgress 命中）。</summary>
    private void SetShiftName(string name)
    {
        lock (_appSettings.ShiftsLock)
        {
            _appSettings.Shifts.Clear();
            _appSettings.Shifts.Add(new ShiftConfig
            {
                Name = name,
                StartTime = TimeSpan.Zero,
                EndTime = TimeSpan.FromDays(1) - TimeSpan.FromMilliseconds(1), // 23:59:59.999 覆盖全天
            });
        }
    }

    private static async Task<ChannelReader<MetaStateDto>> SubscribeAsync(MetaPublisher publisher, CancellationTokenSource? cts = null)
    {
        cts ??= new CancellationTokenSource();
        return await publisher.SubscribeAsync(cts.Token);
    }

    // ──────────── 订阅补发（连接即有数据） ────────────

    [Fact]
    public async Task Subscribe_BackfillsLatestMeta()
    {
        // 未发布时订阅：空 reader
        var cts0 = new CancellationTokenSource();
        var emptyReader = await SubscribeAsync(_publisher, cts0);
        Assert.False(emptyReader.TryRead(out _));

        SetShiftName("S1");
        _publisher.Publish();

        // 发布后订阅：补发最新包
        var reader = await SubscribeAsync(_publisher);
        Assert.True(reader.TryRead(out var meta));
        Assert.Equal("S1", meta.Shift.Name);
        Assert.False(reader.TryRead(out _)); // 只补发一份
    }

    // ──────────── 背压语义：有界 Channel + DropOldest（#3 重构成果回归） ────────────

    /// <summary>
    /// 慢订阅者（注册后暂停消费）：发布量超过 channel 容量（16）时丢最旧保最新，
    /// 恢复消费后收到的是最新 16 条——既不 OOM（有界）也不出现空洞。
    /// </summary>
    [Fact]
    public async Task SlowSubscriber_BoundedChannel_DropsOldest_KeepsLatest()
    {
        var reader = await SubscribeAsync(_publisher);

        // 暂停消费：快速发布直至远超容量（班次名标识先后）
        for (var i = 1; i <= 30; i++)
        {
            SetShiftName($"S{i}");
            _publisher.Publish();
        }

        // 恢复消费：channel 容量 16，丢最旧（S1..S14 被丢弃），剩余 S15..S30 连续
        var received = new List<MetaStateDto>();
        while (reader.TryRead(out var meta))
            received.Add(meta);

        Assert.Equal(Capacity, received.Count);
        for (var i = 0; i < received.Count; i++)
            Assert.Equal($"S{i + 15}", received[i].Shift.Name); // 首条 S15，逐条连续到 S30
        Assert.Equal("S30", received[^1].Shift.Name);            // 最新一条保留
    }

    // ──────────── 退订 ────────────

    [Fact]
    public async Task CancelSubscription_Unsubscribes_NoThrowOnPublish()
    {
        using var cts = new CancellationTokenSource();
        var reader = await SubscribeAsync(_publisher, cts);
        cts.Cancel();

        SetShiftName("S1");
        _publisher.Publish(); // 不抛（退订后的扇出不触达已取消订阅者）

        // 新订阅者不受影响：补发正常
        var reader2 = await SubscribeAsync(_publisher);
        Assert.True(reader2.TryRead(out var meta));
        Assert.Equal("S1", meta.Shift.Name);
    }

    // ──────────── 缺陷 TOP5 / 上班次汇总聚合（Web 首页补齐：Meta 扩展字段） ────────────

    /// <summary>构造带设备数据（缺陷计数）与班次汇总的 MetaPublisher。</summary>
    private MetaPublisher CreatePublisherWithData(
        Action<DeviceRepository> seedDevices,
        (int Ok, int Ng, string Name)? lastShift = null)
    {
        var repo = new DeviceRepository(_appSettings);
        seedDevices(repo);
        var plc = Substitute.For<IPlcDataAcquisitionService>();
        plc.GetLastShiftSummary(Arg.Any<string>())
            .Returns(lastShift is { } s ? s : (0, 0, ""));
        return new MetaPublisher(
            _configSyncHandler,
            new ShiftProgressProvider(_appSettings),
            Substitute.For<ILogger<MetaPublisher>>(),
            repo,
            plc);
    }

    [Fact]
    public void Publish_DefectTop_OnlyPositiveCounts_SortedDesc_Take5()
    {
        var publisher = CreatePublisherWithData(repo =>
        {
            var d = new Device { Id = "d1", Name = "机1" };
            d.Defects.Add(new Defect { Name = "毛边", Count = 3 });
            d.Defects.Add(new Defect { Name = "缺料", Count = 12 });
            d.Defects.Add(new Defect { Name = "色差", Count = 0 });   // 0 值过滤
            d.Defects.Add(new Defect { Name = "缩水", Count = 5 });
            d.Defects.Add(new Defect { Name = "飞边", Count = 8 });
            d.Defects.Add(new Defect { Name = "暗纹", Count = 1 });   // 第 6 个（Take5 截断）
            repo.ReplaceAll([d]);
        });

        publisher.Publish();
        var reader = SubscribeAsync(publisher).GetAwaiter().GetResult();
        Assert.True(reader.TryRead(out var meta));

        var top = meta.DefectTop;
        Assert.Equal(5, top.Count);
        Assert.Equal(["缺料", "飞边", "缩水", "毛边", "暗纹"], top.Select(x => x.Name));
        Assert.All(top, x => Assert.True(x.Count > 0));
        Assert.Equal("d1", top[0].DeviceId);
        Assert.Equal(12, top[0].Count);
    }

    [Fact]
    public void Publish_DefectTop_NoPositiveCounts_Empty()
    {
        var publisher = CreatePublisherWithData(repo =>
        {
            var d = new Device { Id = "d1", Name = "机1" };
            d.Defects.Add(new Defect { Name = "毛边", Count = 0 });
            repo.ReplaceAll([d]);
        });

        publisher.Publish();
        var reader = SubscribeAsync(publisher).GetAwaiter().GetResult();
        Assert.True(reader.TryRead(out var meta));
        Assert.Empty(meta.DefectTop);
    }

    [Fact]
    public void Publish_LastShifts_FromPlcServiceCache()
    {
        var publisher = CreatePublisherWithData(
            repo => repo.ReplaceAll([new Device { Id = "d1", Name = "机1" }]),
            lastShift: (45, 3, "白班"));

        publisher.Publish();
        var reader = SubscribeAsync(publisher).GetAwaiter().GetResult();
        Assert.True(reader.TryRead(out var meta));

        var shift = Assert.Single(meta.LastShifts);
        Assert.Equal("d1", shift.DeviceId);
        Assert.Equal("白班", shift.ShiftName);
        Assert.Equal(45, shift.Ok);
        Assert.Equal(3, shift.Ng);
    }

    [Fact]
    public void Publish_LastShifts_NoCache_Empty()
    {
        var publisher = CreatePublisherWithData(repo =>
            repo.ReplaceAll([new Device { Id = "d1", Name = "机1" }]));

        publisher.Publish();
        var reader = SubscribeAsync(publisher).GetAwaiter().GetResult();
        Assert.True(reader.TryRead(out var meta));
        Assert.Empty(meta.LastShifts);
    }
}
