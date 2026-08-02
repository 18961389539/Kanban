using Kanban.Core.Entities;
using Kanban.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Kanban.Core.Data;

/// <summary>
/// 工单数据库上下文（独立数据库文件 work_orders.db）。
/// 参照 <see cref="AlarmEventDbContext"/> 模式：继承 <see cref="KanbanDbContextBase"/>，
/// 仅声明 DbSet + OnModelCreating 配置索引与字段约束。
/// </summary>
public class WorkOrderDbContext : KanbanDbContextBase
{
    public DbSet<WorkOrder> WorkOrders => Set<WorkOrder>();

    public WorkOrderDbContext(AppSettings appSettings) : base(appSettings, "work_orders.db") { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WorkOrder>(e =>
        {
            e.HasKey(w => w.Id);
            // 按设备 + 状态查询当前 Running 工单（主页/工单管理页高频查询路径）
            e.HasIndex(w => new { w.DeviceId, w.Status });
            // 按状态筛选工单列表
            e.HasIndex(w => w.Status);
            // 按创建时间倒序展示
            e.HasIndex(w => w.CreatedAt);
            // 枚举显式按 int 列存储，避免迁移时枚举值变化导致旧数据被误读
            e.Property(w => w.Status).HasConversion<int>();

            e.Property(w => w.OrderNo).IsRequired().HasMaxLength(64);
            e.Property(w => w.ProductCode).IsRequired().HasMaxLength(64);
            e.Property(w => w.ProductName).IsRequired().HasMaxLength(128);
            e.Property(w => w.DeviceId).IsRequired().HasMaxLength(64);
            e.Property(w => w.DeviceName).HasMaxLength(128);
            e.Property(w => w.Remark).HasMaxLength(512);
        });
    }
}
