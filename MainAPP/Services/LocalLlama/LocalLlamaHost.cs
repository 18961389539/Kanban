using System.IO;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace MainAPP.Services;

public interface ILocalLlamaHost : IDisposable
{
    /// <summary>
    /// 需要本地模型时调用。权重不在发布目录里：没有或未下完就从 ModelScope 下载，然后拉起旁边的 llama-server。
    /// </summary>
    Task<Uri> EnsureStartedAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 使用发布目录里的 CPU 版 llama-server。不在程序启动时拉起。
/// </summary>
public sealed class LocalLlamaHost : ILocalLlamaHost
{
    public const string FolderName = "llama.cpp";
    public const string ServerFileName = "llama-server.exe";
    public const int ContextTokens = 16384;

    private readonly ModelScopeModelDownloader _downloader;
    private readonly ILogger<LocalLlamaHost> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private Uri? _endpoint;

    public LocalLlamaHost(ModelScopeModelDownloader downloader, ILogger<LocalLlamaHost> logger)
    {
        _downloader = downloader;
        _logger = logger;
    }

    public static string ServerExecutable(string applicationDirectory)
        => Path.Combine(applicationDirectory, FolderName, ServerFileName);

    public async Task<Uri> EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_process is { HasExited: false } && _endpoint != null)
                return _endpoint;

            var model = ModelScopeModelDownloader.ModelFile(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            await _downloader.EnsureAsync(model, cancellationToken).ConfigureAwait(false);

            var appDirectory = Path.GetDirectoryName(Environment.ProcessPath)
                ?? AppContext.BaseDirectory;
            var server = ServerExecutable(appDirectory);
            if (!File.Exists(server))
                throw new FileNotFoundException("发布目录里没有 CPU 版 llama-server", server);

            var port = FreePort();
            var process = new Process
            {
                StartInfo = new ProcessStartInfo(server)
                {
                    WorkingDirectory = Path.GetDirectoryName(server)!,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            process.StartInfo.ArgumentList.Add("-m");
            process.StartInfo.ArgumentList.Add(model);
            process.StartInfo.ArgumentList.Add("--host");
            process.StartInfo.ArgumentList.Add("127.0.0.1");
            process.StartInfo.ArgumentList.Add("--port");
            process.StartInfo.ArgumentList.Add(port.ToString());
            process.StartInfo.ArgumentList.Add("-c");
            process.StartInfo.ArgumentList.Add(ContextTokens.ToString());
            process.StartInfo.ArgumentList.Add("-ngl");
            process.StartInfo.ArgumentList.Add("0");
            process.StartInfo.ArgumentList.Add("--jinja");

            if (!process.Start())
                throw new InvalidOperationException("llama-server 没有启动");

            _process = process;
            var endpoint = new Uri($"http://127.0.0.1:{port}/");
            await WaitUntilReadyAsync(endpoint, process, cancellationToken).ConfigureAwait(false);
            _endpoint = endpoint;
            _logger.LogInformation("llama-server 已就绪 {Endpoint}", endpoint);
            return endpoint;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        var process = _process;
        _process = null;
        _endpoint = null;
        if (process == null)
            return;
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "结束 llama-server 失败");
        }
        process.Dispose();
        _gate.Dispose();
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitUntilReadyAsync(Uri endpoint, Process process, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
                throw new InvalidOperationException($"llama-server 已退出，代码 {process.ExitCode}");
            try
            {
                using var response = await client.GetAsync(new Uri(endpoint, "health"), cancellationToken)
                    .ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException("llama-server 在 3 分钟内没有就绪");
    }
}
