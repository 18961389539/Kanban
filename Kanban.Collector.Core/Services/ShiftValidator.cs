using MainAPP.Models;

namespace MainAPP.Services;

/// <summary>
/// 班次配置校验器（纯函数，无副作用，便于单元测试）。
/// 从 SettingsViewModel.ValidateShifts 提取，供 ViewModel 与测试共用。
/// </summary>
public static class ShiftValidator
{
    /// <summary>
    /// 校验班次配置，返回错误信息；返回 null 表示通过。
    /// 规则：
    /// - 至少一个班次
    /// - 班次名非空且唯一（忽略大小写）
    /// - 开始时间 ≠ 结束时间
    /// - 全天 1440 分钟被恰好覆盖一次（无空隙、无重叠）
    /// </summary>
    public static string? Validate(IEnumerable<ShiftConfig> shifts)
    {
        if (shifts == null)
            return "班次配置为空";

        var list = shifts.ToList();
        if (list.Count == 0)
            return "至少配置一个班次";

        for (int i = 0; i < list.Count; i++)
        {
            var s = list[i];
            if (string.IsNullOrWhiteSpace(s.Name))
                return $"第 {i + 1} 个班次名称不能为空";
            if (s.StartTime == s.EndTime)
                return $"班次「{s.Name}」开始时间和结束时间不能相同";
        }

        var dupName = list.GroupBy(s => s.Name!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).FirstOrDefault();
        if (dupName != null)
            return $"存在重名班次：「{dupName.Key}」";

        // 用 bool[1440] 检查每分钟恰好被一个班次覆盖
        var covered = new bool[1440];
        foreach (var s in list)
        {
            int start = (int)s.StartTime.TotalMinutes;
            int end = (int)s.EndTime.TotalMinutes;
            // 跨天：拆成 [start, 1440) + [0, end)
            if (end <= start)
            {
                for (int m = start; m < 1440; m++)
                {
                    if (covered[m])
                        return $"班次「{s.Name}」与其他班次在 {m / 60:D2}:{m % 60:D2} 重叠";
                    covered[m] = true;
                }
                for (int m = 0; m < end; m++)
                {
                    if (covered[m])
                        return $"班次「{s.Name}」与其他班次在 {m / 60:D2}:{m % 60:D2} 重叠";
                    covered[m] = true;
                }
            }
            else
            {
                for (int m = start; m < end; m++)
                {
                    if (covered[m])
                        return $"班次「{s.Name}」与其他班次在 {m / 60:D2}:{m % 60:D2} 重叠";
                    covered[m] = true;
                }
            }
        }

        for (int m = 0; m < 1440; m++)
        {
            if (!covered[m])
                return $"班次配置存在时间空隙：{m / 60:D2}:{m % 60:D2} 不属于任何班次";
        }

        return null;
    }
}
