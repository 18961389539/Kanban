using MainAPP.Models;
using MainAPP.Services;
using Microsoft.EntityFrameworkCore;
using MainAPP.Entities;

namespace MainAPP.Data;

/// <summary>
/// 状态转换数据库上下文（独立数据库文件 status_transitions.db）。
/// </summary>
public class StatusTransitionDbContext : KanbanDbContextBase
{
    public DbSet<StatusTransitionRecord> StatusTransitions => Set<StatusTransitionRecord>();

    public StatusTransitionDbContext(AppSettings appSettings) : base(appSettings, "status_transitions.db") { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StatusTransitionRecord>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasIndex(s => new { s.DeviceId, s.EventTime });
            e.HasIndex(s => s.EventTime);

            // 字符串字段显式约束：让 EnsureCreated 生成的 schema 有 NOT NULL 约束 + 长度限制，
            // 并由 EF Core Migrations 固化为数据库约束。
            e.Property(s => s.DeviceId).IsRequired().HasMaxLength(64);
            e.Property(s => s.DeviceName).HasMaxLength(128);
            e.Property(s => s.ShiftName).HasMaxLength(64);
        });
    }
}
