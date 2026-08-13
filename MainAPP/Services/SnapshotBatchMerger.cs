using Kanban.Contracts.Dtos;

namespace MainAPP.Services;

/// <summary>
/// 快照合并缓冲：按设备去重（同一设备只保留最新一帧），供 Dispatcher 节流批量应用。
/// SignalR 回调线程写入（<see cref="Add"/>），UI 线程定时 <see cref="Drain"/>——内部 gate 保证并发安全。
/// 语义依据：快照是全量状态（最终一致），同设备旧帧可安全丢弃、只应用最新一帧；
/// 不同设备互不覆盖。报警/状态事件不可去重（边沿语义），由调用方另行保序批量应用。
/// </summary>
internal sealed class SnapshotBatchMerger
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DeviceSnapshotDto> _latestByDevice = new(StringComparer.Ordinal);

    /// <summary>写入一帧快照：同设备已有旧帧时直接覆盖（只保留最新）。</summary>
    public void Add(DeviceSnapshotDto snapshot)
    {
        lock (_gate)
            _latestByDevice[snapshot.DeviceId] = snapshot;
    }

    /// <summary>取出全部待应用快照（每设备最新一帧）并清空缓冲。空缓冲返回空列表。</summary>
    public List<DeviceSnapshotDto> Drain()
    {
        lock (_gate)
        {
            if (_latestByDevice.Count == 0)
                return [];
            var result = new List<DeviceSnapshotDto>(_latestByDevice.Count);
            foreach (var pair in _latestByDevice)
                result.Add(pair.Value);
            _latestByDevice.Clear();
            return result;
        }
    }
}
