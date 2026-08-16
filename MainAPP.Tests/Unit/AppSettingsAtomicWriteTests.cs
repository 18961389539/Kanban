using System.IO;
using Kanban.Collector.Core.Services;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// WriteFileAtomically 备份失败容错回归测试。
/// 背景：登录时 users.json 的 .bak 备份目标被占用/只读/安全软件短暂锁定，
/// File.Copy 抛 UnauthorizedAccessException（与 IOException 平级、非其子类），
/// 旧实现只 catch IOException，异常冒泡到 UI 线程导致 MainAPP 进程崩溃退出（Event ID 1026）。
/// 修复后：备份失败应被吞掉并记录警告，主写入流程继续。
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public class AppSettingsAtomicWriteTests
{
    private readonly string _tempDir;

    public AppSettingsAtomicWriteTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"AtomicWriteTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    /// <summary>目标文件被保留为 .bak 只读（UnauthorizedAccessException 路径，即线上崩溃场景）。</summary>
    [Fact]
    public void WriteFileAtomically_BakReadOnly_DoesNotThrowAndWritesTarget()
    {
        var target = Path.Combine(_tempDir, "users.json");
        var bak = target + ".bak";
        File.WriteAllText(target, "v1");
        File.WriteAllText(bak, "v0");
        File.SetAttributes(bak, FileAttributes.ReadOnly);
        try
        {
            AppSettings.WriteFileAtomically(target, "v2");
        }
        finally
        {
            File.SetAttributes(bak, FileAttributes.Normal);
        }

        Assert.Equal("v2", File.ReadAllText(target));
        Assert.Equal("v0", File.ReadAllText(bak)); // 备份失败，旧备份保持原样
    }

    /// <summary>目标 .bak 被独占打开（IOException 共享冲突路径）。</summary>
    [Fact]
    public void WriteFileAtomically_BakExclusivelyLocked_DoesNotThrowAndWritesTarget()
    {
        var target = Path.Combine(_tempDir, "settings.json");
        var bak = target + ".bak";
        File.WriteAllText(target, "v1");
        File.WriteAllText(bak, "v0");

        using (new FileStream(bak, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            AppSettings.WriteFileAtomically(target, "v2");
        }

        Assert.Equal("v2", File.ReadAllText(target));
        Assert.Equal("v0", File.ReadAllText(bak));
    }
}
