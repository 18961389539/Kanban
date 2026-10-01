using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace MainAPP.Services;

/// <summary>
/// MiniCPM5-2B 的 Q4_K_M 权重。发布目录不带这个文件，第一次要用时从 ModelScope 下载到本机用户目录。
/// </summary>
public sealed class ModelScopeModelDownloader
{
    public const string ModelUrl =
        "https://www.modelscope.cn/models/openbmb/MiniCPM5-2B-GGUF/resolve/master/MiniCPM5-2B-Q4_K_M.gguf";

    public const string ModelFileName = "MiniCPM5-2B-Q4_K_M.gguf";

    /// <summary>官方 Q4_K_M 的字节数。对上才算下载完成，避免把半截文件当成可用模型。</summary>
    public const long ModelBytes = 1_561_318_368L;

    private const int MaxDownloadAttempts = 60;
    private const int MaxStalledAttempts = 3;

    private readonly ILogger<ModelScopeModelDownloader> _logger;
    private readonly HttpMessageHandler? _handler;

    public ModelScopeModelDownloader(ILogger<ModelScopeModelDownloader> logger, HttpMessageHandler? handler = null)
    {
        _logger = logger;
        _handler = handler;
    }

    public static string ModelFile(string localAppData)
        => Path.Combine(localAppData, "Kanban", "models", ModelFileName);

    public static bool IsComplete(string path, long expectedBytes = ModelBytes)
        => File.Exists(path) && new FileInfo(path).Length == expectedBytes;

    public Task<string> EnsureAsync(string destination, CancellationToken cancellationToken = default)
        => EnsureAsync(destination, ModelBytes, cancellationToken);

    internal async Task<string> EnsureAsync(string destination, long expectedBytes, CancellationToken cancellationToken = default)
    {
        if (IsComplete(destination, expectedBytes))
        {
            _logger.LogInformation("本地模型已齐全，跳过下载 {Path}", destination);
            return destination;
        }

        var directory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("模型路径没有目录");
        Directory.CreateDirectory(directory);
        var partial = destination + ".partial";
        _logger.LogInformation("从 ModelScope 下载模型到 {Path}", destination);
        await DownloadAsync(partial, expectedBytes, cancellationToken).ConfigureAwait(false);

        var length = new FileInfo(partial).Length;
        if (length != expectedBytes)
            throw new IOException($"模型下载不完整：得到 {length} 字节，应为 {expectedBytes} 字节");

        if (File.Exists(destination))
            File.Delete(destination);
        File.Move(partial, destination);
        _logger.LogInformation("模型下载完成 {Path}", destination);
        return destination;
    }

    /// <summary>
    /// ModelScope 的 CDN 经常在传到一半时把 TLS 连接掐断，页面上就是 unexpected EOF。
    /// 半截文件留下，下一次用 Range 接着传，连续三次没有新字节才失败。
    /// </summary>
    private async Task DownloadAsync(string partialPath, long expectedBytes, CancellationToken cancellationToken)
    {
        using var client = CreateClient();
        var stalled = 0;
        for (var attempt = 1; attempt <= MaxDownloadAttempts; attempt++)
        {
            var before = LengthOrZero(partialPath);
            if (before > expectedBytes)
            {
                File.Delete(partialPath);
                before = 0;
            }

            if (before == expectedBytes)
                return;

            try
            {
                await DownloadOnceAsync(client, partialPath, before, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsRetryable(ex) && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "模型下载连接中断，将从已下载的部分续传");
            }

            var after = LengthOrZero(partialPath);
            if (after == expectedBytes)
                return;

            if (after > before)
                stalled = 0;
            else if (++stalled >= MaxStalledAttempts)
                throw new IOException($"模型下载中断：已得到 {after} 字节，应为 {expectedBytes} 字节。再发送一次会从这里继续。");

            _logger.LogInformation("模型已下载 {Got}/{Expected} 字节，继续", after, expectedBytes);
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }

        throw new IOException($"模型下载中断：已得到 {LengthOrZero(partialPath)} 字节，应为 {expectedBytes} 字节。再发送一次会从这里继续。");
    }

    private HttpClient CreateClient()
    {
        var client = _handler == null
            ? new HttpClient()
            : new HttpClient(_handler, disposeHandler: false);
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Kanban/1.0");
        // 这条 CDN 的 HTTP/2 连接会在长下载中途被掐断。固定 HTTP/1.1。
        client.DefaultRequestVersion = HttpVersion.Version11;
        client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        return client;
    }

    private static async Task DownloadOnceAsync(HttpClient client, string partialPath, long start, CancellationToken cancellationToken)
    {
        using var request = NewGet(start);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (start > 0 && response.StatusCode != HttpStatusCode.PartialContent)
        {
            response.Dispose();
            File.Delete(partialPath);
            using var again = NewGet(0);
            using var full = await client.SendAsync(again, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            full.EnsureSuccessStatusCode();
            await WriteAsync(partialPath, full, 0, cancellationToken).ConfigureAwait(false);
            return;
        }

        response.EnsureSuccessStatusCode();
        await WriteAsync(partialPath, response, start, cancellationToken).ConfigureAwait(false);
    }

    private static HttpRequestMessage NewGet(long start)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, ModelUrl)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        if (start > 0)
            request.Headers.Range = new RangeHeaderValue(start, null);
        return request;
    }

    private static long LengthOrZero(string path)
        => File.Exists(path) ? new FileInfo(path).Length : 0;

    private static bool IsRetryable(Exception ex)
    {
        for (Exception? current = ex; current != null; current = current.InnerException)
        {
            if (current is OperationCanceledException)
                return false;
            if (current is HttpRequestException http)
            {
                if (http.StatusCode is HttpStatusCode status)
                {
                    var code = (int)status;
                    return code is 408 or 429 or >= 500;
                }

                return true;
            }

            if (current is IOException)
                return true;
        }

        return false;
    }

    private static async Task WriteAsync(string partialPath, HttpResponseMessage response, long start, CancellationToken cancellationToken)
    {
        var mode = start > 0 ? FileMode.Append : FileMode.Create;
        await using var file = new FileStream(partialPath, mode, FileAccess.Write, FileShare.None);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await stream.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
    }
}
