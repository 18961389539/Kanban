using System.IO;
using Kanban.Collector.Services;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// 历史保留策略测试（P1：清理方法此前无人调用，等于没做）：
/// - 环境变量解析（默认 365 / 合法值 / 非法值回退 / 0 禁用）
/// - HistoryService.CleanupOldHistory 实际批量删除过期数据并保留新数据
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class HistoryRetentionTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppSettings _settings;
    private readonly DatabaseProvider _db;
    private readonly HistoryService _history;
    private readonly string _originalEnv;

    public HistoryRetentionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"RetentionTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _settings = new AppSettings { ConfigDirectory = _tempDir };
        _db = new DatabaseProvider(_settings);
        _db.EnsureCreatedAll();
        _db.EnsureWalModeEnabled();
        _history = new HistoryService(_db, _settings, NullLogger<HistoryService>.Instance);
        _originalEnv = Environment.GetEnvironmentVariable(HistoryRetentionService.EnvRetentionDays);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(HistoryRetentionService.EnvRetentionDays, _originalEnv);
        _history.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ──────────── 环境变量解析 ────────────

    [Fact]
    public void RetentionDays_Default_Is365()
    {
        Environment.SetEnvironmentVariable(HistoryRetentionService.EnvRetentionDays, null);
        Assert.Equal(365, HistoryRetentionService.ResolveRetentionDays());
    }

    [Fact]
    public void RetentionDays_ParsesValidEnv()
    {
        Environment.SetEnvironmentVariable(HistoryRetentionService.EnvRetentionDays, "90");
        Assert.Equal(90, HistoryRetentionService.ResolveRetentionDays());
    }

    [Fact]
    public void RetentionDays_InvalidEnv_FallsBackToDefault()
    {
        Environment.SetEnvironmentVariable(HistoryRetentionService.EnvRetentionDays, "abc");
        Assert.Equal(365, HistoryRetentionService.ResolveRetentionDays());
    }

    [Fact]
    public void RetentionDays_Zero_DisablesCleanup()
    {
        Environment.SetEnvironmentVariable(HistoryRetentionService.EnvRetentionDays, "0");
        Assert.Equal(0, HistoryRetentionService.ResolveRetentionDays());
    }

    // ──────────── 实际清理 ────────────

    [Fact]
    public void CleanupOldHistory_RemovesExpired_KeepsRecent()
    {
        // 生产日志：1 条 400 天前（过期）+ 1 条今天（保留）
        using (var context = _db.CreateProductionLogContext())
        {
            context.ProductionLogs.AddRange(
                new ProductionLog
                {
                    DeviceId = "dev-1", DeviceName = "D1", ShiftName = "白班",
                    Timestamp = DateTime.Now.AddDays(-400), EventId = Guid.NewGuid(),
                },
                new ProductionLog
                {
                    DeviceId = "dev-1", DeviceName = "D1", ShiftName = "白班",
                    Timestamp = DateTime.Now, EventId = Guid.NewGuid(),
                });
            context.SaveChanges();
        }

        var deleted = _history.CleanupOldHistory(retentionDays: 365);
        Assert.Equal(1, deleted);

        using var verify = _db.CreateProductionLogContext();
        Assert.Equal(1, verify.ProductionLogs.Count());
    }

    [Fact]
    public void CleanupOldHistory_ZeroDays_IsNoOp()
    {
        using (var context = _db.CreateProductionLogContext())
        {
            context.ProductionLogs.Add(new ProductionLog
            {
                DeviceId = "dev-1", DeviceName = "D1", ShiftName = "白班",
                Timestamp = DateTime.Now.AddDays(-500), EventId = Guid.NewGuid(),
            });
            context.SaveChanges();
        }

        var deleted = _history.CleanupOldHistory(retentionDays: 0); // 禁用
        Assert.Equal(0, deleted);

        using var verify = _db.CreateProductionLogContext();
        Assert.Equal(1, verify.ProductionLogs.Count());
    }
}
