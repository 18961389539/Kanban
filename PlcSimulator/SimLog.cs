using System.Text;

namespace PlcSimulator;

/// <summary>
/// 轻量日志：同时写入控制台和 sim_log.txt（UTF-8 追加）。
/// 控制台输出保持简短格式（HH:mm:ss），文件写入完整格式（含日期与级别）便于排查。
/// 文件写入失败不影响主流程。
///
/// 性能：使用持久 <see cref="StreamWriter"/>（AutoFlush）避免每条日志都开关文件，
/// 进程退出时通过 ProcessExit 自动释放。
/// </summary>
internal static class SimLog
{
    private static readonly object _lock = new();
    /// <summary>控制台输出锁：多线程并发写日志时保护「颜色设置-写入-恢复」三步，避免串色（审查修复 2026-08-16）。</summary>
    private static readonly object _consoleLock = new();
    private static string _logFile = "sim_log.txt";
    private static StreamWriter? _logWriter;

    // 日志轮转（审查修复 2026-08-16，F7/O1）：超过 MaxLogBytes 时滚动为 .1~.5，避免长跑无限增长。
    private const long MaxLogBytes = 10 * 1024 * 1024;   // 10MB
    private const int MaxLogFiles = 5;
    private static long _bytesWritten;

    /// <summary>
    /// 控制台写入后的回调：交互模式下由 Program 设置为重绘提示符 "&gt; "，
    /// 以缓解后台日志打断用户输入提示符的问题（P2-12）。非交互模式保持 null。
    /// </summary>
    public static Action? OnConsoleWrite { get; set; }

    static SimLog()
    {
        // 进程退出时确保缓冲刷盘并释放文件句柄
        AppDomain.CurrentDomain.ProcessExit += (s, e) =>
        {
            try { _logWriter?.Flush(); _logWriter?.Dispose(); }
            catch { }
        };
    }

    /// <summary>初始化日志文件路径并打开持久写入器（追加模式）。</summary>
    public static void Initialize(string logFile)
    {
        _logFile = logFile;
        try
        {
            var dir = Path.GetDirectoryName(logFile);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            _logWriter = new StreamWriter(logFile, append: true, Encoding.UTF8) { AutoFlush = true };
        }
        catch
        {
            // 日志文件初始化失败，回退到仅控制台输出
            _logWriter = null;
        }
    }

    /// <summary>信息级日志（设备状态变更、报警、恢复等）。</summary>
    public static void Info(string message) => Write("INFO", message);

    /// <summary>警告级日志（代理异常、超时等可恢复问题）。</summary>
    public static void Warning(string message) => Write("WARN", message, ConsoleColor.Yellow);

    /// <summary>错误级日志（连接失败、读写失败等）。</summary>
    public static void Error(string message) => Write("ERROR", message, ConsoleColor.Red);

    private static void Write(string level, string message, ConsoleColor color = ConsoleColor.Gray)
    {
        var now = DateTime.Now;

        // 控制台输出：保持与原 Console.WriteLine 一致的简短格式；加锁避免多线程串色/串行
        lock (_consoleLock)
        {
            var prevColor = Console.ForegroundColor;
            try
            {
                if (color != ConsoleColor.Gray && prevColor != color)
                    Console.ForegroundColor = color;
                Console.WriteLine($"  {now:HH:mm:ss} {message}");
            }
            finally
            {
                if (prevColor != color)
                    Console.ForegroundColor = prevColor;
            }
        }

        // 后台日志可能打断命令行提示符，通知 Program 重绘
        try { OnConsoleWrite?.Invoke(); }
        catch { }

        // 文件输出：完整格式（含日期与级别），使用持久 StreamWriter 减少文件 I/O；按大小轮转
        try
        {
            if (_logWriter != null)
            {
                var line = $"{now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
                var lineBytes = Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
                lock (_lock)
                {
                    RotateIfNeeded(lineBytes);
                    _logWriter?.WriteLine(line);
                    _bytesWritten += lineBytes;
                }
            }
        }
        catch
        {
            // 日志文件写入失败不应影响仿真主流程
        }
    }

    /// <summary>按大小轮转日志文件：sim_log.txt → .1 → ... → .5（超出部分丢弃）。</summary>
    private static void RotateIfNeeded(int incomingBytes)
    {
        if (_logWriter == null || _bytesWritten + incomingBytes <= MaxLogBytes) return;
        try
        {
            _logWriter.Flush();
            _logWriter.Dispose();
            _logWriter = null;

            var oldest = $"{_logFile}.{MaxLogFiles}";
            if (File.Exists(oldest)) File.Delete(oldest);
            for (int i = MaxLogFiles - 1; i >= 1; i--)
            {
                var src = $"{_logFile}.{i}";
                var dst = $"{_logFile}.{i + 1}";
                if (File.Exists(src)) File.Move(src, dst, overwrite: true);
            }
            if (File.Exists(_logFile)) File.Move(_logFile, $"{_logFile}.1", overwrite: true);

            _logWriter = new StreamWriter(_logFile, append: true, Encoding.UTF8) { AutoFlush = true };
            _bytesWritten = 0;
        }
        catch
        {
            // 轮转失败不影响主流程；下次仍尝试写原文件
            if (_logWriter == null)
            {
                try { _logWriter = new StreamWriter(_logFile, append: true, Encoding.UTF8) { AutoFlush = true }; }
                catch { _logWriter = null; }
            }
        }
    }
}
