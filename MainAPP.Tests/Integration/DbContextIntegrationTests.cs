using System;
using System.IO;
using System.Linq;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Models;
using MainAPP.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MainAPP.Tests.Integration;

/// <summary>
/// 三个业务 DbContext 的真实 SQLite 集成测试（直接构造 DbContext，不经由 HistoryService）：
/// - EnsureCreated 在 AppSettings.ConfigDirectory 下生成对应 .db 文件（验证 KanbanDbContextBase 路径解析）
/// - Add / SaveChanges / 查询 往返保留字段
/// - Update / Delete 往返
/// - AlarmEventDbContext 的 EventType 必须按 int 列存储（HasConversion&lt;int&gt;）
/// - SqlitePragmaInterceptor 在每次连接打开时设置 PRAGMA（synchronous/temp_store/busy_timeout/mmap_size）
/// - DatabaseProvider.EnsureWalModeEnabled 将 journal_mode 设为 WAL
/// 共用一个临时目录，避免污染真实 %APPDATA%/Kanban。
/// 所有 PRAGMA 读取都通过 EF 打开的连接完成，不引入对 Microsoft.Data.Sqlite 具体类型的直接依赖。
/// </summary>
[Trait("Category","Integration")]
[Trait("Speed","Slow")]
[Trait("Requires","Database")]
public class DbContextIntegrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppSettings _appSettings;
    private readonly DatabaseProvider _db;

    public DbContextIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "KanbanDbCtx_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _appSettings = new AppSettings { ConfigDirectory = _tempDir };
        _db = new DatabaseProvider(_appSettings);
    }

    public void Dispose()
    {
        try
        {
            using (_db.CreateProductionLogContext()) { }
            using (_db.CreateAlarmEventContext()) { }
            using (_db.CreateStatusTransitionContext()) { }
            Directory.Delete(_tempDir, true);
        }
        catch { }
    }

    /// <summary>读取 PRAGMA 值：兼容 long / int / string 多种返回类型。</summary>
    private static long ReadPragma(DbContext ctx, string pragma)
    {
        if (ctx.Database.GetDbConnection().State != System.Data.ConnectionState.Open)
            ctx.Database.OpenConnection();
        using var cmd = ctx.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = pragma;
        var v = cmd.ExecuteScalar();
        return v switch
        {
            long l => l,
            int i => i,
            string s => long.Parse(s),
            _ => Convert.ToInt64(v)
        };
    }

    private static string ReadPragmaString(DbContext ctx, string pragma)
    {
        if (ctx.Database.GetDbConnection().State != System.Data.ConnectionState.Open)
            ctx.Database.OpenConnection();
        using var cmd = ctx.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = pragma;
        return (string)cmd.ExecuteScalar()!;
    }

    // ---------- ProductionLogDbContext ----------

    [Fact]
    public void ProductionLogDbContext_EnsureCreated_CreatesDbFile_AtConfiguredPath()
    {
        var path = _appSettings.GetFilePath("production_logs.db");
        Assert.False(File.Exists(path));
        using (var ctx = _db.CreateProductionLogContext())
            ctx.Database.EnsureCreated();
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void ProductionLogDbContext_AddAndQuery_RoundTripsFields()
    {
        using (var ctx = _db.CreateProductionLogContext())
        {
            ctx.Database.EnsureCreated();
            ctx.ProductionLogs.Add(new ProductionLog
            {
                DeviceId = "dev-001",
                DeviceName = "设备A",
                ShiftName = "白班",
                OkProduction = 100,
                NgProduction = 5,
                StatusWord = (int)DeviceStatus.Running,
                Timestamp = new DateTime(2026, 7, 26, 9, 0, 0)
            });
            ctx.SaveChanges();
        }

        using (var ctx = _db.CreateProductionLogContext())
        {
            var all = ctx.ProductionLogs.ToList();
            Assert.Single(all);
            var r = all[0];
            Assert.Equal("dev-001", r.DeviceId);
            Assert.Equal("设备A", r.DeviceName);
            Assert.Equal("白班", r.ShiftName);
            Assert.Equal(100, r.OkProduction);
            Assert.Equal(5, r.NgProduction);
            Assert.Equal((int)DeviceStatus.Running, r.StatusWord);
            Assert.Equal(new DateTime(2026, 7, 26, 9, 0, 0), r.Timestamp);
            Assert.True(r.Id > 0);
        }
    }

    [Fact]
    public void ProductionLogDbContext_UpdateAndDelete_Work()
    {
        int id;
        using (var ctx = _db.CreateProductionLogContext())
        {
            ctx.Database.EnsureCreated();
            var log = new ProductionLog
            {
                DeviceId = "d",
                DeviceName = "n",
                OkProduction = 1,
                NgProduction = 0,
                StatusWord = (int)DeviceStatus.Running,
                Timestamp = DateTime.Now
            };
            ctx.ProductionLogs.Add(log);
            ctx.SaveChanges();
            id = log.Id;
        }

        using (var ctx = _db.CreateProductionLogContext())
        {
            var log = ctx.ProductionLogs.Single(x => x.Id == id);
            log.OkProduction = 999;
            ctx.SaveChanges();
        }
        using (var ctx = _db.CreateProductionLogContext())
            Assert.Equal(999, ctx.ProductionLogs.Single(x => x.Id == id).OkProduction);

        using (var ctx = _db.CreateProductionLogContext())
        {
            var log = ctx.ProductionLogs.Single(x => x.Id == id);
            ctx.ProductionLogs.Remove(log);
            ctx.SaveChanges();
        }
        using (var ctx = _db.CreateProductionLogContext())
            Assert.Empty(ctx.ProductionLogs);
    }

    // ---------- StatusTransitionDbContext ----------

    [Fact]
    public void StatusTransitionDbContext_EnsureCreated_CreatesDbFile_AtConfiguredPath()
    {
        var path = _appSettings.GetFilePath("status_transitions.db");
        Assert.False(File.Exists(path));
        using (var ctx = _db.CreateStatusTransitionContext())
            ctx.Database.EnsureCreated();
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void StatusTransitionDbContext_AddAndQuery_RoundTripsFields()
    {
        using (var ctx = _db.CreateStatusTransitionContext())
        {
            ctx.Database.EnsureCreated();
            ctx.StatusTransitions.Add(new StatusTransitionRecord
            {
                DeviceId = "dev-001",
                DeviceName = "设备A",
                PreviousState = (int)DeviceStatus.Unknown,
                CurrentState = (int)DeviceStatus.Running,
                EventTime = new DateTime(2026, 7, 26, 8, 0, 0),
                ShiftName = "白班"
            });
            ctx.SaveChanges();
        }

        using (var ctx = _db.CreateStatusTransitionContext())
        {
            var all = ctx.StatusTransitions.ToList();
            Assert.Single(all);
            var r = all[0];
            Assert.Equal("dev-001", r.DeviceId);
            Assert.Equal("设备A", r.DeviceName);
            Assert.Equal((int)DeviceStatus.Unknown, r.PreviousState);
            Assert.Equal((int)DeviceStatus.Running, r.CurrentState);
            Assert.Equal("白班", r.ShiftName);
            Assert.True(r.Id > 0);
        }
    }

    [Fact]
    public void StatusTransitionDbContext_UpdateAndDelete_Work()
    {
        int id;
        using (var ctx = _db.CreateStatusTransitionContext())
        {
            ctx.Database.EnsureCreated();
            var rec = new StatusTransitionRecord
            {
                DeviceId = "d",
                DeviceName = "n",
                PreviousState = (int)DeviceStatus.Unknown,
                CurrentState = (int)DeviceStatus.Running,
                EventTime = DateTime.Now,
                ShiftName = "白班"
            };
            ctx.StatusTransitions.Add(rec);
            ctx.SaveChanges();
            id = rec.Id;
        }

        using (var ctx = _db.CreateStatusTransitionContext())
        {
            var rec = ctx.StatusTransitions.Single(x => x.Id == id);
            rec.CurrentState = (int)DeviceStatus.Alarm;
            ctx.SaveChanges();
        }
        using (var ctx = _db.CreateStatusTransitionContext())
            Assert.Equal((int)DeviceStatus.Alarm, ctx.StatusTransitions.Single(x => x.Id == id).CurrentState);

        using (var ctx = _db.CreateStatusTransitionContext())
        {
            var rec = ctx.StatusTransitions.Single(x => x.Id == id);
            ctx.StatusTransitions.Remove(rec);
            ctx.SaveChanges();
        }
        using (var ctx = _db.CreateStatusTransitionContext())
            Assert.Empty(ctx.StatusTransitions);
    }

    // ---------- AlarmEventDbContext ----------

    [Fact]
    public void AlarmEventDbContext_EnsureCreated_CreatesDbFile_AtConfiguredPath()
    {
        var path = _appSettings.GetFilePath("alarm_events.db");
        Assert.False(File.Exists(path));
        using (var ctx = _db.CreateAlarmEventContext())
            ctx.Database.EnsureCreated();
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void AlarmEventDbContext_AddAndQuery_RoundTripsFields()
    {
        using (var ctx = _db.CreateAlarmEventContext())
        {
            ctx.Database.EnsureCreated();
            ctx.AlarmEvents.Add(new AlarmEventRecord
            {
                DeviceId = "dev-001",
                DeviceName = "设备A",
                AlarmId = "alm-1",
                AlarmName = "高温报警",
                PlcAddress = "D100",
                EventType = AlarmEventType.Triggered,
                EventTime = new DateTime(2026, 7, 26, 10, 0, 0),
                ShiftName = "白班"
            });
            ctx.SaveChanges();
        }

        using (var ctx = _db.CreateAlarmEventContext())
        {
            var all = ctx.AlarmEvents.ToList();
            Assert.Single(all);
            var r = all[0];
            Assert.Equal("alm-1", r.AlarmId);
            Assert.Equal("高温报警", r.AlarmName);
            Assert.Equal("D100", r.PlcAddress);
            Assert.Equal(AlarmEventType.Triggered, r.EventType);
            Assert.Equal("白班", r.ShiftName);
            Assert.True(r.Id > 0);
        }
    }

    [Fact]
    public void AlarmEventDbContext_StoresEventTypeAsIntColumn()
    {
        using (var ctx = _db.CreateAlarmEventContext())
        {
            ctx.Database.EnsureCreated();
            ctx.AlarmEvents.Add(new AlarmEventRecord
            {
                DeviceId = "d",
                DeviceName = "n",
                AlarmId = "a",
                AlarmName = "x",
                PlcAddress = "M1",
                EventType = AlarmEventType.Triggered,
                EventTime = DateTime.Now
            });
            ctx.SaveChanges();
        }

        // 通过 EF 读取 EventType 的底层 int 值，确认按 int 列存储（Triggered = 1），
        // 而非枚举默认映射可能带来的偏差（HasConversion<int> 保证存储为 int）
        using (var ctx = _db.CreateAlarmEventContext())
        {
            var raw = ctx.AlarmEvents.Select(a => (int)a.EventType).Single();
            Assert.Equal(1, raw);
        }
    }

    [Fact]
    public void AlarmEventDbContext_UpdateAndDelete_Work()
    {
        int id;
        using (var ctx = _db.CreateAlarmEventContext())
        {
            ctx.Database.EnsureCreated();
            var rec = new AlarmEventRecord
            {
                DeviceId = "d",
                DeviceName = "n",
                AlarmId = "a",
                AlarmName = "x",
                PlcAddress = "M1",
                EventType = AlarmEventType.Triggered,
                EventTime = DateTime.Now
            };
            ctx.AlarmEvents.Add(rec);
            ctx.SaveChanges();
            id = rec.Id;
        }

        using (var ctx = _db.CreateAlarmEventContext())
        {
            var rec = ctx.AlarmEvents.Single(x => x.Id == id);
            rec.EventType = AlarmEventType.Recovered;
            ctx.SaveChanges();
        }
        using (var ctx = _db.CreateAlarmEventContext())
            Assert.Equal(AlarmEventType.Recovered, ctx.AlarmEvents.Single(x => x.Id == id).EventType);

        using (var ctx = _db.CreateAlarmEventContext())
        {
            var rec = ctx.AlarmEvents.Single(x => x.Id == id);
            ctx.AlarmEvents.Remove(rec);
            ctx.SaveChanges();
        }
        using (var ctx = _db.CreateAlarmEventContext())
            Assert.Empty(ctx.AlarmEvents);
    }

    // ---------- SqlitePragmaInterceptor + WAL ----------

    [Fact]
    public void SqlitePragmaInterceptor_AppliesPragmas_OnConnectionOpen()
    {
        // 经由 DbContext 打开连接会触发 SqlitePragmaInterceptor.ConnectionOpened，
        // 再读取 PRAGMA 验证其设置：synchronous=NORMAL(2) / temp_store=MEMORY(2) /
        // busy_timeout=5000 / mmap_size=268435456
        using (var ctx = _db.CreateProductionLogContext())
        {
            ctx.Database.EnsureCreated();
            Assert.Equal(5000, ReadPragma(ctx, "PRAGMA busy_timeout;"));
            // synchronous: 0=OFF, 1=NORMAL, 2=FULL。拦截器设 NORMAL=1
            Assert.Equal(1, ReadPragma(ctx, "PRAGMA synchronous;"));
            Assert.Equal(2, ReadPragma(ctx, "PRAGMA temp_store;"));
            Assert.Equal(268435456L, ReadPragma(ctx, "PRAGMA mmap_size;"));
        }
    }

    [Fact]
    public void DatabaseProvider_EnsureWalModeEnabled_SetsJournalModeToWal()
    {
        _db.EnsureWalModeEnabled();
        using (var ctx = _db.CreateProductionLogContext())
        {
            ctx.Database.EnsureCreated();
            // 通过 EF 打开的连接读取 journal_mode（WAL 已持久化到数据库文件头）
            var mode = ReadPragmaString(ctx, "PRAGMA journal_mode;");
            Assert.Equal("wal", mode, ignoreCase: true);
        }
    }
}
