using Kanban.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Kanban.Core.Data;

public sealed class DefectHistoryDbContext : KanbanDbContextBase
{
    public DbSet<DefectSnapshotRecord> DefectSnapshots => Set<DefectSnapshotRecord>();

    public DefectHistoryDbContext(Kanban.Core.Services.AppSettings appSettings)
        : base(appSettings, "defect_history.db")
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DefectSnapshotRecord>(entity =>
        {
            entity.HasKey(record => record.Id);
            entity.HasIndex(record => new { record.DeviceId, record.Timestamp });
            entity.HasIndex(record => new { record.DeviceId, record.DefectId, record.Timestamp });
            entity.Property(record => record.DeviceId).IsRequired().HasMaxLength(64);
            entity.Property(record => record.DeviceName).HasMaxLength(128);
            entity.Property(record => record.DefectId).IsRequired().HasMaxLength(64);
            entity.Property(record => record.DefectName).HasMaxLength(128);
            entity.Property(record => record.ShiftName).HasMaxLength(64);
            entity.Property(record => record.Severity).HasConversion<int>();
            entity.Property(record => record.Category).HasConversion<int>();
        });
    }
}
