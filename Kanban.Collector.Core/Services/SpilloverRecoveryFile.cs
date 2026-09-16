using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Kanban.Collector.Core.Services;

/// <summary>
/// 溢出恢复文件（JSONL）：有界队列满载/落库失败时的"不丢数据"兜底通道。
/// 语义照搬 ProductionHistoryWriter 恢复文件（设计审查修复 2026-09-16 泛化复用）：
/// - 追加：锁内 <see cref="File.AppendAllLines(string, IEnumerable{string})"/>（O(1)，溢出高发期不做全文件重写）；
///   超过大小上限停止追加并告警（有界且大声的丢失 &gt; 无限磁盘占用，同 MaxRecoveryFileBytes 决策）；
/// - 回放：锁内取字节快照 → 锁外分批插库 → 锁内收尾：回放期间无新追加则删文件，
///   有新追加则按字节偏移切出未处理尾部原子重写（追加只发生在 EOF，偏移切分安全）；
/// - 坏行转存 .bad 后剔除（避免每轮回放反复失败）；回放失败退避 30s（DB 持续不可写时不拖垮后台循环）；
/// - 崩溃窗口：插库提交与文件收尾之间崩溃会重放一个批次（at-least-once）。
///   审计/缺陷实体无 EventId 唯一键，无法做 ProductionHistoryWriter 式幂等去重——
///   对"防静默丢失"场景，极窄窗口的重复远优于数据缺口（合规审计宁可重复不可缺）。
/// </summary>
internal sealed class SpilloverRecoveryFile<T>
{
    private static readonly TimeSpan ReplayRetryDelay = TimeSpan.FromSeconds(30);

    private readonly string _path;
    private readonly long _maxBytes;
    private readonly ILogger _logger;
    /// <summary>日志前缀（"审计"/"缺陷快照"等），区分多个使用方的日志。</summary>
    private readonly string _name;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _replayGate = new(1, 1);
    /// <summary>上次回放失败时刻（仅回放线程读写）；非 null 且在退避窗口内时跳过回放。</summary>
    private DateTime? _lastReplayFailureAt;

    public SpilloverRecoveryFile(string path, long maxBytes, ILogger logger, string name)
    {
        _path = path;
        _maxBytes = maxBytes;
        _logger = logger;
        _name = name;
    }

    /// <summary>恢复文件路径（测试断言用）。</summary>
    public string Path => _path;

    /// <summary>是否存在待回放内容（文件存在且非空）。</summary>
    public bool HasPending
    {
        get
        {
            try { return File.Exists(_path) && new FileInfo(_path).Length > 0; }
            catch { return false; }
        }
    }

    /// <summary>追加溢出条目（任意线程可调；序列化失败/IO 失败仅记日志，不向调用方抛出）。</summary>
    public void Append(IEnumerable<T> items)
    {
        try
        {
            var lines = items.Select(item => JsonSerializer.Serialize(item)).ToList();
            if (lines.Count == 0) return;
            lock (_lock)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
                if (File.Exists(_path))
                {
                    var newBytes = lines.Sum(l => Encoding.UTF8.GetByteCount(l) + 1);
                    if (new FileInfo(_path).Length + newBytes > _maxBytes)
                    {
                        _logger.LogError("{Name}恢复文件超过上限（{Max:N0} 字节），停止追加转存；请人工处理 {Path}",
                            _name, _maxBytes, _path);
                        return;
                    }
                }
                File.AppendAllLines(_path, lines);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Name}恢复文件追加失败", _name);
        }
    }

    /// <summary>
    /// 回放：把恢复文件条目分批交给 <paramref name="insertBatchAsync"/> 落库，成功后删除/截尾文件。
    /// 并发调用经内部闸门串行；失败退避 <see cref="ReplayRetryDelay"/>。
    /// </summary>
    public async Task ReplayAsync(Func<IReadOnlyList<T>, Task> insertBatchAsync, int batchSize, CancellationToken ct)
    {
        if (!HasPending) return;
        if (_lastReplayFailureAt is { } lastFailure && DateTime.Now - lastFailure < ReplayRetryDelay)
            return;
        await _replayGate.WaitAsync(ct);
        try
        {
            await ReplayCoreAsync(insertBatchAsync, batchSize, ct);
        }
        finally
        {
            _replayGate.Release();
        }
    }

    private async Task ReplayCoreAsync(Func<IReadOnlyList<T>, Task> insertBatchAsync, int batchSize, CancellationToken ct)
    {
        try
        {
            // 阶段 1（锁内）：读取快照 + 记录字节长度（追加只发生在 EOF，收尾按此偏移切尾部）
            string[] lines;
            long snapshotLength;
            lock (_lock)
            {
                if (!File.Exists(_path)) return;
                var info = new FileInfo(_path);
                if (info.Length > _maxBytes)
                {
                    _logger.LogError("{Name}恢复文件超过上限（{Max:N0} 字节），停止回放；请人工处理 {Path}",
                        _name, _maxBytes, _path);
                    return;
                }
                snapshotLength = info.Length;
                lines = File.ReadAllLines(_path);
                if (lines.Length == 0)
                {
                    File.Delete(_path);
                    return;
                }
            }

            // 阶段 2（锁外）：解析 + 分批插库（慢 IO 不持锁，不阻塞追加路径）
            var badLines = new List<string>();
            var replayed = 0;
            var batch = new List<T>(batchSize);
            foreach (var line in lines)
            {
                ct.ThrowIfCancellationRequested();
                T? item;
                try
                {
                    item = JsonSerializer.Deserialize<T>(line);
                }
                catch (JsonException ex)
                {
                    badLines.Add(line);
                    _logger.LogError(ex, "{Name}恢复文件存在损坏记录，已转存 .bad 文件", _name);
                    continue;
                }
                if (item is null) { badLines.Add(line); continue; }
                batch.Add(item);
                if (batch.Count >= batchSize)
                {
                    await insertBatchAsync(batch);
                    replayed += batch.Count;
                    batch.Clear();
                }
            }
            if (batch.Count > 0)
            {
                await insertBatchAsync(batch);
                replayed += batch.Count;
            }

            // 阶段 3（锁内收尾）：切出回放期间的新尾部（字节偏移之后的部分），原子重写或删除
            lock (_lock)
            {
                if (!File.Exists(_path)) return;
                string[] tail;
                using (var stream = File.OpenRead(_path))
                {
                    if (stream.Length < snapshotLength)
                    {
                        // 文件在回放期间被外部替换/截断：保留现状，避免误删他人数据
                        _logger.LogWarning("{Name}恢复文件在回放期间被替换或截断，保留现有文件避免丢失", _name);
                        return;
                    }
                    stream.Seek(snapshotLength, SeekOrigin.Begin);
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    var tailText = reader.ReadToEnd();
                    tail = tailText.Length == 0
                        ? []
                        : tailText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                }

                if (badLines.Count > 0)
                {
                    try
                    {
                        File.AppendAllLines(_path + ".bad", badLines);
                        _logger.LogWarning("{Name}恢复文件 {Count} 条损坏记录已转存 .bad 文件", _name, badLines.Count);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "{Name}损坏记录转存 .bad 失败，保留原恢复文件", _name);
                        return;
                    }
                }

                if (tail.Length == 0)
                {
                    File.Delete(_path);
                }
                else
                {
                    // 原子重写未处理尾部（临时文件 + 替换），防断电留下半写文件
                    var tmp = _path + ".tmp";
                    File.WriteAllLines(tmp, tail);
                    File.Move(tmp, _path, overwrite: true);
                }
                if (replayed > 0)
                    _logger.LogInformation("{Name}恢复文件回放完成：共 {Count} 条", _name, replayed);
            }

            _lastReplayFailureAt = null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Name}恢复文件回放失败，{Delay}s 后重试", _name, (int)ReplayRetryDelay.TotalSeconds);
            _lastReplayFailureAt = DateTime.Now;
        }
    }
}
