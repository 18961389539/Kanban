using Kanban.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Kanban.Core.Data;

/// <summary>
/// EF CLI 设计时工厂。四个数据库共用 AppSettings，但每个上下文仍生成独立迁移目录。
/// </summary>
public abstract class KanbanDbContextFactory<TContext> : IDesignTimeDbContextFactory<TContext>
    where TContext : KanbanDbContextBase
{
    public TContext CreateDbContext(string[] args)
    {
        var settings = new AppSettings();
        return CreateContext(settings);
    }

    protected abstract TContext CreateContext(AppSettings settings);
}

public sealed class ProductionLogDbContextFactory : KanbanDbContextFactory<ProductionLogDbContext>
{
    protected override ProductionLogDbContext CreateContext(AppSettings settings) => new(settings);
}

public sealed class AlarmEventDbContextFactory : KanbanDbContextFactory<AlarmEventDbContext>
{
    protected override AlarmEventDbContext CreateContext(AppSettings settings) => new(settings);
}

public sealed class StatusTransitionDbContextFactory : KanbanDbContextFactory<StatusTransitionDbContext>
{
    protected override StatusTransitionDbContext CreateContext(AppSettings settings) => new(settings);
}

public sealed class WorkOrderDbContextFactory : KanbanDbContextFactory<WorkOrderDbContext>
{
    protected override WorkOrderDbContext CreateContext(AppSettings settings) => new(settings);
}
