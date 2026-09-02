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
    /// 调用方需保证 source 中未变化项与 target 中为同一引用（可预先做 Equals 查找替换，
    /// 或直接用 <see cref="ReuseExisting{T}"/>），否则等价于全量重建。
    /// </summary>
    public static void Sync<T>(ObservableCollection<T> target, IList<T> source)
        where T : class
    {
        // 先把 source 引用集合化：原先 source.Contains(target[i], ReferenceEqualityComparer)
        // 在 IList<T> 上落到 Enumerable.Contains —— 每个 target 项一次 O(N) 线性扫，整体 O(N²)。
        // 报警中心 200 条 × 每 3s 一次时是纯浪费；HashSet 化后整趟 O(N)。
        var sourceSet = new HashSet<T>(source, ReferenceEqualityComparer.Instance);

        // 移除不再存在于 source 的项
        for (int i = target.Count - 1; i >= 0; i--)
        {
            if (!sourceSet.Contains(target[i]))
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

    /// <summary>
    /// 按身份键把 source 中与 target 既有项相同的条目替换为 target 的既有实例（引用复用），
    /// 使随后的 <see cref="Sync{T}"/> 引用差分真正命中——未变化项零集合事件，
    /// 虚拟化容器不重建、滚动位置保留。
    /// </summary>
    /// <param name="target">目标集合（同时是既有实例的来源）。</param>
    /// <param name="source">期望列表；命中的条目会被就地替换为 target 中的既有实例。</param>
    /// <param name="identityKey">
    /// 身份键选择器。键必须覆盖全部「UI 依赖的只读展示字段」：
    /// 键相同 → 复用实例（可变字段由调用方原地刷新，项须实现 INPC 才能驱动 UI）；
    /// 键不同（如报警重新触发导致 EventTime 变化）→ 不复用，走 Replace 让 UI 取到新值。
    /// </param>
    public static void ReuseExisting<T>(
        ObservableCollection<T> target,
        IList<T> source,
        Func<T, object> identityKey)
        where T : class
    {
        if (target.Count == 0 || source.Count == 0)
            return;

        var existingByKey = new Dictionary<object, T>(target.Count);
        foreach (var item in target)
        {
            var key = identityKey(item);
            if (!existingByKey.ContainsKey(key))
                existingByKey[key] = item; // target 内同键重复时保留首个
        }

        for (var i = 0; i < source.Count; i++)
        {
            if (existingByKey.TryGetValue(identityKey(source[i]), out var kept))
                source[i] = kept;
        }
    }
}
