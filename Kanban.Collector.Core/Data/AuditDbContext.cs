using Kanban.Core.Entities;
using Kanban.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Kanban.Core.Data;

/// <summary>
/// 操作审计数据库（audit_logs.db）：只追加写入，与业务库隔离，避免审计膨胀影响生产库。
/// 与缺陷历史库同模式：EnsureCreated 建表，不引入 EF migration（表结构演进空间小）。
/// </summary>
public sealed class AuditDbContext : KanbanDbContextBase
{
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    public AuditDbContext(AppSettings appSettings)
        : base(appSettings, "audit_logs.db")
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AuditEntry>(entity =>
        {
            entity.HasKey(entry => entry.Id);
            entity.HasIndex(entry => entry.Timestamp);
            entity.HasIndex(entry => entry.Operator);
            entity.HasIndex(entry => entry.Action);
            entity.HasIndex(entry => new { entry.TargetType, entry.TargetId });
            entity.Property(entry => entry.Operator).IsRequired().HasMaxLength(64);
            entity.Property(entry => entry.Action).IsRequired().HasMaxLength(64);
            entity.Property(entry => entry.TargetType).IsRequired().HasMaxLength(64);
            entity.Property(entry => entry.TargetId).HasMaxLength(128);
            entity.Property(entry => entry.Detail).HasMaxLength(512);
            entity.Property(entry => entry.BeforeJson).HasMaxLength(4000);
            entity.Property(entry => entry.AfterJson).HasMaxLength(4000);
        });
    }
}
