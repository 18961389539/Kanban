namespace MainAPP.Services;

/// <summary>
/// 导航服务接口：基于名称的页面导航，替代魔术数字索引。
/// </summary>
public interface INavigationService
{
    /// <summary>导航到指定名称的页面。</summary>
    void Navigate(string pageKey);
}
