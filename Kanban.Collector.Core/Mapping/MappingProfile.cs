using AutoMapper;
using Kanban.Core.Entities;

namespace Kanban.Core.Mapping;

/// <summary>
/// AutoMapper 映射配置。
/// WorkOrder 自映射用于 Upsert：从入参实体（可能 detached）批量拷贝到 EF Core 追踪的 existing 实体，
/// 避免手动逐字段赋值漏写新字段（如 CompletedOkCount/CompletedNgCount 曾被漏写）。
/// </summary>
public class MappingProfile : Profile
{
    public MappingProfile()
    {
        // WorkOrder → WorkOrder：映射到现有对象，忽略主键 Id 与创建时间（更新场景不应改）
        CreateMap<WorkOrder, WorkOrder>()
            .ForMember(d => d.Id, opt => opt.Ignore())
            .ForMember(d => d.CreatedAt, opt => opt.Ignore());
    }
}
