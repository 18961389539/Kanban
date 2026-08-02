using Xunit;

namespace MainAPP.UIAutomation;

/// <summary>
/// UI 自动化测试集合：所有用例归入同一 collection，xUnit 默认串行执行同 collection 测试。
/// 原因：被测 MainAPP.exe 使用 Global\Kanban_SingleInstance Mutex 禁止多开，
/// 多个测试并行启动会因 Mutex 冲突弹窗退出。
/// </summary>
[CollectionDefinition("UIA")]
public class UiaTestCollection { }
