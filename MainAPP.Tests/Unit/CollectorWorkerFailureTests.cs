using System.IO;
using System.Reflection;
using Kanban.Collector.Services;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// CollectorWorker 失败语义测试（P1：消除"进程存活但采集已停止"）：
/// 初始化失败必须**重新抛出**（宿主才能按 Windows Service 恢复策略拉起），
/// 且退出前在健康状态中记录失败原因（/health/ready 即使进程未退出也判 Unhealthy）。
/// 用"依赖缺失导致解析失败"模拟初始化异常，无需真实 PLC 驱动。
/// ExecuteAsync 为 protected，经反射调用（宿主 StartAsync 的内部路径）。
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class CollectorWorkerFailureTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppSettings _settings;

    public CollectorWorkerFailureTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"WorkerTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _settings = new AppSettings { ConfigDirectory = _tempDir };
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task ExecuteAsync_InitFailure_Rethrows_AndMarksHealthFailed()
    {
        // 只注册 AppSettings + HealthState：InitializeAsync 解析 DeviceRepository 时
        // GetRequiredService 抛 InvalidOperationException（模拟启动期依赖/配置损坏）
        var services = new ServiceCollection();
        services.AddSingleton(_settings);
        services.AddSingleton<CollectorHealthState>();
        using var provider = services.BuildServiceProvider();

        var worker = new CollectorWorker(
            provider,
            new SnapshotPublisher(
                new DeviceRepository(_settings),
                new SnapshotAggregator(),
                NullLogger<SnapshotPublisher>.Instance),
            provider.GetRequiredService<CollectorHealthState>(),
            NullLogger<CollectorWorker>.Instance);

        var healthState = provider.GetRequiredService<CollectorHealthState>();

        // 反射调用 protected ExecuteAsync（等同宿主 StartAsync 的触发路径）
        var executeMethod = typeof(CollectorWorker).GetMethod(
            "ExecuteAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ExecuteAsync 方法不存在");

        var task = (Task?)executeMethod.Invoke(worker, [CancellationToken.None]);
        Assert.NotNull(task);

        // 关键断言：初始化失败必须向上抛出（宿主据此重启进程），而不是吞掉后静默运行
        var ex = await Record.ExceptionAsync(() => task);
        Assert.NotNull(ex);
        Assert.IsType<InvalidOperationException>(ex);

        // 退出前记录失败原因：即使宿主异常被吞（如个别宿主配置），readiness 仍判 Unhealthy
        Assert.Equal(CollectorHealthState.InitState.Failed, healthState.State);
        Assert.False(string.IsNullOrEmpty(healthState.InitErrorMessage));
        Assert.NotNull(healthState.FailedAtUtc);
    }
}
