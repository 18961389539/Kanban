using AutoMapper;
using Kanban.Core.Mapping;

namespace MainAPP.Tests;

/// <summary>
/// 测试用共享 IMapper 单例：使用与生产环境相同的 MappingProfile 配置。
/// 避免每个测试方法都重复构建 MapperConfiguration。
///
/// 使用 Lazy 延迟初始化：若 MappingProfile 配置错误，异常不会以 TypeInitializationException
/// 形式抛出（静态构造器异常难以定位），而是直接抛出带清晰提示的 InvalidOperationException，
/// 指向 MappingProfile 配置问题。
/// </summary>
public static class TestMapper
{
    private static readonly Lazy<IMapper> _instance = new(() =>
    {
        try
        {
            var config = new MapperConfiguration(cfg => cfg.AddProfile<MappingProfile>());
            config.AssertConfigurationIsValid();
            return config.CreateMapper();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "TestMapper 初始化失败：MappingProfile 配置错误，请检查 AutoMapper 映射定义。", ex);
        }
    });

    public static IMapper Instance => _instance.Value;
}
