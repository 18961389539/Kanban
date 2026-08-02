using Xunit;

// v3：程序集级串行控制已移至 xunit.runner.json（parallelizeAssembly=false + parallelizeTestCollections=false）。
// 各测试集合可通过 [CollectionDefinition(DisableParallelization = true)] 独立控制并行度（v3 才真正生效）。
// 修改环境变量（KANBAN_DATA_DIR）的集合均标记 DisableParallelization，避免进程级状态竞态。

namespace MainAPP.Tests.Integration;

/// <summary>
/// WPF UI 测试集合定义：所有 UI 测试共享同一个 STA 线程和 Application 实例。
/// 必要性：Material.Icons 的 MaterialIconDataProvider 内部 Dictionary 非线程安全，
/// 多个 STA 线程并行创建 MaterialIcon 会并发更新字典导致状态损坏。
///
/// v3 迁移：仍保留 ICollectionFixture&lt;WpfStaFixture&gt;（而非 IAssemblyFixture），
/// 因为部分非 UI 测试（如 ThresholdConvertersTests）需要资源初始化但不需要 fixture 注入，
/// 用集合名 "WpfUi" 显式标记比全局 assembly fixture 更精确。
/// </summary>
[CollectionDefinition("WpfUi")]
public class WpfUiTestCollection : ICollectionFixture<WpfStaFixture>
{
    // 该类仅供 xUnit 标识集合用，无成员
}
