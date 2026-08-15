using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Serilog;

namespace Kanban.Core.Services;

/// <summary>
/// 产量基线存储（彻底归位）：原寄居在 AppSettings 上的「按设备产量基线」现归位于本独立服务。
///
/// 基线 key 形如 "{deviceId}_ok_base" / "{deviceId}_ng_base"，value 为班次起始时的 PLC 累计值，
/// 用于把 PLC 单调计数器换算成「本班次会话增量」(当前值 - 基线)。
///
/// 本类持有两份数据，职责分离：
/// - <see cref="_baselines"/>：本会话活动缓存，等价于旧代码中的服务字段 _cumulativeBase，是所有读取/写入的最终落点；Save 时序列化的就是它。
/// - <see cref="_loadedBaselines"/> / <see cref="BaselineShiftId"/>：启动时从磁盘(baselines.json)加载的快照，
///   仅用于「本会话首次读取某 key」时的恢复判定（班次一致才恢复，否则以当前 PLC 值为新基线）。
///   一旦该 key 在活动缓存中建立，后续读取不再参考此快照，避免跨班次误恢复。
///
/// 持久化复用 AppSettings.WriteFileAtomically（临时文件 + 重命名 + .bak 备份），
/// 与 devices.json / settings.json 同一套原子写入机制，避免断电/崩溃导致 baselines.json 损坏。
///
/// 线程安全：GetOrCreate/ClearDevice/ClearAll/SaveBaselines 在锁内更新字典与 BaselineShiftId，
/// 序列化+原子写盘在锁外执行（P1-6：避免慢盘阻塞轮询线程对其他设备基线的读取），
/// 写盘完成后回锁复查版本号，并发写者乱序落盘时以最新快照重写直至收敛（审查修复 2026-08-13）。
/// </summary>
public class ProductionBaselineStore(AppSettings appSettings)
{
    private readonly object _lock = new();
    private readonly string _filePath = appSettings.GetFilePath("baselines.json");

    /// <summary>
    /// 写盘版本号：锁内每次变更 ++；写者携带捕获版本落盘后回锁复查，
    /// 版本已前进说明期间有更新的变更，需用最新快照重写，杜绝乱序写盘丢失更新。
    /// </summary>
    private long _saveVersion;

    /// <summary>本会话活动缓存（等价于旧 _cumulativeBase）。</summary>
    private Dictionary<string, int> _baselines = new();

    /// <summary>磁盘快照，仅用于首次读取的恢复判定；ClearAll/ClearDevice 时同步清空。</summary>
    private Dictionary<string, int> _loadedBaselines = new();

    /// <summary>当前持久化基线所属班次标识（"Name|StartTime|EndTime"）。用于跨班次防串账。</summary>
    public string? BaselineShiftId { get; private set; }

    /// <summary>
    /// 从 baselines.json 加载基线快照到内存（不写入活动缓存）。
    /// 文件不存在或反序列化失败则保持空快照。
    /// 兼容旧格式（文件直接是 Dictionary）：旧格式无班次标识，BaselineShiftId 置 null，
    /// 调用方首次读取时会据此判定班次不一致并丢弃旧基线、以当前 PLC 累计值重新建立。
    /// </summary>
    public void Load()
    {
        if (!File.Exists(_filePath)) return;
        try
        {
            var json = File.ReadAllText(_filePath);
            var file = JsonSerializer.Deserialize<BaselineFile>(json);
            if (file?.Baselines != null)
            {
                _loadedBaselines = file.Baselines;
                BaselineShiftId = file.ShiftId;
                return;
            }

            // 兼容旧版 baselines.json（顶层即 Dictionary）
            var legacy = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
            _loadedBaselines = legacy ?? new();
            BaselineShiftId = null;
        }
        catch (Exception ex)
        {
            // 文件损坏：备份原文件供用户排查（参照 AppSettings 的 .corrupt 机制），
            // 避免静默丢弃后下一轮 Save 覆写导致原始数据永久丢失。
            Log.Warning(ex, "baselines.json 解析失败，将备份原文件并回退空快照");
            var corruptPath = _filePath + ".corrupt";
            try
            {
                if (File.Exists(corruptPath)) File.Delete(corruptPath);
                File.Move(_filePath, corruptPath);
            }
            catch (Exception backupEx)
            {
                Log.Warning(backupEx, "baselines.json 备份为 .corrupt 失败，原文件保留原位");
            }

            _loadedBaselines = new();
            BaselineShiftId = null;
        }
    }

    /// <summary>
    /// 取（或建立）某设备基线的当前值。
    /// - 活动缓存缺该 key（本会话首次读取）：若磁盘基线班次与当前班次一致则恢复旧基线，否则以 raw 为新基线。
    /// - 活动缓存已有该 key 且 raw &lt; baseline（PLC 计数器回退/外部清零）：以 raw 为新基线。
    /// - 其余情况直接返回活动缓存中的基线。
    /// 基线发生变更时立即持久化（原子写），保证每班次/每次校正都落盘。
    /// 持久化在锁外执行：锁内仅更新字典与 BaselineShiftId 并捕获快照+版本号，锁外做 JSON 序列化+写盘，
    /// 避免慢盘阻塞轮询线程对其他设备基线的并发读取；写盘后回锁复查版本，乱序时重写最新快照。
    /// </summary>
    /// <param name="key">基线 key，形如 "{deviceId}_ok_base"。</param>
    /// <param name="raw">本次读取到的 PLC 累计值。</param>
    /// <param name="currentShiftId">当前班次标识，用于首次读取的班次一致判定。</param>
    /// <returns>应作为减数的基线值。</returns>
    public int GetOrCreate(string key, int raw, string? currentShiftId)
    {
        string? shiftIdToSave = null;
        Dictionary<string, int>? snapshotToSave = null;
        long versionToSave = 0;
        int baseline;
        lock (_lock)
        {
            bool changed;
            if (!_baselines.TryGetValue(key, out baseline))
            {
                // 首次读取本会话：磁盘基线班次与当前一致才恢复，否则以当前 raw 为新基线。
                // 恢复时同样检查 raw < saved：PLC 计数器已被外部清零（raw < 磁盘基线）时
                // 若沿用 saved，当轮 raw - baseline 会算出负产量（审查修复 2026-08-15）。
                if (BaselineShiftId == currentShiftId && _loadedBaselines.TryGetValue(key, out var saved))
                    baseline = raw < saved ? raw : saved;
                else
                    baseline = raw;
                _baselines[key] = baseline;
                changed = true;
            }
            else if (raw < baseline)
            {
                // PLC 计数器回退（外部手动清零或班次切换后 PLC 程序清零）
                baseline = raw;
                _baselines[key] = baseline;
                changed = true;
            }
            else
            {
                baseline = _baselines[key];
                changed = false;
            }

            if (changed)
            {
                BaselineShiftId = currentShiftId;
                shiftIdToSave = currentShiftId;
                // 复制一份快照，在锁外序列化写盘，避免持有锁期间阻塞其他读基线操作
                snapshotToSave = new Dictionary<string, int>(_baselines);
                versionToSave = ++_saveVersion;
            }
        }

        // 锁外序列化+原子写盘：慢盘不影响其他设备基线的并发读取
        if (snapshotToSave != null)
            SaveWithRecheck(snapshotToSave, shiftIdToSave, versionToSave);
        return baseline;
    }

    /// <summary>
    /// 清空某个设备的全部基线（删设备时调用），并持久化。
    /// 同时清理活动缓存与磁盘快照中的该设备 key，避免文件随设备增删膨胀。
    /// </summary>
    public void ClearDevice(string deviceId)
    {
        var prefix = deviceId + "_";
        string? shiftIdToSave;
        Dictionary<string, int> snapshotToSave;
        long versionToSave;
        lock (_lock)
        {
            RemovePrefixed(_baselines, prefix);
            RemovePrefixed(_loadedBaselines, prefix);
            shiftIdToSave = BaselineShiftId;
            snapshotToSave = new Dictionary<string, int>(_baselines);
            versionToSave = ++_saveVersion;
        }
        SaveWithRecheck(snapshotToSave, shiftIdToSave, versionToSave);
    }

    /// <summary>
    /// 清空全部基线（班次切换 ResetShift 时调用），并以新班次标识持久化空基线。
    /// 同时清空活动缓存与磁盘快照，使下一轮读取以当前 PLC 值重建基线（不误恢复旧班次）。
    /// </summary>
    public void ClearAll(string? shiftId)
    {
        Dictionary<string, int> emptySnapshot;
        long versionToSave;
        lock (_lock)
        {
            _baselines.Clear();
            _loadedBaselines.Clear();
            BaselineShiftId = shiftId;
            emptySnapshot = new Dictionary<string, int>(_baselines);
            versionToSave = ++_saveVersion;
        }
        SaveWithRecheck(emptySnapshot, shiftId, versionToSave);
    }

    /// <summary>
    /// 直接写入整份基线（测试 / 诊断 / 迁移场景用）。会替换活动缓存并原子落盘。
    /// 正常采集路径不应调用本方法，应走 GetOrCreate / ClearAll / ClearDevice。
    /// </summary>
    public void SaveBaselines(Dictionary<string, int> baselines, string? shiftId)
    {
        Dictionary<string, int> snapshot;
        long versionToSave;
        lock (_lock)
        {
            _baselines = new Dictionary<string, int>(baselines);
            BaselineShiftId = shiftId;
            snapshot = new Dictionary<string, int>(_baselines);
            versionToSave = ++_saveVersion;
        }
        SaveWithRecheck(snapshot, shiftId, versionToSave);
    }

    /// <summary>
    /// 最近一次 Load() 从磁盘读取到的基线快照（只读）。用于测试断言与诊断展示。
    /// </summary>
    public IReadOnlyDictionary<string, int> LoadedBaselines => _loadedBaselines;

    /// <summary>
    /// 测试用：本会话活动缓存中的基线条目数（线程安全）。
    /// 长期累积泄漏测试用于断言"删除设备后基线条目被清理，字典不无限增长"。
    /// </summary>
    internal int ActiveBaselineCount
    {
        get
        {
            lock (_lock)
                return _baselines.Count;
        }
    }

    private static void RemovePrefixed(Dictionary<string, int> dict, string prefix)
    {
        List<string> toRemove = [];
        foreach (var key in dict.Keys)
            if (key.StartsWith(prefix, StringComparison.Ordinal)) toRemove.Add(key);
        foreach (var key in toRemove) dict.Remove(key);
    }

    /// <summary>
    /// 锁外写盘 + 版本复查收敛：写完后回锁比对捕获版本，期间有新变更则取最新快照重写，
    /// 直至落盘内容确认为最新状态。并发写者乱序（先捕获的旧快照后落盘）时由复查循环纠正
    /// （审查修复 2026-08-13：修复旧快照覆盖新快照导致基线丢失/复活的竞态）。
    /// </summary>
    private void SaveWithRecheck(Dictionary<string, int> snapshot, string? shiftId, long version)
    {
        while (true)
        {
            SaveToFile(snapshot, shiftId);
            lock (_lock)
            {
                if (_saveVersion == version)
                    return;
                snapshot = new Dictionary<string, int>(_baselines);
                shiftId = BaselineShiftId;
                version = _saveVersion;
            }
        }
    }

    /// <summary>
    /// 原子写入 baselines.json（复用 AppSettings.WriteFileAtomically：临时文件 + 重命名 + .bak 备份）。
    /// 在锁外调用：调用方需在锁内捕获快照副本后释放锁再调用本方法。
    /// internal virtual：供测试子类注入写盘延迟，构造确定性的乱序写场景（回归测试用）。
    /// </summary>
    internal virtual void SaveToFile(Dictionary<string, int> baselines, string? shiftId)
    {
        var file = new BaselineFile { ShiftId = shiftId, Baselines = baselines };
        var json = JsonSerializer.Serialize(file, AppSettings.JsonOptions);
        AppSettings.WriteFileAtomically(_filePath, json);
    }

    /// <summary>
    /// baselines.json 的内部结构：基线字典 + 所属班次标识。
    /// </summary>
    private sealed class BaselineFile
    {
        public string? ShiftId { get; set; }
        public Dictionary<string, int>? Baselines { get; set; }
    }
}
