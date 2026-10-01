using System.IO;
using System.Net;
using System.Net.Http;
using MainAPP.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MainAPP.Tests.Unit;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public class ModelScopeModelDownloaderTests
{
    [Fact]
    public void ModelFile_LivesUnderUserData_NotBesideTheApp()
    {
        var path = ModelScopeModelDownloader.ModelFile(@"C:\Users\operator\AppData\Local");
        Assert.Equal(
            Path.Combine(@"C:\Users\operator\AppData\Local", "Kanban", "models", "MiniCPM5-2B-Q4_K_M.gguf"),
            path);
        Assert.DoesNotContain("llama.cpp", path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ModelUrl_IsModelScope()
    {
        var uri = new Uri(ModelScopeModelDownloader.ModelUrl);
        Assert.Equal("www.modelscope.cn", uri.Host);
        Assert.EndsWith("MiniCPM5-2B-Q4_K_M.gguf", uri.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public void ServerExecutable_IsBesideThePublishedApp()
    {
        var path = LocalLlamaHost.ServerExecutable(@"D:\Publish\win-x64");
        Assert.Equal(
            Path.Combine(@"D:\Publish\win-x64", "llama.cpp", "llama-server.exe"),
            path);
    }

    [Fact]
    public async Task EnsureAsync_SkipsDownload_WhenFileIsAlreadyComplete()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kanban-model-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var destination = Path.Combine(dir, ModelScopeModelDownloader.ModelFileName);
        try
        {
            await File.WriteAllBytesAsync(destination, [1, 2, 3, 4]);

            var downloader = new ModelScopeModelDownloader(
                NullLogger<ModelScopeModelDownloader>.Instance,
                new ThrowingHandler());
            var result = await downloader.EnsureAsync(destination, expectedBytes: 4);
            Assert.Equal(destination, result);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureAsync_ResumesAfterTheConnectionDrops()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kanban-model-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var destination = Path.Combine(dir, ModelScopeModelDownloader.ModelFileName);
        var handler = new ScriptedHandler(
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new PrefixThenDropStream([1, 2, 3, 4], dropAfter: 2)),
            },
            request =>
            {
                Assert.Equal(2, request.Headers.Range?.Ranges.Single().From);
                Assert.Equal(HttpVersion.Version11, request.Version);
                return new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent([3, 4]),
                };
            });
        try
        {
            var downloader = new ModelScopeModelDownloader(NullLogger<ModelScopeModelDownloader>.Instance, handler);
            await downloader.EnsureAsync(destination, expectedBytes: 4);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(destination));
            Assert.Equal(2, handler.Calls);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureAsync_StopsWhenTheConnectionMakesNoProgress()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kanban-model-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var destination = Path.Combine(dir, ModelScopeModelDownloader.ModelFileName);
        var handler = new ScriptedHandler(_ => throw new IOException(
            "Received an unexpected EOF or 0 bytes from the transport stream."));
        try
        {
            var downloader = new ModelScopeModelDownloader(NullLogger<ModelScopeModelDownloader>.Instance, handler);
            var error = await Assert.ThrowsAsync<IOException>(() => downloader.EnsureAsync(destination, expectedBytes: 4));
            Assert.Contains("已得到 0 字节", error.Message);
            Assert.Equal(3, handler.Calls);
            Assert.False(File.Exists(destination));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureAsync_DoesNotRetryAMissingFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kanban-model-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var destination = Path.Combine(dir, ModelScopeModelDownloader.ModelFileName);
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        try
        {
            var downloader = new ModelScopeModelDownloader(NullLogger<ModelScopeModelDownloader>.Instance, handler);
            await Assert.ThrowsAsync<HttpRequestException>(() => downloader.EnsureAsync(destination, expectedBytes: 4));
            Assert.Equal(1, handler.Calls);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("完整文件不应再请求 ModelScope");
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage>[] _steps;

        public ScriptedHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] steps) => _steps = steps;

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var step = _steps[Math.Min(Calls, _steps.Length - 1)];
            Calls++;
            return Task.FromResult(step(request));
        }
    }

    /// <summary>先吐出前几个字节，再抛出和线上一样的传输中断。</summary>
    private sealed class PrefixThenDropStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _dropAfter;
        private int _position;

        public PrefixThenDropStream(byte[] data, int dropAfter)
        {
            _data = data;
            _dropAfter = dropAfter;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(Span<byte> buffer)
        {
            if (_position >= _dropAfter)
                throw new IOException("Received an unexpected EOF or 0 bytes from the transport stream.");
            var count = Math.Min(buffer.Length, _dropAfter - _position);
            _data.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => new(Read(buffer.Span));
    }
}
