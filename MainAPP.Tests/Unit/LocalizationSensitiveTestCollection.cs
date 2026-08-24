using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// Localization.Apply 修改进程级静态资源文化；依赖该文化的测试必须串行运行。
/// </summary>
[CollectionDefinition("LocalizationSensitive", DisableParallelization = true)]
public sealed class LocalizationSensitiveTestCollectionDefinition
{
}
