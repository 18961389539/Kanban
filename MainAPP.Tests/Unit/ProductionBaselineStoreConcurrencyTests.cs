using System.Collections.Generic;
using System.IO;
using System.Threading;
using Kanban.Core.Services;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// ProductionBaselineStore 写盘乱序回归测试（审查修复 2026-08-13）：
/// 旧实现"锁内捕获快照、锁外写盘"在并发变更下，旧快照可能后落盘覆盖新快照，
/// 导致磁盘基线丢失/复活。修复后的版本复查循环（SaveWithRecheck）保证最终落盘收敛到最新快照。
/// 通过子类重写 internal virtual SaveToFile 注入确定性写盘延迟（阻塞指定序号的那次写），
/// 构造"旧快照最后落盘"乱序，且保证旧快照的**变更**发生在后写者之前（阻塞点紧跟在锁内变更之后）。
/// </summary>
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class ProductionBaselineStoreConcurrencyTests
{
    /// <summary>按序号阻塞指定的一次写盘（序号 = 全生命周期第 N 次 SaveToFile），其余写盘正常。</summary>
    private sealed class DelayedSaveStore : ProductionBaselineStore
    {
        private readonly ManualResetEventSlim _blockedWriteStarted = new(false);
        private readonly ManualResetEventSlim _releaseBlockedWrite = new(false);
        private int _writeCount;
        private int _blockWriteIndex = -1;

        public DelayedSaveStore(AppSettings appSettings) : base(appSettings) { }

        /// <summary>阻塞第 N 次写盘（1-based）；-1 不阻塞。</summary>
        public void BlockWriteIndex(int index) => _blockWriteIndex = index;

        public void WaitBlockedWriteStarted() => _blockedWriteStarted.Wait(TimeSpan.FromSeconds(10));

        public void ReleaseBlockedWrite() => _releaseBlockedWrite.Set();

        public int WriteCount => _writeCount;

        internal override void SaveToFile(Dictionary<string, int> baselines, string? shiftId)
        {
            if (Interlocked.Increment(ref _writeCount) == _blockWriteIndex)
            {
                _blockedWriteStarted.Set();
                _releaseBlockedWrite.Wait(TimeSpan.FromSeconds(10));
            }
            base.SaveToFile(baselines, shiftId);
        }
    }

    [Fact]
    public void ConcurrentMutation_OutOfOrderWrite_RecheckConvergesToLatestSnapshot()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "KanbanBaselineConcurrency_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var appSettings = new AppSettings { ConfigDirectory = tempDir };
            var store = new DelayedSaveStore(appSettings);
            store.Load();

            // 第 1 次写：预置 dev-1 基线（不阻塞）
            store.GetOrCreate("dev-1_ok_base", 100, "shift-1");

            // 第 2 次写（阻塞）：线程 A 变更（dev-1 回退校正）→ 锁内版本前进后卡在写盘
            store.BlockWriteIndex(2);
            var first = Task.Run(() => store.GetOrCreate("dev-1_ok_base", 50, "shift-1"));
            store.WaitBlockedWriteStarted();

            // 主线程：第 3 次写（不阻塞）——另一个变更（dev-2 基线）落盘并复查通过 → 磁盘 = {dev-1:50, dev-2:200}
            store.BlockWriteIndex(-1);
            store.GetOrCreate("dev-2_ok_base", 200, "shift-1");

            // 放行 A 的旧快照写盘：A 落盘 {dev-1:50} 后复查发现版本已前进 → 重写最新快照
            store.ReleaseBlockedWrite();
            first.GetAwaiter().GetResult();

            // 磁盘必须收敛到最新状态：两个基线都在，且班次标识正确
            var reloaded = new ProductionBaselineStore(appSettings);
            reloaded.Load();
            Assert.Equal(2, reloaded.LoadedBaselines.Count);
            Assert.Equal(50, reloaded.LoadedBaselines["dev-1_ok_base"]);
            Assert.Equal(200, reloaded.LoadedBaselines["dev-2_ok_base"]);
            Assert.Equal("shift-1", reloaded.BaselineShiftId);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void ConcurrentClearDevice_OutOfOrderWrite_DeletedBaselineDoesNotResurrect()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "KanbanBaselineConcurrency_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var appSettings = new AppSettings { ConfigDirectory = tempDir };
            var store = new DelayedSaveStore(appSettings);
            store.Load();
            store.GetOrCreate("dev-1_ok_base", 100, "shift-1");

            // 第 2 次写（阻塞）：线程 A 的回退校正变更已完成（锁内版本已前进），卡在写旧快照
            store.BlockWriteIndex(2);
            var first = Task.Run(() => store.GetOrCreate("dev-1_ok_base", 50, "shift-1"));
            store.WaitBlockedWriteStarted();

            // 主线程：第 3 次写（不阻塞）——删除设备立即落盘 → 磁盘 = {}
            store.BlockWriteIndex(-1);
            store.ClearDevice("dev-1");

            // 放行 A：旧快照（含 dev-1）落盘后复查发现版本前进 → 重写最新快照 {}，被删基线不复活
            store.ReleaseBlockedWrite();
            first.GetAwaiter().GetResult();

            var reloaded = new ProductionBaselineStore(appSettings);
            reloaded.Load();
            Assert.Empty(reloaded.LoadedBaselines);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort */ }
        }
    }
}
