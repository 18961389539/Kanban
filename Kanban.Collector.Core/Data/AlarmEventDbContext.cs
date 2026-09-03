using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using Microsoft.EntityFrameworkCore;
using Kanban.Collector.Core.Entities;

namespace Kanban.Collector.Core.Data;

/// <summary>
/// 报警事件数据库上下文（独立数据库文件 alarm_events.db）。
/// </summary>
public class AlarmEventDbContext : KanbanDbContextBase
{
    public DbSet<AlarmEventRecord> AlarmEvents => Set<AlarmEventRecord>();

    /// <summary>报警活跃状态（当前是否触发）快照表，供重启恢复与前端活跃报警墙直查。</summary>
    public DbSet<ActiveAlarmStateRecord> ActiveAlarmStates => Set<ActiveAlarmStateRecord>();

    public AlarmEventDbContext(AppSettings appSettings) : base(appSettings, "alarm_events.db") { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AlarmEventRecord>(e =>
        {
            e.HasKey(a => a.Id);
            e.HasIndex(a => new { a.DeviceId, a.AlarmId });
            e.HasIndex(a => a.AlarmId);
            e.HasIndex(a => a.EventTime);
            // 显式按 int 列存储枚举：避免不同 EF Core/provider 版本对枚举默认映射行为不一致，
            // 也防止将来在 AlarmEventType 中间插入新值时旧数据库记录被误读
            // （当前 Triggered=1, Recovered=2, ShiftChange=3 已持久化到生产数据库，禁止调整数值）
            e.Property(a => a.EventType).HasConversion<int>();

            // 字符串字段显式约束：EF Core 默认将 string 映射为 nullable TEXT，
            // 此处声明 IsRequired + MaxLength，让 EF Core Migrations 生成的 schema 有 NOT NULL 约束。
            e.Property(a => a.DeviceId).IsRequired().HasMaxLength(64);
            e.Property(a => a.AlarmId).IsRequired().HasMaxLength(128);
            e.Property(a => a.DeviceName).HasMaxLength(128);
            e.Property(a => a.AlarmName).HasMaxLength(256);
            e.Property(a => a.PlcAddress).HasMaxLength(128);
            e.Property(a => a.ShiftName).HasMaxLength(64);
        });

        modelBuilder.Entity<ActiveAlarmStateRecord>(e =>
        {
            e.HasKey(a => a.Id);
            // (DeviceId, AlarmId) 唯一：同一报警同一时刻只有一行状态。
            e.HasIndex(a => new { a.DeviceId, a.AlarmId }).IsUnique();
            e.HasIndex(a => a.IsActive);

            e.Property(a => a.DeviceId).IsRequired().HasMaxLength(64);
            e.Property(a => a.AlarmId).IsRequired().HasMaxLength(128);
            e.Property(a => a.DeviceName).HasMaxLength(128);
            e.Property(a => a.AlarmName).HasMaxLength(256);
            e.Property(a => a.PlcAddress).HasMaxLength(128);
            e.Property(a => a.ShiftName).HasMaxLength(64);
        });
    }
}
