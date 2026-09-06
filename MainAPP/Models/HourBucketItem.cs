namespace MainAPP.Models;

/// <summary>本班小时计划格的状态：过去达标 / 过去未达标 / 当前小时 / 未到点。</summary>
public enum HourBucketState
{
    Hit,
    Miss,
    Current,
    Future,
}

/// <summary>
/// 设备详情「本班小时计划」一格：计划 = 该机额定产能按该小时重叠分钟折算，实际 = 该小时 OK。
/// </summary>
public sealed record HourBucketItem(
    DateTime Start,
    DateTime End,
    string TimeLabel,
    int Plan,
    int? Actual,
    HourBucketState State)
{
    public string ActualDisplay => Actual?.ToString() ?? "—";

    public bool IsHit => State == HourBucketState.Hit;
    public bool IsMiss => State == HourBucketState.Miss;
    public bool IsCurrent => State == HourBucketState.Current;
    public bool IsFuture => State == HourBucketState.Future;
}
