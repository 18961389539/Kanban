namespace Kanban.Core.Services;

/// <summary>
/// <see cref="PlcConnectionManager"/> 的测试访问助手（partial）。
/// 仅 internal，测试项目通过 InternalsVisibleTo 可见；生产代码不调用。
/// 拆分到独立文件避免污染主类的可读性，新增测试助手时优先加在此处。
/// </summary>
public partial class PlcConnectionManager
{
    /// <summary>
    /// 测试用：重置连接冷却期（_lastConnectAttempt 设为 MinValue），使下一次 EnsureConnected 立即尝试连接，
    /// 避免单测等待指数退避冷却期（5s+）。通过 internal 钩子替代反射，避免重命名字段时测试静默失败。
    /// </summary>
    internal void ResetConnectCooldownForTest()
    {
        lock (_stateLock)
        {
            _lastConnectAttempt = DateTime.MinValue;
        }
    }
}
