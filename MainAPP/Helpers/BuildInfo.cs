namespace MainAPP.Helpers;

/// <summary>
/// 构建信息单源（DEBUG 判断）：DeviceManagerViewModel 与 WorkOrderManagerViewModel 的
/// "生成虚拟数据"按钮可见性共用，避免两处重复 #if DEBUG。
/// </summary>
public static class BuildInfo
{
    /// <summary>当前是否为 Debug 构建（Release 下为 false）。</summary>
    public static bool IsDebug
    {
        get
        {
#if DEBUG
            return true;
#else
            return false;
#endif
        }
    }
}
