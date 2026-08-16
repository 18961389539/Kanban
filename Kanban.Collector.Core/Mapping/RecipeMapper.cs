using Kanban.Contracts.Dtos;
using Kanban.Collector.Core.Models;

namespace Kanban.Collector.Core.Mapping;

/// <summary>
/// 配方实体 ↔ DTO 映射（**全局唯一实现**，ADR-4 单源约定）。
/// MainAPP（配方管理/Remote 同步）与 Collector（ConfigSyncHandler 落盘）共用，禁止在别处手写映射。
/// </summary>
public static class RecipeMapper
{
    public static IReadOnlyList<RecipeDto> ToDtos(IEnumerable<Recipe> recipes)
        => recipes.Select(ToDto).ToList();

    public static RecipeDto ToDto(Recipe r) => new()
    {
        Id = r.Id,
        Name = r.Name,
        MachineType = r.MachineType,
        Remark = r.Remark,
        CreatedAt = r.CreatedAt,
        UpdatedAt = r.UpdatedAt,
        Items = r.Items.Select(i => new RecipeItemDto
        {
            ParamName = i.ParamName,
            PlcAddress = i.PlcAddress,
            DataType = i.DataType,
            Value = i.Value,
            Min = i.Min,
            Max = i.Max,
            Unit = i.Unit,
        }).ToList(),
    };

    public static List<Recipe> ToEntities(IReadOnlyList<RecipeDto> dtos)
        => dtos.Select(ToEntity).ToList();

    public static Recipe ToEntity(RecipeDto dto)
    {
        var recipe = new Recipe
        {
            Id = dto.Id,
            Name = dto.Name,
            MachineType = dto.MachineType,
            Remark = dto.Remark,
            CreatedAt = dto.CreatedAt,
            UpdatedAt = dto.UpdatedAt,
        };
        foreach (var i in dto.Items ?? [])
        {
            recipe.Items.Add(new RecipeItem
            {
                ParamName = i.ParamName,
                PlcAddress = i.PlcAddress,
                DataType = i.DataType,
                Value = i.Value,
                Min = i.Min,
                Max = i.Max,
                Unit = i.Unit,
            });
        }
        return recipe;
    }
}
