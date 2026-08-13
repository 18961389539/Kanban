using System.IO;
using Kanban.Collector.Services;
using Kanban.Core.Data;
using Kanban.Core.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// Collector 健康状态与 readiness 探针测试（P1：消除"进程存活但采集已停止"的假健康）：
/// - 初始化失败 → 状态 Failed（Worker 重新抛出前记录），readiness Unhealthy
/// - 初始化未完成/采集循环停止/连续失败/库不可写 → /health/ready Unhealthy
/// - 正常状态（无设备配置 + 库可写）→ Healthy
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class CollectorReadinessTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppSettings _settings;
    private readonly DatabaseProvider _db;
    private readonly CollectorHealthState _healthState = new();

    public CollectorReadinessTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ReadinessTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _settings = new AppSettings { ConfigDirectory = _tempDir };
        _db = new DatabaseProvider(_settings);
        _db.EnsureCreatedAll();
        _db.EnsureWalModeEnabled();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private CollectorReadinessCheck CreateCheck(IPlcDataAcquisitionService? acquisition = null, int configuredDevices = 0)
    {
        if (acquisition is null)
        {
            // 仅对默认 mock 设置健康行为；外部传入的 mock 由测试自身配置（避免 .Returns 覆盖）
            var acq = Substitute.For<IPlcDataAcquisitionService>();
            acq.IsRunning.Returns(true);
            acq.GetDiagnosticsSnapshot().Returns(new AcquisitionDiagnosticsSnapshot
            {
                CompletedCycles = 10,
                LastCycleSucceeded = true,
            });
            acquisition = acq;
        }
        return new CollectorReadinessCheck(
            _healthState, acquisition, _db, NullLogger<CollectorReadinessCheck>.Instance,
            configuredDeviceCount: () => configuredDevices);
    }

    [Fact]
    public async Task InitFailed_ReturnsUnhealthyWithMessage()
    {
        _healthState.MarkFailed("settings.json 损坏");
        var check = CreateCheck();
        var result = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("settings.json 损坏", result.Description);
    }

    [Fact]
    public async Task InitPending_ReturnsUnhealthy()
    {
        var check = CreateCheck();
        var result = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task ReadyHealthy_WhenAcquisitionRunningAndDbWritable()
    {
        _healthState.MarkReady();
        var check = CreateCheck();
        var result = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task AcquisitionStopped_ReturnsUnhealthy()
    {
        _healthState.MarkReady();
        var acq = Substitute.For<IPlcDataAcquisitionService>();
        acq.IsRunning.Returns(false); // 采集循环已停止（进程存活但采集挂掉）
        var check = CreateCheck(acq);
        var result = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("采集循环已停止", result.Description);
    }

    [Fact]
    public async Task ConsecutiveFailures_ReturnsDegraded()
    {
        _healthState.MarkReady();
        var acq = Substitute.For<IPlcDataAcquisitionService>();
        acq.IsRunning.Returns(true);
        acq.GetDiagnosticsSnapshot().Returns(new AcquisitionDiagnosticsSnapshot
        {
            CompletedCycles = 30,
            LastSuccessfulAt = DateTime.Now.AddSeconds(-30), // 最近成功过（不触发 Unhealthy 分支）
            ConsecutiveFailureCycles = 12, // 连续失败超阈值 → Degraded
        });

        var check = CreateCheck(acq, configuredDevices: 1);
        var result = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public async Task StaleLastSuccessfulAt_ReturnsUnhealthy()
    {
        _healthState.MarkReady();
        var acq = Substitute.For<IPlcDataAcquisitionService>();
        acq.IsRunning.Returns(true);
        acq.GetDiagnosticsSnapshot().Returns(new AcquisitionDiagnosticsSnapshot
        {
            CompletedCycles = 30,
            LastSuccessfulAt = DateTime.Now.AddMinutes(-10), // 超过 3 分钟判活窗口
        });

        var check = CreateCheck(acq, configuredDevices: 1);
        var result = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("最近成功采集", result.Description);
    }

    [Fact]
    public async Task NoConfiguredDevices_SkipsAcquisitionLiveness()
    {
        // 未配置设备（部署初期/空配置）不应因"无成功采集"误判不健康
        _healthState.MarkReady();
        var acq = Substitute.For<IPlcDataAcquisitionService>();
        acq.IsRunning.Returns(true);
        acq.GetDiagnosticsSnapshot().Returns(new AcquisitionDiagnosticsSnapshot
        {
            CompletedCycles = 5,
            ConsecutiveFailureCycles = 20,
        });

        var check = CreateCheck(acq, configuredDevices: 0);
        var result = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public void ProbeWriteAccess_ReturnsNull_WhenDbHealthy()
    {
        Assert.Null(_db.ProbeWriteAccess());
    }

    [Fact]
    public async Task DbUnwritable_ReturnsUnhealthy()
    {
        _healthState.MarkReady();
        // 占用生产库写锁：模拟另一连接持写锁时 BEGIN IMMEDIATE 无法获取（busy_timeout 内失败）
        var path = _settings.GetFilePath("production_logs.db");
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Cache=Shared"))
        {
            conn.Open();
            using var begin = conn.CreateCommand();
            begin.CommandText = "BEGIN IMMEDIATE;";
            begin.ExecuteNonQuery();
            try
            {
                var probe = _db.ProbeWriteAccess();
                Assert.NotNull(probe); // 写锁被占 → 探测失败
                var check = CreateCheck();
                var result = await check.CheckHealthAsync(new HealthCheckContext());
                Assert.Equal(HealthStatus.Unhealthy, result.Status);
            }
            finally
            {
                using var rollback = conn.CreateCommand();
                rollback.CommandText = "ROLLBACK;";
                rollback.ExecuteNonQuery();
            }
        }
    }
}
