namespace Kanban.Core.Services;

/// <summary>
/// 历史查询分页参数归一化：防止客户端传入的 Page/PageSize 造成
/// 超大偏移（Skip 溢出）、超大单页（全量传输）或非正参数（EF 抛异常）。
/// </summary>
public static class HistoryPagination
{
    /// <summary>单页上限：超过则截断（防止一次拉取百万行经 SignalR 传输）。</summary>
    public const int MaxPageSize = 500;

    /// <summary>页码上限：与 MaxPageSize 相乘后偏移量限制在约 10 万行内（防深分页 DoS——Skip 为 O(offset)，
    /// 无鉴权客户端此前可 page=100000 造成 5×10^7 偏移拖垮 SQLite）。与客户端 MaxFetchAllPages=200 对齐。</summary>
    public const int MaxPage = 200;

    /// <summary>归一化页码与页大小（均 Clamp 到安全范围）。</summary>
    public static (int Page, int PageSize) Normalize(int page, int pageSize)
    {
        var p = Math.Clamp(page, 1, MaxPage);
        var s = Math.Clamp(pageSize, 1, MaxPageSize);
        return (p, s);
    }

    /// <summary>计算安全偏移量（基于已归一化参数）。</summary>
    public static int Offset(int page, int pageSize)
    {
        var (p, s) = Normalize(page, pageSize);
        return (p - 1) * s;
    }
}
