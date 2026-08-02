using Xunit;

namespace MainAPP.Tests;

/// <summary>
/// 测试分类常量。用于 <c>[Trait]</c> 的统一筛选键，避免拼写错误。
///
/// 筛选示例（dotnet test --filter 或 MTP -filter-class）：
///   Category=Simulation     只跑 PLC 仿真测试
///   Category=Contract       只跑契约测试
///   Speed=Fast              本地快速反馈（单元测试默认 Fast）
///   Requires!=STA           跳过需要 STA 线程的 UI 测试
///   Requires!=Network       跳过需要 TCP 端口的测试
///   Requires!=Database      跳过数据库测试
///
/// 分类规则（由批量注入脚本依据命名空间/基类/类名自动判定，详见 TestTraits.md）：
/// - 命名空间 MainAPP.Tests.Integration → Category=Integration, Speed=Slow
/// - 命名空间 MainAPP.Tests.Unit        → Category=Unit, Speed=Fast
/// - 继承 WpfTestHost                   → 追加 Requires=STA
/// - 继承 HslPlcSimulationTestBase      → Category=Simulation, Requires=Network
/// - 类名含 ContractTests               → Category=Contract
/// - 类名/文件名含 Database/DbContext/Repository/Wal/Pragma → 追加 Requires=Database
/// - 类名含 License                     → 追加 Requires=License
/// </summary>
public static class TestCategories
{
    /// <summary>大类：Unit / Integration / Simulation / Contract</summary>
    public const string Category = "Category";

    /// <summary>执行速度：Fast (&lt;1s) / Slow。用于本地快速反馈筛选。</summary>
    public const string Speed = "Speed";

    /// <summary>外部依赖：STA / Network / Database / License / None。</summary>
    public const string Requires = "Requires";

    public static class Values
    {
        public const string Unit = "Unit";
        public const string Integration = "Integration";
        public const string Simulation = "Simulation";
        public const string Contract = "Contract";
        public const string Fast = "Fast";
        public const string Slow = "Slow";
        public const string STA = "STA";
        public const string Network = "Network";
        public const string Database = "Database";
        public const string License = "License";
        public const string None = "None";
    }
}
