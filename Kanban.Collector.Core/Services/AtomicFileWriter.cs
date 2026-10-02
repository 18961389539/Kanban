using Serilog;

namespace Kanban.Collector.Core.Services;

/// <summary>
/// 配置文件原子写入。先写临时文件再替换目标，并保留上一版 .bak。
/// devices.json、settings.json、baselines.json、users.json 共用。
/// </summary>
public static class AtomicFileWriter
{
    private static readonly Dictionary<string, object> s_pathLocks = new();
    private static readonly object s_pathLocksLock = new();

    public static void Write(string path, string content)
    {
        object? pathLock;
        lock (s_pathLocksLock)
        {
            if (!s_pathLocks.TryGetValue(path, out pathLock))
            {
                pathLock = new object();
                s_pathLocks[path] = pathLock;
            }
        }

        lock (pathLock!)
        {
            if (File.Exists(path))
            {
                try
                {
                    File.Copy(path, path + ".bak", overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Warning(ex, "备份文件 {Path} → .bak 失败，继续主写入", path);
                }
            }

            var tempPath = path + "." + Path.GetRandomFileName() + ".tmp";
            try
            {
                File.WriteAllText(tempPath, content);
                File.Move(tempPath, path, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "原子写入 {Path} 失败", path);
                try { if (File.Exists(tempPath)) File.Delete(tempPath); }
                catch { /* 清理失败时等待下次写入覆盖 */ }
                throw;
            }
        }
    }
}
