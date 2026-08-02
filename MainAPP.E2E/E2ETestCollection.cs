using Xunit;

namespace MainAPP.E2E;

/// <summary>
/// E2E 测试集合：强制串行执行，所有 E2E 测试共享同一个 TestHost（含 Application 单例）。
/// 与 MainAPP.Tests 的 WpfUi collection 独立，避免两个测试项目并发创建 Application 实例冲突。
/// </summary>
[CollectionDefinition("E2E")]
public class E2ETestCollection : ICollectionFixture<TestHost>
{
    // 该类仅供 xUnit 标识集合用，无成员
}
