using System.Collections.ObjectModel;

namespace MainAPP.Helpers;

/// <summary>
/// ObservableCollection 差分同步工具：以目标列表为基准做最小增量更新，避免全量重建导致列表闪烁。
/// </summary>
public static class ObservableCollectionSyncHelper
{
    /// <summary>
    /// 将 source 同步到 target：移除 target 中不存在于 source 的项，追加 source 中的新增项。
    /// 保留未变化项的引用，避免闪烁。使用 ReferenceEqualityComparer 做引用比较。
    /// 调用方需保证 source 中未变化项与 target 中为同一引用（可预先做 Equals 查找替换），
    /// 否则等价于全量重建。
    /// </summary>
    public static void Sync<T>(ObservableCollection<T> target, IList<T> source)
        where T : class
    {
        // 移除不再存在于 source 的项
        for (int i = target.Count - 1; i >= 0; i--)
        {
            if (!source.Contains(target[i], ReferenceEqualityComparer.Instance))
                target.RemoveAt(i);
        }

        // 追加/插入 source 中的项
        for (int i = 0; i < source.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], source[i]))
                continue;
            if (i < target.Count)
                target[i] = source[i];
            else
                target.Add(source[i]);
        }
    }
}
