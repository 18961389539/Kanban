using System.Net;
using System.Net.Sockets;
using HslCommunication;
using HslCommunication.Core.Net;
using Xunit;

namespace MainAPP.Tests.Integration;

/// <summary>
/// HslCommunication 虚拟 PLC 仿真测试基类。
///
/// 在测试进程内启动 HslCommunication 自带的虚拟服务器（<see cref="HslCommunication.Profinet.Siemens.SiemensS7Server"/>
/// 或 <see cref="HslCommunication.ModBus.ModbusTcpServer"/>），让真实驱动
/// （HslSiemensPlcDriver / HslModbusTcpDriver）通过 TCP 回环连接到虚拟服务器，
/// 验证 Connect / Read / Write / Batch / Disconnect / Configure 的端到端真实协议行为。
///
/// 设计要点：
/// - <b>测试隔离</b>：xUnit 每个 [Fact] 创建新的测试类实例 → 构造函数启动新服务器（独立端口）→
///   <see cref="Dispose"/> 关闭服务器；测试间无预设值残留。
/// - <b>TCP 代理</b>：服务器运行在内部端口，<see cref="SimulationTcpRelay"/> 代理监听公开端口。
///   驱动连接到代理端口，代理转发到内部服务器。断线测试通过 <see cref="CutConnection"/> 停止代理实现，
///   而非调用 ServerClose（后者在 AsyncAcceptCallback 中抛出未处理异常，会导致进程崩溃）。
///   此方案与 PlcSimulator 项目一致。
/// - <b>空闲端口</b>：用 <see cref="TcpListener"/> 绑定端口 0 让 OS 分配空闲端口，避免端口冲突。
/// - <b>超时约束</b>：派生类构建 <c>PlcConfig.TimeoutMs=3000</c>，符合项目硬约束
///   "PLC 驱动连接/接收超时 3000ms 防止 UI 阻塞"。
/// </summary>
/// <typeparam name="TServer">HslCommunication 虚拟服务器类型；若无参构造可访问，默认工厂直接实例化，否则派生类重写 <see cref="CreateServer"/>。</typeparam>
public abstract class HslPlcSimulationTestBase<TServer> : IDisposable
    where TServer : NetworkDataServerBase
{
    private readonly TServer _server;
    private readonly SimulationTcpRelay _relay;
    private bool _disposed;

    protected HslPlcSimulationTestBase()
    {
        Port = AllocateFreePort();            // 代理公开端口（驱动连接此处）
        InternalPort = AllocateFreePort();     // 服务器内部端口
        _server = CreateServer();
        // ServerStart 返回 void；端口被占时会抛异常，构造函数失败使测试明确报错。
        _server.ServerStart(InternalPort);
        // 启动 TCP 代理：监听公开端口，转发到内部服务器端口
        _relay = new SimulationTcpRelay(Port, InternalPort);
        _relay.Start();
    }

    /// <summary>
    /// 创建虚拟服务器实例。默认实现用 <see cref="Activator.CreateInstance{T}"/> 调用无参构造
    /// （适用 SiemensS7Server / ModbusTcpServer / OmronFinsServer 等有无参构造的服务器）。
    /// 派生类可重写以调用带参构造，例如 MelsecMcServer 需通过 <c>new MelsecMcServer(true)</c>
    /// 显式指定二进制帧格式。
    /// </summary>
    protected virtual TServer CreateServer() => Activator.CreateInstance<TServer>();

    /// <summary>代理公开端口，驱动通过此端口连接（经代理转发到内部服务器）。</summary>
    protected int Port { get; }

    /// <summary>虚拟服务器内部监听端口（仅代理访问，驱动不直接连接）。</summary>
    protected int InternalPort { get; }

    /// <summary>虚拟服务器实例（实现 IReadWriteNet，可直接预置数据）。</summary>
    protected TServer Server => _server;

    // ──────────── 数据预置辅助（供派生类在服务器端预置期望值） ────────────

    /// <summary>在服务器端预置一个 Int32，供驱动 ReadInt32 读回验证。</summary>
    protected void PresetInt32(string address, int value)
    {
        var w = _server.Write(address, value);
        Assert.True(w.IsSuccess, $"预置 Int32 失败 {address}: {w.Message}");
    }

    /// <summary>在服务器端预置一个 UInt16，供驱动 ReadUInt16 读回验证。</summary>
    protected void PresetUInt16(string address, ushort value)
    {
        var w = _server.Write(address, value);
        Assert.True(w.IsSuccess, $"预置 UInt16 失败 {address}: {w.Message}");
    }

    /// <summary>在服务器端预置一个 Bool，供驱动 ReadBool 读回验证。</summary>
    protected void PresetBool(string address, bool value)
    {
        var w = _server.Write(address, value);
        Assert.True(w.IsSuccess, $"预置 Bool 失败 {address}: {w.Message}");
    }

    /// <summary>在服务器端预置连续多个 Int32，供驱动 ReadInt32Batch 读回验证。</summary>
    protected void PresetInt32Array(string address, int[] values)
    {
        var w = _server.Write(address, values);
        Assert.True(w.IsSuccess, $"预置 Int32[] 失败 {address}: {w.Message}");
    }

    /// <summary>
    /// 切断 TCP 代理，模拟网络断线。
    /// 停止代理后，驱动的自动重连会因连接被拒绝而失败，从而验证断线场景下的读取失败行为。
    /// 注意：HslCommunication 客户端在 <c>ConnectClose()</c> 后会自动重连，
    /// 因此不能用 driver.Disconnect() 测试"断线后读取失败"——必须通过切断网络实现。
    /// </summary>
    protected void CutConnection() => _relay.Stop();

    /// <summary>
    /// 分配一个当前空闲的 TCP 端口。
    /// 用于"服务器不可达"场景：分配端口但不绑定服务器，驱动连接该端口会被立即拒绝。
    /// </summary>
    protected static int AllocateFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    public virtual void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // 仅停止代理（切断所有客户端连接），不调用 _server.Dispose() 或 ServerClose()。
        // HslCommunication 的 NetworkServerBase.Dispose/ServerClose 会在 AsyncAcceptCallback 中
        // 抛出未处理异常（"重新异步接受传入的连接尝试"），导致 [FATAL ERROR] 噪声和非零退出码。
        // 服务器对象由 GC 终结化释放监听器；每个测试使用独立端口无冲突。
        try { _relay.Dispose(); } catch { /* 忽略代理关闭异常 */ }
    }
}

/// <summary>
/// 轻量 TCP 代理：监听公开端口，将数据双向转发到内部目标端口。
/// 用于 PLC 仿真测试的断线模拟：停止代理即可切断驱动与服务器之间的 TCP 连接，
/// 避免 HslCommunication ServerClose 在 AsyncAcceptCallback 中抛出未处理异常。
/// 设计参考 PlcSimulator 项目的 TcpRelay。
/// </summary>
internal sealed class SimulationTcpRelay : IDisposable
{
    private TcpListener? _listener;
    private readonly List<TcpClient> _clients = [];
    private readonly int _listenPort;
    private readonly int _targetPort;
    private CancellationTokenSource _cts = new();
    private Task? _acceptTask;

    public SimulationTcpRelay(int listenPort, int targetPort)
    {
        _listenPort = listenPort;
        _targetPort = targetPort;
    }

    public void Start()
    {
        _cts.Cancel();
        _cts.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _listener = new TcpListener(IPAddress.Loopback, _listenPort);
        _listener.Start();
        _acceptTask = AcceptLoopAsync(token);
    }

    public void Stop()
    {
        _cts.Cancel();
        _listener?.Stop();
        _listener = null;
        lock (_clients)
        {
            foreach (var c in _clients)
            {
                try { c.Close(); } catch { }
            }
            _clients.Clear();
        }
        try { _acceptTask?.Wait(1000); } catch { }
    }

    public void Dispose()
    {
        Stop();
        _cts.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient? client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch { break; }

            lock (_clients) _clients.Add(client);
            _ = RelayAsync(client, token);
        }
    }

    private async Task RelayAsync(TcpClient client, CancellationToken token)
    {
        TcpClient? server = null;
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            server = new TcpClient();
            await server.ConnectAsync(IPAddress.Loopback, _targetPort, token).ConfigureAwait(false);

            var clientStream = client.GetStream();
            var serverStream = server.GetStream();

            var c2s = PumpAsync(clientStream, serverStream, linkedCts.Token);
            var s2c = PumpAsync(serverStream, clientStream, linkedCts.Token);
            await Task.WhenAny(c2s, s2c).ConfigureAwait(false);
            linkedCts.Cancel();
            try { await Task.WhenAll(c2s, s2c).ConfigureAwait(false); } catch { }
        }
        catch { /* 连接断开属正常情况 */ }
        finally
        {
            linkedCts.Cancel();
            try { client.Close(); } catch { }
            try { server?.Close(); } catch { }
            lock (_clients) _clients.Remove(client);
        }
    }

    private static async Task PumpAsync(NetworkStream from, NetworkStream to, CancellationToken token)
    {
        var buffer = new byte[4096];
        while (!token.IsCancellationRequested)
        {
            var n = await from.ReadAsync(buffer, token).ConfigureAwait(false);
            if (n == 0) break;
            await to.WriteAsync(buffer.AsMemory(0, n), token).ConfigureAwait(false);
        }
    }
}
