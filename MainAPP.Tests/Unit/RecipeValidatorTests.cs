using Kanban.Contracts.Enums;
using Kanban.Core.Models;
using Kanban.Core.Services;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>配方校验（RecipeValidator）单测：覆盖名称/参数项/地址/类型/值/范围/重复地址。</summary>
public class RecipeValidatorTests
{
    private static Recipe ValidRecipe() => new()
    {
        Name = "节拍配方",
        Items =
        {
            new RecipeItem { ParamName = "节拍", PlcAddress = "D108", DataType = PlcDataType.Int32, Value = "50" },
        },
    };

    [Fact]
    public void Validate_ValidRecipe_NoErrors()
    {
        var errors = RecipeValidator.Validate(ValidRecipe());
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_NullRecipe_Error()
    {
        var errors = RecipeValidator.Validate(null!);
        Assert.Single(errors);
    }

    [Fact]
    public void Validate_EmptyName_Error()
    {
        var recipe = ValidRecipe();
        recipe.Name = "  ";
        Assert.Contains(RecipeValidator.Validate(recipe), e => e.Contains("名称"));
    }

    [Fact]
    public void Validate_NoItems_Error()
    {
        var recipe = ValidRecipe();
        recipe.Items.Clear();
        Assert.Contains(RecipeValidator.Validate(recipe), e => e.Contains("至少需要一项"));
    }

    [Fact]
    public void Validate_EmptyParamName_Error()
    {
        var recipe = ValidRecipe();
        recipe.Items[0].ParamName = "";
        Assert.Contains(RecipeValidator.Validate(recipe), e => e.Contains("参数名称"));
    }

    [Fact]
    public void Validate_EmptyAddress_Error()
    {
        var recipe = ValidRecipe();
        recipe.Items[0].PlcAddress = "";
        Assert.Contains(RecipeValidator.Validate(recipe), e => e.Contains("地址"));
    }

    [Fact]
    public void Validate_DuplicateAddress_Error()
    {
        var recipe = ValidRecipe();
        recipe.Items.Add(new RecipeItem { ParamName = "温度", PlcAddress = "D108", DataType = PlcDataType.Int32, Value = "60" });
        Assert.Contains(RecipeValidator.Validate(recipe), e => e.Contains("重复"));
    }

    [Fact]
    public void Validate_UnresolvableAddress_Error()
    {
        var recipe = ValidRecipe();
        recipe.Items[0].PlcAddress = "XYZ123";
        Assert.Contains(RecipeValidator.Validate(recipe), e => e.Contains("无法解析"));
    }

    [Fact]
    public void Validate_AddressTypeMismatch_Error()
    {
        // Bool 需要位区（M）；D 区是字区 → 类型不匹配
        var recipe = ValidRecipe();
        recipe.Items[0].PlcAddress = "D108";
        recipe.Items[0].DataType = PlcDataType.Bool;
        Assert.Contains(RecipeValidator.Validate(recipe), e => e.Contains("类型不匹配"));
    }

    [Fact]
    public void Validate_InvalidValue_Error()
    {
        var recipe = ValidRecipe();
        recipe.Items[0].Value = "abc";
        Assert.Contains(RecipeValidator.Validate(recipe), e => e.Contains("不是有效"));
    }

    [Fact]
    public void Validate_OutOfRange_Error()
    {
        var recipe = ValidRecipe();
        recipe.Items[0].Min = 0;
        recipe.Items[0].Max = 100;
        recipe.Items[0].Value = "150";
        var errors = RecipeValidator.Validate(recipe);
        Assert.Contains(errors, e => e.Contains("上限"));
    }

    [Theory]
    [InlineData(PlcDataType.Int32, "123", true)]
    [InlineData(PlcDataType.Int32, "1.5", false)]
    [InlineData(PlcDataType.Float, "1.5", true)]
    [InlineData(PlcDataType.UInt16, "65535", true)]
    [InlineData(PlcDataType.UInt16, "-1", false)]
    [InlineData(PlcDataType.Bool, "1", true)]
    [InlineData(PlcDataType.Bool, "true", true)]
    [InlineData(PlcDataType.Bool, "2", false)]
    [InlineData(PlcDataType.String, "任意文本", true)]
    public void TryParseValue_ByType(PlcDataType type, string value, bool expected)
    {
        var item = new RecipeItem { DataType = type, Value = value };
        Assert.Equal(expected, RecipeValidator.TryParseValue(item, out _));
    }

    // ─── 配方名唯一性（同机型内；空机型 = 通用配方） ───

    [Fact]
    public void Validate_DuplicateName_SameMachineType_Error()
    {
        var existing = new List<Recipe> { ValidRecipe() };
        var dup = ValidRecipe();
        dup.Id = "different-id";
        var errors = RecipeValidator.Validate(dup, existing);
        Assert.Contains(errors, e => e.Contains("已存在"));
    }

    [Fact]
    public void Validate_SameName_DifferentMachineType_NoError()
    {
        var existing = new List<Recipe> { ValidRecipe() };
        var other = ValidRecipe();
        other.Id = "different-id";
        other.MachineType = "注塑机";
        Assert.DoesNotContain(RecipeValidator.Validate(other, existing), e => e.Contains("已存在"));
    }

    [Fact]
    public void Validate_SameName_CaseInsensitiveMachineType_Error()
    {
        var existing = new List<Recipe> { ValidRecipe() };
        var dup = ValidRecipe();
        dup.Id = "different-id";
        dup.MachineType = "注塑机";
        existing[0].MachineType = " 注塑机 ";
        var errors = RecipeValidator.Validate(dup, existing);
        Assert.Contains(errors, e => e.Contains("已存在"));
    }

    [Fact]
    public void Validate_SameRecipe_ExcludedFromDuplicateCheck()
    {
        var recipe = ValidRecipe();
        Assert.DoesNotContain(RecipeValidator.Validate(recipe, new List<Recipe> { recipe }), e => e.Contains("已存在"));
    }

    [Fact]
    public void Validate_WithoutExisting_SkipsDuplicateCheck()
    {
        var recipe = ValidRecipe();
        Assert.DoesNotContain(RecipeValidator.Validate(recipe), e => e.Contains("已存在"));
    }

    // ─── 范围一致性（Min > Max） ───

    [Fact]
    public void Validate_MinGreaterThanMax_Error()
    {
        var recipe = ValidRecipe();
        recipe.Items[0].Min = 200;
        recipe.Items[0].Max = 100;
        Assert.Contains(RecipeValidator.Validate(recipe), e => e.Contains("下限"));
    }

    [Fact]
    public void Validate_MinLessThanMax_NoError()
    {
        var recipe = ValidRecipe();
        recipe.Items[0].Min = 0;
        recipe.Items[0].Max = 100;
        Assert.DoesNotContain(RecipeValidator.Validate(recipe), e => e.Contains("下限"));
    }

    // ─── 字符串值长度上限（S7 STRING 254） ───

    [Fact]
    public void Validate_StringValueOverMaxLength_Error()
    {
        var recipe = ValidRecipe();
        recipe.Items[0].DataType = PlcDataType.String;
        recipe.Items[0].Value = new string('a', RecipeValidator.MaxStringLength + 1);
        Assert.Contains(RecipeValidator.Validate(recipe), e => e.Contains("最大长度"));
    }

    [Fact]
    public void Validate_StringValueAtMaxLength_NoError()
    {
        var recipe = ValidRecipe();
        recipe.Items[0].DataType = PlcDataType.String;
        recipe.Items[0].Value = new string('a', RecipeValidator.MaxStringLength);
        Assert.DoesNotContain(RecipeValidator.Validate(recipe), e => e.Contains("最大长度"));
    }
}
