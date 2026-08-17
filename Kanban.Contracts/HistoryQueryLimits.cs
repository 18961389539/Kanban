namespace Kanban.Contracts;

/// <summary>
/// 历史查询跨进程契约常量（单一来源）。服务端（Core/Collector）与客户端（WPF/Web）共用，
/// 替代此前各层硬编码 + 注释对齐的分散定义（MaxPageSize/MaxPage/MaxBatchQueries/
/// MaxQueryWindow/FetchAllPageSize/MaxFetchAllPages 等），改一处即可全局生效。
/// </summary>
public static class HistoryQueryLimits
{
    /// <summary>单页上限：超过则截断（防止一次拉取百万行经 SignalR 传输）。</summary>
    public const int MaxPageSize = 500;

    /// <summary>页码上限：与 <see cref="MaxPageSize"/> 相乘后偏移量限制在约 10 万行内（防深分页 DoS，
    /// Skip 为 O(offset)，无鉴权客户端 page=100000 可造成 5×10^7 偏移拖垮 SQLite）。</summary>
    public const int MaxPage = 200;

    /// <summary>单次批量查询子查询数上限（防无鉴权/异常客户端一次发起数十个全表查询拖垮 SQLite）。</summary>
    public const int MaxBatchQueries = 32;

    /// <summary>单查询最大时间跨度（天）：覆盖 WPF/Web 的 近30天/本月 快捷档。</summary>
    public const int MaxQueryWindowDays = 31;

    /// <summary>未指定时间范围时的默认窗口（小时），替代全表（MinValue..MaxValue）扫描。</summary>
    public const int DefaultQueryWindowHours = 24;

    /// <summary>全量拉取的最大页数（<see cref="MaxPageSize"/> × 本值 = 10 万条上限）：防服务端 Total
    /// 语义异常时无限翻页。</summary>
    public const int MaxFetchAllPages = 200;
}
