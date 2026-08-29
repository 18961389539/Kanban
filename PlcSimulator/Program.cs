using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Kanban.Contracts.Dtos;
using HslCommunication;
using HslCommunication.Core;
using HslCommunication.Profinet.Melsec;

namespace PlcSimulator;

/// <summary>
/// 虚拟 PLC 工具：在内置 <see cref="MelsecMcServer"/> 上托管虚拟 PLC，
/// 并按 <c>devices.json</c> 的地址为每台设备运行独立状态机，模拟真实生产场景。
///
/// 用法：
///   PlcSimulator                                host 模式，监听 4999，自动开始模拟
///   PlcSimulator 4998                           host 模式，监听 4998
///   PlcSimulator --speed 10                     加速 10 倍（节拍缩短到 1/10）
///   PlcSimulator --scenario stress              压力测试场景（高频报警 + 高 NG 率）
///   PlcSimulator --scenario demo                演示场景（数据丰富便于截图）
///   PlcSimulator --scenario fault               故障演练场景（长报警 + 连环突发）
///   PlcSimulator --fresh                        全新初始化（清零所有 PLC 值，不恢复）
///   PlcSimulator --noauto                       不自动开始，等待 start 命令
///   PlcSimulator --client 127.0.0.1 4999        连接外部虚拟 PLC
///
/// 运行时命令：
///   start          所有设备启动（待机→运行）
///   stop           所有设备待机（停机次数 +1）
///   pause          同 stop
///   resume         所有待机设备恢复运行
///   alarm [设备名]  手动触发报警（不指定则触发所有设备）
///   reset          所有产量/缺陷/连续不良归零（停机次数保留）
///   read           读取并显示当前所有 PLC 地址的值
///   status         显示所有设备模拟状态
///   scenario       显示当前场景配置
///   help           显示帮助
///   quit           退出
/// </summary>
internal class Program
{
    private static MelsecMcServer? _server;
    private static MelsecMcNet? _client;
    private static IReadWriteNet _io = null!;
    private static readonly object _ioLock = new();

    private static List<DeviceConfig> _devices = new();
    private static List<DeviceSimulator> _simulators = new();
    private static CancellationTokenSource _cts = new();
    private static Task? _tickTask;
    private static Task? _resetWatcherTask;
    private static Task? _disconnectSimTask;
    private static TcpRelay? _relay;
    private static int _listenPort = 4999;
    /// <summary>默认加速倍率：10 倍（节拍缩短到 1/10），使仿真时产量增长明显。
    /// 可通过命令行 --speed N 覆盖（范围 0.1-100）。</summary>
    private static double _speedMultiplier = 10.0;
    private static ScenarioConfig _scenario = new();
    private static bool _freshInit = false;

    static async Task Main(string[] args)
    {
        // 单实例保护：防误启双 Simulator 抢占同一端口（残留实例与真实例双监听 4999/5000 时，
        // Collector 连接可能路由到残留实例，导致持续 ReadFailure——已多次踩坑）。
        // 与 Collector 的 Global\ 互斥同模式：Windows 服务（Session 0）与交互会话互斥生效；
        // WaitOne(0) 语义在前实例崩溃（abandoned）时正常接管启动。
        using var singleInstanceMutex = TryAcquireSingleton(@"Global\Kanban.PlcSimulator.SingleInstance");
        if (singleInstanceMutex is null)
        {
            Console.Error.WriteLine("PlcSimulator 已在运行（单实例保护），本实例退出。");
            return;
        }

        // 强制控制台输出为 UTF-8，避免中文日志重定向到文件时出现 GBK 乱码
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }

        // 初始化日志路径到配置目录，避免日志散落在不同工作目录
        SimLog.Initialize(Path.Combine(GetConfigDir(), "sim_log.txt"));

        // Ctrl+C / Ctrl+Break 处理：触发取消并走 exit 清理流程，避免资源泄漏
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;  // 阻止默认终止行为，由 exit 标签统一清理
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        };

        var clientMode = args.Contains("--client", StringComparer.OrdinalIgnoreCase);
        var noAuto = args.Contains("--noauto", StringComparer.OrdinalIgnoreCase);
        _freshInit = args.Contains("--fresh", StringComparer.OrdinalIgnoreCase);

        // 解析 --speed（InvariantCulture：避免 "2.5" 在逗号小数区域被解析失败）
        var speedIdx = Array.IndexOf(args, "--speed");
        if (speedIdx >= 0 && speedIdx + 1 < args.Length
            && double.TryParse(args[speedIdx + 1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var sp))
        {
            _speedMultiplier = Math.Clamp(sp, 0.1, 100);
        }
        else if (speedIdx >= 0)
        {
            Console.WriteLine("[警告] --speed 参数无效，已使用默认 x10（可用 0.1-100 之间的数字，如 --speed 2.5）");
        }

        // 解析 --scenario
        var scenarioIdx = Array.IndexOf(args, "--scenario");
        string? scenarioName = null;
        if (scenarioIdx >= 0 && scenarioIdx + 1 < args.Length)
            scenarioName = args[scenarioIdx + 1];
        // 修复（2026-08-16，审查 M3）：未知名场景显式警告（原静默回退 normal，联调时难排查）
        if (scenarioName != null && !ScenarioConfig.Presets.ContainsKey(scenarioName))
        {
            Console.WriteLine($"[警告] 未知名场景 \"{scenarioName}\"，已回退到 normal；可用场景: {string.Join(", ", ScenarioConfig.Presets.Keys)}");
        }
        _scenario = ScenarioConfig.Get(scenarioName);

        // 修复（2026-08-16，审查 M7）：启动时校验场景参数合法性（自定义场景兜底）
        foreach (var err in _scenario.Validate())
            Console.WriteLine($"[警告] 场景配置非法：{err}");

        // 位置参数 = 排除 -- 开头的参数和带值的 --speed/--scenario 的值
        var skipNext = false;
        var listenAll = false;  // 审查修复 2026-08-15：默认只绑 127.0.0.1，需局域网访问时显式 --listen-all
        var positional = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (skipNext) { skipNext = false; continue; }
            if (args[i] == "--speed" || args[i] == "--scenario") { skipNext = true; continue; }
            if (args[i] == "--listen-all") { listenAll = true; continue; }
            if (args[i].StartsWith("--")) continue;
            positional.Add(args[i]);
        }

        int port = 4999;
        string ip = "127.0.0.1";
        if (clientMode)
        {
            if (positional.Count > 0) ip = positional[0];
            if (positional.Count > 1 && int.TryParse(positional[1], out var p)) port = p;
        }
        else if (positional.Count > 0 && int.TryParse(positional[0], out var p2))
        {
            port = p2;
        }

        // 修复（2026-08-16，审查 L1）：host 模式内部端口 = port+1，port=65535 会溢出为非法端口。
        if (port < 1 || port > 65534)
        {
            Console.WriteLine($"[错误] 端口 {port} 非法（须在 1-65534 之间，因内部端口 = 端口+1）");
            WaitExit();
            return;
        }

        Console.Title = $"PLC 模拟器 - {(clientMode ? $"client {ip}:{port}" : $"host :{port}")}";
        Console.WriteLine("═══════════════════════════════════════════");
        Console.WriteLine($"  PLC 模拟数据工具 ({(clientMode ? "客户端模式" : "托管模式")})");
        Console.WriteLine($"  速度 x{_speedMultiplier:F1}  场景: {_scenario.Name}  初始化: {(_freshInit ? "全新" : "恢复")}");
        Console.WriteLine("═══════════════════════════════════════════");
        Console.WriteLine();

        // 加载设备配置（文件损坏或被占用时回退到默认配置）
        var configDir = GetConfigDir();
        var devicesFile = Path.Combine(configDir, "devices.json");
        var jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
        if (File.Exists(devicesFile))
        {
            Console.WriteLine($"从 {devicesFile} 加载设备配置...");
            try
            {
                var json = await File.ReadAllTextAsync(devicesFile);
                _devices = JsonSerializer.Deserialize<List<DeviceConfig>>(json, jsonOpts) ?? new();
                Console.WriteLine($"已加载 {_devices.Count} 台设备配置");
            }
            catch (Exception ex)
            {
                SimLog.Error($"加载 {devicesFile} 失败：{ex.Message}，回退到默认设备配置");
                _devices = CreateDefaultDevices();
            }
        }
        else
        {
            Console.WriteLine($"未找到 {devicesFile}，使用内置默认设备配置");
            _devices = CreateDefaultDevices();
            try
            {
                Directory.CreateDirectory(configDir);
                await File.WriteAllTextAsync(devicesFile, JsonSerializer.Serialize(_devices, jsonOpts));
                Console.WriteLine($"默认配置已写入 {devicesFile}");
            }
            catch (Exception ex)
            {
                SimLog.Warning($"写入默认配置失败：{ex.Message}");
            }
        }

        if (_devices.Count == 0)
        {
            Console.WriteLine("无设备配置，退出。");
            return;
        }

        // 校验设备地址冲突：同一地址被多台设备使用会导致数据相互覆盖
        var conflicts = DetectAddressConflicts(_devices);
        foreach (var c in conflicts)
            Console.WriteLine($"  [警告] 地址冲突：{c}");

        PrintDevices();
        PrintScenario();

        // 连接/启动 PLC
        Console.WriteLine();
        if (clientMode)
        {
            Console.WriteLine($"连接外部虚拟 PLC {ip}:{port} ...");
            _client = new MelsecMcNet(ip, port);
            var connectResult = _client.ConnectServer();
            if (!connectResult.IsSuccess)
            {
                SimLog.Error($"[错误] 连接失败：{connectResult.Message}");
                WaitExit();
                return;
            }
            _io = _client;
            Console.WriteLine("[成功] 已连接外部虚拟 PLC");
        }
        else
        {
            // 断线仿真方案：PLC 服务器运行在内部端口（port+1），TCP 代理监听公开端口（port）。
            // 断线时只需关闭/重启代理（TcpRelay），无需调用 HslCommunication.ServerClose()
            // （后者在 AsyncAcceptCallback 中抛出未处理异常，会导致进程崩溃）。
            var internalPort = port + 1;
            Console.WriteLine($"启动托管虚拟 PLC 服务器（内部端口 {internalPort}，代理端口 {port}）...");
            _server = new MelsecMcServer();
            _listenPort = port;
            try { _server.ServerStart(internalPort); }
            catch (Exception ex)
            {
                SimLog.Error($"[错误] 服务器启动异常：{ex.Message}");
                WaitExit();
                return;
            }
            if (!_server.IsStarted)
            {
                Console.WriteLine("[错误] 服务器启动失败（端口可能被占用）");
                WaitExit();
                return;
            }
            _io = _server;
            // 启动 TCP 代理：监听公开端口，转发到内部 PLC 端口。
            // 默认绑定 127.0.0.1（局域网内任意主机可读写虚拟 PLC 内存属高危，审查修复 2026-08-15）；
            // 需要跨主机访问时显式传 --listen-all。
            _relay = new TcpRelay(port, internalPort, listenAll ? IPAddress.Any : IPAddress.Loopback);
            _relay.Start();
            Console.WriteLine($"[成功] 托管虚拟 PLC 已启动（内部端口 {internalPort}）");
            Console.WriteLine($"       TCP 代理监听 {(listenAll ? "0.0.0.0" : "127.0.0.1")}:{port} → 127.0.0.1:{internalPort}");
            Console.WriteLine("       MainAPP 可连接到 127.0.0.1:" + port);
            // 修复（2026-08-16，审查 H1）：--listen-all 时醒目警告，三菱 MC 协议无鉴权，局域网可任意读写虚拟 PLC。
            if (listenAll)
            {
                Console.WriteLine("  ⚠ 警告：--listen-all 已启用，虚拟 PLC 内存以无鉴权方式暴露到局域网，");
                Console.WriteLine("    任何可访问该主机的设备均可读写全部 PLC 地址（篡改产量/触发报警）。仅限可信网络使用。");
            }
        }

        // 创建每设备的模拟器实例（注入场景配置 + 读写回调）
        _simulators = _devices.Select((d, idx) => new DeviceSimulator(
            d, _scenario, _speedMultiplier,
            WriteIntCallback, WriteBoolCallback,
            ReadInt, ReadBool,
            writeFloat: WriteFloatCallback,
            writeString: WriteStringCallback,
            registerOffset: idx * 50)).ToList();
        foreach (var sim in _simulators)
            sim.Log += msg => SimLog.Info(msg);

        // 初始化 PLC 内存（--fresh 强制清零，否则从 PLC 读取现有值恢复）
        Console.WriteLine();
        foreach (var sim in _simulators)
            sim.Initialize(restoreFromPlc: !_freshInit);
        Console.WriteLine(_freshInit ? "已全新初始化（所有设备待机）" : "已从 PLC 恢复状态");

        // 启动 Tick 循环 + 清零监听
        StartTickLoop();

        // 自动开始（恢复模式下，如果设备已是运行态，Tick 会自动产出，无需再调 Start）
        if (!noAuto)
        {
            var now = DateTime.UtcNow;
            var startedCount = 0;
            foreach (var sim in _simulators)
            {
                // 仅待机态才自动启动；运行态（恢复）保持原状
                if (sim.Status == DeviceSimulator.SimStatus.Idle)
                {
                    sim.Start(now);
                    startedCount++;
                }
            }
            if (startedCount > 0)
                Console.WriteLine($"已自动启动 {startedCount} 台待机设备");
            else
                Console.WriteLine("无待机设备需启动（均为运行态）");
        }

        // 非交互模式
        if (Console.IsInputRedirected)
        {
            Console.WriteLine("[非交互模式] 模拟运行中，按 Ctrl+C 停止。");
            try { await Task.Delay(Timeout.Infinite, _cts.Token); }
            catch (OperationCanceledException) { }
            goto exit;
        }

        // 交互模式：后台线程的日志会打断命令提示符，注册回调在日志输出后重绘 "> "
        SimLog.OnConsoleWrite = () => Console.Write("> ");

        // 命令循环（Ctrl+C 会触发 _cts.Cancel，ReadLine 可返回 null 退出）
        while (!_cts.IsCancellationRequested)
        {
            Console.Write("> ");
            var cmd = Console.ReadLine();
            if (cmd == null) goto exit;
            cmd = cmd.Trim();
            if (string.IsNullOrEmpty(cmd)) continue;

            try
            {
                var parts = cmd.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                var action = parts[0].ToLowerInvariant();
                var arg = parts.Length > 1 ? parts[1].Trim() : null;

                switch (action)
                {
                    case "start":
                        foreach (var sim in _simulators) sim.Start(DateTime.UtcNow);
                        break;
                    case "stop":
                        foreach (var sim in _simulators) sim.Pause(DateTime.UtcNow);
                        Console.WriteLine("所有设备已待机（输入 resume 恢复）");
                        break;
                    case "pause":
                        foreach (var sim in _simulators) sim.Pause(DateTime.UtcNow);
                        break;
                    case "resume":
                        foreach (var sim in _simulators) sim.Resume(DateTime.UtcNow);
                        break;
                    case "alarm":
                        if (string.IsNullOrEmpty(arg))
                        {
                            foreach (var sim in _simulators) sim.TriggerAlarm(DateTime.UtcNow);
                        }
                        else
                        {
                            // 精确匹配优先（Name 或 Id），模糊匹配次之；多匹配时全部触发并提示
                            var targets = _simulators
                                .Where(s => s.Name == arg || s.Config.Id == arg)
                                .ToList();
                            if (targets.Count == 0)
                                targets = _simulators.Where(s => s.Name.Contains(arg)).ToList();
                            if (targets.Count > 0)
                            {
                                foreach (var t in targets) t.TriggerAlarm(DateTime.UtcNow);
                                if (targets.Count > 1)
                                    Console.WriteLine($"  匹配到 {targets.Count} 台设备：{string.Join(", ", targets.Select(t => t.Name))}");
                            }
                            else Console.WriteLine($"未找到设备: {arg}");
                        }
                        break;
                    case "reset":
                        foreach (var sim in _simulators) sim.ResetCounts();
                        break;
                    case "read":
                        ReadAllValues();
                        break;
                    case "status":
                        foreach (var sim in _simulators)
                            Console.WriteLine($"  {sim.GetStatusText()}");
                        break;
                    case "scenario":
                        PrintScenario();
                        break;
                    case "clear" or "cls":
                        Console.Clear();
                        break;
                    case "reload":
                        ReloadScenario(arg);
                        break;
                    case "quit" or "exit":
                        goto exit;
                    case "help":
                        PrintHelp();
                        break;
                    default:
                        Console.WriteLine($"未知命令: {action}（输入 help 查看帮助）");
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[异常] {ex.Message}");
            }
        }

    exit:
        await StopTickLoopAsync();
        try { _relay?.Dispose(); } catch { }
        try { _client?.ConnectClose(); } catch { }
        try { _server?.ServerClose(); } catch { }
        _cts.Dispose();
        Console.WriteLine("已退出。");
    }

    // ──────────── Tick 循环 + 清零监听 ────────────

    private static void StartTickLoop()
    {
        // 修复（2026-08-16，审查 M6）：创建新 CTS 前释放旧实例，避免字段初始化的 CTS 被替换后句柄泄漏。
        _cts.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _tickTask = Task.Run(() => TickLoopAsync(token));
        _resetWatcherTask = Task.Run(() => ResetWatcherLoopAsync(token));
        // 断线仿真：仅 host 模式且场景配置了 DisconnectIntervalSec > 0 时启动
        if (_server != null && _scenario.DisconnectIntervalSec > 0)
        {
            _disconnectSimTask = Task.Run(() => DisconnectSimLoopAsync(token,
                _scenario.DisconnectIntervalSec, _scenario.DisconnectDurationSec));
            Console.WriteLine($"  断线仿真已启动：每 {_scenario.DisconnectIntervalSec}s 断线 {_scenario.DisconnectDurationSec}s");
        }
    }

    /// <summary>停止所有后台循环，最多等待 3 秒。</summary>
    private static async Task StopTickLoopAsync()
    {
        _cts.Cancel();
        // 用 WaitAsync 限定退出时间，避免单个循环卡死阻塞关闭流程
        if (_tickTask != null) try { await _tickTask.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        if (_resetWatcherTask != null) try { await _resetWatcherTask.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        if (_disconnectSimTask != null) try { await _disconnectSimTask.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
    }

    /// <summary>
    /// 断线仿真循环：每隔 intervalSec 秒关闭 TCP 代理，持续 durationSec 秒后恢复。
    /// 模拟网络中断/PLC 重启场景，验证 MainAPP 的断线检测、重连冷却、DeviceStatusTracker 离线转换。
    /// 通过关闭/重启 TcpRelay 实现，不调用 HslCommunication.ServerClose()（后者在 AsyncAcceptCallback
    /// 中抛出未处理异常 "重新异步接受传入的连接尝试"，导致进程崩溃）。PLC 服务器始终在内部端口运行，
    /// 仿真器状态机不受影响（模拟真实 PLC 断网后仍继续生产，只是 MainAPP 无法读取数据）。
    /// </summary>
    private static async Task DisconnectSimLoopAsync(CancellationToken token, int intervalSec, int durationSec)
    {
        // 首次延迟 intervalSec 秒再开始断线（避免启动即断线）
        try { await Task.Delay(intervalSec * 1000, token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!token.IsCancellationRequested)
        {
            SimLog.Info($"[断线仿真] 关闭 TCP 监听（持续 {durationSec}s）...");
            // 关闭代理：断开所有 MainAPP 连接，模拟网络中断
            try { _relay?.Stop(); }
            catch (Exception ex) { SimLog.Warning($"[断线仿真] 代理关闭异常: {ex.Message}"); }

            try { await Task.Delay(durationSec * 1000, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            SimLog.Info("[断线仿真] 恢复 TCP 监听");
            // 重启代理：MainAPP 可重新连接
            try { _relay?.Start(); }
            catch (Exception ex) { SimLog.Warning($"[断线仿真] 代理启动异常: {ex.Message}"); }

            try { await Task.Delay(intervalSec * 1000, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// 主 Tick 循环：每 100ms 调用所有设备的 Tick，驱动节拍产出、报警恢复、突发不良期、阈值检查。
    /// </summary>
    private static async Task TickLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            // 修复（2026-08-16，审查 R2）：per-device 隔离。原 foreach 无 try/catch，
            // 任一设备抛异常会静默终止整个 tick 循环，所有设备同时"冻产"且无告警。
            foreach (var sim in _simulators)
            {
                try { sim.Tick(now); }
                catch (Exception ex)
                {
                    SimLog.Error($"[{sim.Name}] Tick 异常：{ex.Message}（该设备本轮跳过，其余设备继续）");
                }
            }

            try { await Task.Delay(100, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// 清零监听循环：每 500ms 轮询所有设备的 ProductionResetAddress，
    /// 读到非 0 值时归零该设备的所有产量/缺陷/连续不良计数，并写回 0 清除触发位。
    /// 模拟真实 PLC 中清零触发位的握手协议。
    /// 注意：MainAPP 通过 WriteInt32(addr, 1) 写入触发位，此处必须用 ReadInt 读取
    /// （ReadBool 对 D 字地址不可靠：HslCommunication 的 D 寄存器为 16 位字级访问，
    /// ReadBool 读取的是字内位，与 WriteInt32 写入的 32 位整数语义不一致）。
    /// </summary>
    private static async Task ResetWatcherLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                foreach (var sim in _simulators)
                {
                    var addr = sim.Config.ProductionResetAddress;
                    if (string.IsNullOrEmpty(addr)) continue;

                    // 用 ReadInt 读取（与 MainAPP 的 WriteInt32 对应）
                    var triggerVal = ReadInt(addr);
                    if (triggerVal != 0)
                    {
                        SimLog.Info($"[{sim.Name}] 检测到清零触发位={triggerVal}，归零计数...");
                        sim.ResetCounts();
                        // 清除触发位（与 MainAPP 的 WriteInt32 对应，写 0 清除）
                        WriteIntCallback(addr, 0);
                        SimLog.Info($"[{sim.Name}] 清零完成，触发位已清除");
                    }
                }
            }
            catch (Exception ex)
            {
                SimLog.Error($"[清零监听异常] {ex.Message}");
            }

            try { await Task.Delay(500, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    // ──────────── PLC 读写封装 ────────────

    private static void WriteIntCallback(string address, int value)
    {
        lock (_ioLock)
        {
            var r = _io.Write(address, value);
            if (!r.IsSuccess)
                SimLog.Error($"[写入失败] {address}={value}: {r.Message}");
        }
    }

    private static void WriteBoolCallback(string address, bool value)
    {
        lock (_ioLock)
        {
            var r = _io.Write(address, value);
            if (!r.IsSuccess)
                SimLog.Error($"[写入失败] {address}={value}: {r.Message}");
        }
    }

    private static void WriteFloatCallback(string address, float value)
    {
        lock (_ioLock)
        {
            var r = _io.Write(address, value);
            if (!r.IsSuccess)
                SimLog.Error($"[写入失败] {address}={value}: {r.Message}");
        }
    }

    private static void WriteStringCallback(string address, string value)
    {
        lock (_ioLock)
        {
            var r = _io.Write(address, value);
            if (!r.IsSuccess)
                SimLog.Error($"[写入失败] {address}={value}: {r.Message}");
        }
    }

    /// <summary>
    /// 读取 PLC 整数。失败时抛异常（审查修复 2026-08-16，H3）：与「真实值 0」区分，
    /// 供 DeviceSimulator 的 TryReadIntNullable 与清零监听识别读取失败，避免把失败误当 0。
    /// </summary>
    private static int ReadInt(string address)
    {
        lock (_ioLock)
        {
            var r = _io.ReadInt32(address);
            if (!r.IsSuccess)
                throw new IOException($"读取 {address} 失败：{r.Message}");
            return r.Content;
        }
    }

    private static bool ReadBool(string address)
    {
        lock (_ioLock)
        {
            var r = _io.ReadBool(address);
            if (!r.IsSuccess)
                throw new IOException($"读取 {address} 失败：{r.Message}");
            return r.Content;
        }
    }

    /// <summary>读取 PLC 整数（供 read 显示命令用），失败返回 0，避免单次读失败打断整屏输出。</summary>
    private static int ReadIntSafe(string address)
    {
        try { return ReadInt(address); }
        catch { return 0; }
    }

    /// <summary>读取 PLC 布尔（供 read 显示命令用），失败返回 false。</summary>
    private static bool ReadBoolSafe(string address)
    {
        try { return ReadBool(address); }
        catch { return false; }
    }

    // ──────────── 显示 ────────────

    private static void ReadAllValues()
    {
        Console.WriteLine("─── 当前 PLC 值 ───");
        foreach (var dev in _devices)
        {
            Console.WriteLine($"[{dev.Name}]");

            // 修复（2026-08-16，审查 M8）：每地址只读一次并缓存，避免 client 模式同一地址重复读
            // 导致的 3 倍网络往返，以及多次读到的值不一致（模拟器在跑导致计数跳变）。
            var intCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var boolCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            int ReadOnce(string? addr)
            {
                if (string.IsNullOrEmpty(addr)) return 0;
                if (!intCache.TryGetValue(addr, out var v))
                {
                    v = ReadIntSafe(addr);
                    intCache[addr] = v;
                }
                return v;
            }

            bool ReadBoolOnce(string? addr)
            {
                if (string.IsNullOrEmpty(addr)) return false;
                if (!boolCache.TryGetValue(addr, out var v))
                {
                    v = ReadBoolSafe(addr);
                    boolCache[addr] = v;
                }
                return v;
            }

            if (!string.IsNullOrEmpty(dev.OkCountAddress))
                Console.WriteLine($"  OK产量 ({dev.OkCountAddress}): {ReadOnce(dev.OkCountAddress)}");
            if (!string.IsNullOrEmpty(dev.NgCountAddress))
                Console.WriteLine($"  NG产量 ({dev.NgCountAddress}): {ReadOnce(dev.NgCountAddress)}");
            if (!string.IsNullOrEmpty(dev.StatusCountAddress))
            {
                var s = ReadOnce(dev.StatusCountAddress);
                Console.WriteLine($"  状态 ({dev.StatusCountAddress}): {s} ({StatusName(s)})");
            }
            if (!string.IsNullOrEmpty(dev.ProductionResetAddress))
                Console.WriteLine($"  清零触发位 ({dev.ProductionResetAddress}): {ReadOnce(dev.ProductionResetAddress)}");
            if (!string.IsNullOrEmpty(dev.RecipeAddress))
                Console.WriteLine($"  配方 ({dev.RecipeAddress}): {ReadOnce(dev.RecipeAddress)}");

            // 报警位：数量多时只显示 ON 的 + 总数摘要（避免 200 行输出）
            var activeAlarms = dev.Alarms.Where(a => !string.IsNullOrEmpty(a.PlcAddress) && ReadBoolOnce(a.PlcAddress)).ToList();
            if (dev.Alarms.Count > 10)
            {
                Console.WriteLine($"  报警: {activeAlarms.Count}/{dev.Alarms.Count} 个 ON"
                    + (activeAlarms.Count > 0 ? $" → {string.Join(", ", activeAlarms.Select(a => $"{a.Name}@{a.PlcAddress}"))}" : ""));
            }
            else
            {
                foreach (var alarm in dev.Alarms)
                    if (!string.IsNullOrEmpty(alarm.PlcAddress))
                        Console.WriteLine($"  报警[{alarm.Name}] ({alarm.PlcAddress}): {ReadBoolOnce(alarm.PlcAddress)}");
            }

            // 缺陷：数量多时只显示非零的 + 总数摘要
            var nonZeroDefects = dev.Defects.Where(d => !string.IsNullOrEmpty(d.PlcAddress) && ReadOnce(d.PlcAddress) > 0).ToList();
            if (dev.Defects.Count > 10)
            {
                var totalDefects = nonZeroDefects.Sum(d => ReadOnce(d.PlcAddress));
                Console.WriteLine($"  缺陷: {nonZeroDefects.Count}/{dev.Defects.Count} 种有计数, 合计={totalDefects}"
                    + (nonZeroDefects.Count > 0 ? $" → {string.Join(", ", nonZeroDefects.Select(d => $"{d.Name}={ReadOnce(d.PlcAddress)}"))}" : ""));
            }
            else
            {
                foreach (var defect in dev.Defects)
                    if (!string.IsNullOrEmpty(defect.PlcAddress))
                        Console.WriteLine($"  缺陷[{defect.Name}] ({defect.PlcAddress}): {ReadOnce(defect.PlcAddress)}");
            }

            // 计数报警：数量多时只显示非零的 + 总数摘要
            var nonZeroCounterAlarms = dev.CounterAlarms.Where(ca => !string.IsNullOrEmpty(ca.PlcAddress) && ReadOnce(ca.PlcAddress) > 0).ToList();
            if (dev.CounterAlarms.Count > 10)
            {
                Console.WriteLine($"  计数报警: {nonZeroCounterAlarms.Count}/{dev.CounterAlarms.Count} 个非零"
                    + (nonZeroCounterAlarms.Count > 0 ? $" → {string.Join(", ", nonZeroCounterAlarms.Select(ca => $"{ca.Name}={ReadOnce(ca.PlcAddress)}/{ca.MaxValue}"))}" : ""));
            }
            else
            {
                foreach (var ca in dev.CounterAlarms)
                    if (!string.IsNullOrEmpty(ca.PlcAddress))
                        Console.WriteLine($"  计数报警[{ca.Name}] ({ca.PlcAddress}): {ReadOnce(ca.PlcAddress)} (阈值{ca.MaxValue})");
            }
        }
    }

    // ──────────── 辅助 ────────────

    private static string GetConfigDir()
    {
        var dataRoot = Environment.GetEnvironmentVariable("KANBAN_DATA_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kanban");
        return Path.Combine(dataRoot, "Config");
    }

    /// <summary>状态字显示名。与 DeviceSimulator.SimStatus 及 MainAPP DeviceStatus 对齐：3=待机（Paused），0=离线。</summary>
    private static string StatusName(int status) => status switch
    {
        0 => "离线",
        1 => "运行",
        2 => "报警",
        3 => "待机",
        _ => $"未知({status})",
    };

    private static void PrintDevices()
    {
        Console.WriteLine();
        Console.WriteLine("─── 设备配置 ───");
        foreach (var dev in _devices)
        {
            var cycleSec = dev.RecipeValue > 0 ? 3600.0 / dev.RecipeValue : 60.0;
            var simCycleSec = cycleSec / _speedMultiplier;
            Console.WriteLine($"[{dev.Name}] (Id: {dev.Id}, 节拍 {cycleSec:F1}s/件 → 仿真 {simCycleSec:F1}s/件 @x{_speedMultiplier:F1})");
            Console.WriteLine($"  OK: {dev.OkCountAddress}, NG: {dev.NgCountAddress}, 状态: {dev.StatusCountAddress}");
            Console.WriteLine($"  清零: {dev.ProductionResetAddress}, 配方: {dev.RecipeAddress}={dev.RecipeValue}");

            // 数量多时只显示摘要（地址范围 + 总数），避免 200+50+100 行输出
            if (dev.Alarms.Count > 10)
                Console.WriteLine($"  报警: {dev.Alarms.Count} 个 ({dev.Alarms.First().PlcAddress}..{dev.Alarms.Last().PlcAddress})");
            else if (dev.Alarms.Count > 0)
                Console.WriteLine($"  报警: {string.Join(", ", dev.Alarms.Select(a => $"{a.Name}@{a.PlcAddress}"))}");

            if (dev.Defects.Count > 10)
                Console.WriteLine($"  缺陷: {dev.Defects.Count} 个 ({dev.Defects.First().PlcAddress}..{dev.Defects.Last().PlcAddress})");
            else if (dev.Defects.Count > 0)
                Console.WriteLine($"  缺陷: {string.Join(", ", dev.Defects.Select(d => $"{d.Name}@{d.PlcAddress}"))}");

            if (dev.CounterAlarms.Count > 10)
                Console.WriteLine($"  计数报警: {dev.CounterAlarms.Count} 个 ({dev.CounterAlarms.First().PlcAddress}..{dev.CounterAlarms.Last().PlcAddress}, 阈值={dev.CounterAlarms.First().MaxValue})");
            else if (dev.CounterAlarms.Count > 0)
                Console.WriteLine($"  计数报警: {string.Join(", ", dev.CounterAlarms.Select(c => $"{c.Name}@{c.PlcAddress}(≤{c.MaxValue})"))}");
        }
    }

    /// <summary>
    /// 检测设备地址冲突：扫描所有设备的所有 PLC 地址，发现被多台设备使用的地址。
    /// 冲突会导致两台设备相互覆盖数据，行为诡异。
    /// 返回冲突描述列表（空列表表示无冲突）。internal 以支持 PlcSimulator.Tests 单测（审查 2026-08-16）。
    /// </summary>
    internal static List<string> DetectAddressConflicts(List<DeviceConfig> devices)
    {
        var usage = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var deviceIndex = 0;
        foreach (var dev in devices)
        {
            var deviceKey = $"{deviceIndex++}:{dev.Id}";
            void AddIfNotEmpty(string addr, string owner)
            {
                if (string.IsNullOrWhiteSpace(addr)) return;
                if (!usage.TryGetValue(addr, out var owners))
                {
                    owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    usage[addr] = owners;
                }
                owners.TryAdd(deviceKey, $"{dev.Name}({owner})");
            }

            AddIfNotEmpty(dev.OkCountAddress, "OK");
            AddIfNotEmpty(dev.NgCountAddress, "NG");
            AddIfNotEmpty(dev.StatusCountAddress, "状态");
            AddIfNotEmpty(dev.ProductionResetAddress, "清零");
            AddIfNotEmpty(dev.RecipeAddress, "配方");
            foreach (var a in dev.Alarms) AddIfNotEmpty(a.PlcAddress, $"报警:{a.Name}");
            foreach (var d in dev.Defects) AddIfNotEmpty(d.PlcAddress, $"缺陷:{d.Name}");
            foreach (var c in dev.CounterAlarms) AddIfNotEmpty(c.PlcAddress, $"计数报警:{c.Name}");
            foreach (var source in dev.Sources ?? [])
            {
                AddIfNotEmpty(source.TriggerAddress, $"数据源触发:{source.Name}");
                foreach (var value in source.Values ?? [])
                    AddIfNotEmpty(value.PlcAddress, $"数据源值:{source.Name}/{value.Name}");
            }
        }

        var conflicts = new List<string>();
        foreach (var (addr, owners) in usage)
        {
            if (owners.Count > 1)
                conflicts.Add($"地址 {addr} 被 {owners.Count} 台设备共用：{string.Join(", ", owners.Values)}");
        }
        return conflicts;
    }

    private static void PrintScenario()
    {
        Console.WriteLine();
        Console.WriteLine($"─── 场景配置 [{_scenario.Name}] ───");
        Console.WriteLine($"  NG率: 正常 {_scenario.NgRateBase * 100:F0}-{(_scenario.NgRateBase + _scenario.NgRateJitter) * 100:F0}%"
            + $" | 报警 {_scenario.NgRateAlarmBase * 100:F0}-{(_scenario.NgRateAlarmBase + _scenario.NgRateAlarmJitter) * 100:F0}%"
            + $" | 突发 {_scenario.BurstNgRate * 100:F0}%");
        Console.WriteLine($"  随机报警: 每 tick {_scenario.AlarmChancePerTick * 100:F3}% (持续 {_scenario.AlarmMinSec}-{_scenario.AlarmMaxSec}s)");
        Console.WriteLine($"  突发不良: 每 tick {_scenario.BurstChancePerTick * 100:F4}% (持续 {_scenario.BurstMinSec}-{_scenario.BurstMaxSec}s)");
        Console.WriteLine($"  节拍漂移: 最大 +{_scenario.CycleDriftMax * 100:F0}% (+{_scenario.CycleDriftStep * 100:F1}%/件, 报警恢复回收 {_scenario.DriftRecoverOnAlarm * 100:F0}%)");
        Console.WriteLine($"  班次曲线: {(_scenario.EnableShiftCurve ? "启用" : "禁用")}");
        Console.WriteLine($"  开机预热: {(_scenario.EnableWarmup ? $"启用 (前{_scenario.WarmupPieces}件 NG率={_scenario.WarmupNgRate * 100:F0}% 节拍x{_scenario.WarmupCycleFactor:F1})" : "禁用")}");
        Console.WriteLine($"  缺料停机: {(_scenario.EnableMaterialShortage ? $"启用 (每tick {_scenario.ShortageChancePerTick * 100:F4}%, 持续 {_scenario.ShortageMinSec}-{_scenario.ShortageMaxSec}s)" : "禁用")}");
        Console.WriteLine($"  阈值触发: {(_scenario.EnableCounterAlarmThreshold ? "启用" : "禁用")} (报警持续 {_scenario.ThresholdAlarmSec}s)");
        // 新特性
        Console.WriteLine($"  物料批次: {(_scenario.EnableMaterialBatchVariance ? $"启用 (每{_scenario.MaterialBatchMinSec/60}-{_scenario.MaterialBatchMaxSec/60}分钟切换, NG率{_scenario.MaterialBatchNgRateMin*100:F1}-{_scenario.MaterialBatchNgRateMax*100:F1}%)" : "禁用")}");
        Console.WriteLine($"  设备老化: {(_scenario.EnableEquipmentAging ? $"启用 (阈值{_scenario.AgingThresholdHours}h, NG+{_scenario.AgingNgRatePenalty*100:F0}%, 漂移x{_scenario.AgingDriftMultiplier:F1})" : "禁用")}");
        Console.WriteLine($"  工单赶工: {(_scenario.EnableProductionPressure ? $"启用 (目标{_scenario.PressureTargetPieces}件, 达{_scenario.PressureThresholdPercent*100:F0}%时节拍x{_scenario.PressureCycleFactor:F2} NG+{_scenario.PressureNgRatePenalty*100:F0}%)" : "禁用")}");
        Console.WriteLine($"  首件检验: {(_scenario.EnableFirstArticleInspection ? $"启用 (换模/冷启动后待机{_scenario.FirstArticleInspectionSec}s)" : "禁用")}");
        Console.WriteLine($"  报警爬坡: {(_scenario.EnablePostAlarmRampup ? $"启用 (前{_scenario.PostAlarmRampupPieces}件 节拍x{_scenario.PostAlarmRampupCycleFactor:F2} NG+{_scenario.PostAlarmRampupNgRatePenalty*100:F0}%)" : "禁用")}");
        Console.WriteLine($"  深夜疲劳: {(_scenario.EnableDeepNightFatigue ? $"启用 ({_scenario.DeepNightStartHour}-{_scenario.DeepNightEndHour}点 NG+{_scenario.DeepNightNgRatePenalty*100:F0}% 节拍x{_scenario.DeepNightCycleFactor:F2})" : "禁用")}");
        Console.WriteLine($"  通信抖动: {(_scenario.EnableCommJitter ? $"启用 (每tick {_scenario.CommJitterChancePerTick*100:F4}%, 持续{_scenario.CommJitterDurationSec}s)" : "禁用")}");
    }

    /// <summary>
    /// 运行时切换场景：停止后台循环 → 重建模拟器（注入新场景）→ 从 PLC 恢复状态 → 重启循环。
    /// 保留 PLC 中已有产量/状态（MainAPP 数据不丢失），仅替换场景参数。
    /// </summary>
    private static void ReloadScenario(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            Console.WriteLine($"当前场景: {_scenario.Name}");
            Console.WriteLine("可用场景: normal | stress | demo | fault | counteralarm | disconnect");
            Console.WriteLine("用法: reload <场景名>");
            return;
        }

        // 修复（2026-08-16，审查 M3）：reload 未知名场景显式警告（原静默回退 normal）
        if (!ScenarioConfig.Presets.ContainsKey(name))
        {
            Console.WriteLine($"[警告] 未知名场景 \"{name}\"，将回退到 normal；可用场景: {string.Join(", ", ScenarioConfig.Presets.Keys)}");
        }
        var newScenario = ScenarioConfig.Get(name);
        Console.WriteLine($"场景切换: {_scenario.Name} → {newScenario.Name}");

        // 停止后台循环（旧 CTS 由 StartTickLoop 内部统一释放）
        StopTickLoopAsync().GetAwaiter().GetResult();

        // 修复（2026-08-16，审查 H5）：StopTickLoopAsync 最多等 3s，若旧循环仍未退出
        // （如卡在 IO 锁上），直接重建会导致双 tick 循环并发驱动同一批设备（产量翻倍/节拍错乱）。
        // 此处校验旧循环确已结束，未结束则放弃本次 reload，避免并发状态污染。
        if ((_tickTask != null && !_tickTask.IsCompleted)
            || (_resetWatcherTask != null && !_resetWatcherTask.IsCompleted)
            || (_disconnectSimTask != null && !_disconnectSimTask.IsCompleted))
        {
            Console.WriteLine("[警告] 旧后台循环尚未完全退出，已取消本次场景切换（请稍后重试）");
            return;
        }

        _scenario = newScenario;

        // 重建模拟器（注入新场景），从 PLC 恢复以保留 MainAPP 已有数据
        _simulators = _devices.Select((d, idx) => new DeviceSimulator(
            d, _scenario, _speedMultiplier,
            WriteIntCallback, WriteBoolCallback,
            ReadInt, ReadBool,
            writeFloat: WriteFloatCallback,
            writeString: WriteStringCallback,
            registerOffset: idx * 50)).ToList();
        foreach (var sim in _simulators)
        {
            sim.Log += msg => SimLog.Info(msg);
            sim.Initialize(restoreFromPlc: true);
        }

        StartTickLoop();
        Console.WriteLine($"已重载场景 [{_scenario.Name}]，已从 PLC 恢复 {_simulators.Count} 台设备状态");
    }

    private static void PrintHelp()
    {
        Console.WriteLine("可用命令：");
        Console.WriteLine("  start          所有设备启动（待机→运行）");
        Console.WriteLine("  stop/pause     所有设备待机（停机次数 +1）");
        Console.WriteLine("  resume         所有待机设备恢复运行");
        Console.WriteLine("  alarm [设备名]  手动触发报警（不指定则触发所有设备）");
        Console.WriteLine("  reset          所有产量/缺陷/连续不良归零（停机次数保留）");
        Console.WriteLine("  read           读取并显示当前所有 PLC 地址的值");
        Console.WriteLine("  status         显示所有设备模拟状态");
        Console.WriteLine("  scenario       显示当前场景配置");
        Console.WriteLine("  reload [场景名] 切换场景（不指定则列出可选场景），保留 PLC 已有数据");
        Console.WriteLine("  clear/cls      清空控制台屏幕");
        Console.WriteLine("  help           显示此帮助");
        Console.WriteLine("  quit/exit      退出");
        Console.WriteLine();
        Console.WriteLine("启动参数：");
        Console.WriteLine("  --speed N           节拍加速倍率（默认 10，即节拍缩短到 1/10；--speed 1 为真实节拍，范围 0.1-100）");
        Console.WriteLine("  --scenario NAME     场景预设: normal|stress|demo|fault|counteralarm|disconnect");
        Console.WriteLine("  --fresh             全新初始化（清零所有 PLC 值）");
        Console.WriteLine("  --noauto            不自动启动设备");
        Console.WriteLine("  --client IP PORT    连接外部虚拟 PLC");
        Console.WriteLine("  --listen-all        监听 0.0.0.0（默认仅 127.0.0.1；⚠ 局域网可无鉴权读写虚拟 PLC，仅限可信网络）");
    }

    private static void WaitExit()
    {
        if (Console.IsInputRedirected) return;
        Console.WriteLine("按任意键退出...");
        try { Console.ReadKey(); } catch { }
    }

    /// <summary>
    /// 单实例互斥获取（健壮版，与 Kanban.Collector 同模式）：非阻塞尝试取得命名互斥所有权。
    /// - 前实例崩溃遗留（AbandonedMutexException）→ 接管所有权，正常启动；
    /// - 已有实例持有 → 返回 null，调用方退出；
    /// - Global\ 前缀权限不足（UnauthorizedAccessException）→ 返回 null 保守退出，
    ///   避免无保护运行导致双监听抢端口。
    /// </summary>
    private static Mutex? TryAcquireSingleton(string name)
    {
        var mutex = new Mutex(false, name);
        try
        {
            if (mutex.WaitOne(TimeSpan.Zero))
                return mutex; // 取得所有权
            mutex.Dispose();
            return null; // 已有实例持有
        }
        catch (AbandonedMutexException)
        {
            return mutex; // 前实例崩溃，已接管所有权
        }
        catch (UnauthorizedAccessException)
        {
            mutex.Dispose();
            return null;
        }
    }

    private static List<DeviceConfig> CreateDefaultDevices()
    {
        return new List<DeviceConfig>
        {
            new()
            {
                Id = "device-001",
                Name = "注塑机1",
                OkCountAddress = "D100",
                NgCountAddress = "D102",
                StatusCountAddress = "D104",
                ProductionResetAddress = "D106",
                RecipeAddress = "D108",
                RecipeValue = 50,
                Alarms = new()
                {
                    new() { Id = "alm-001", Name = "高温报警", PlcAddress = "M100" },
                    new() { Id = "alm-002", Name = "低油压报警", PlcAddress = "M101" },
                },
                Defects = new()
                {
                    new() { Name = "毛边", PlcAddress = "D110" },
                    new() { Name = "缩水", PlcAddress = "D112" },
                },
                CounterAlarms = new()
                {
                    new() { Name = "连续不良计数", PlcAddress = "D114", MaxValue = 10 },
                },
                Sources = new()
                {
                    new()
                    {
                        Id = "src-temp-001", DeviceId = "device-001", Name = "车间温度", Type = "温湿度",
                        Values = new List<DataSourceValueConfigDto>
                        {
                            new() { Id = "src-temp-001-value", Name = "值1", PlcAddress = "D502", Unit = "℃", LimitMin = 200, LimitMax = 300 },
                        },
                    },
                    new()
                    {
                        Id = "src-hum-001", DeviceId = "device-001", Name = "车间湿度", Type = "温湿度",
                        Values = new List<DataSourceValueConfigDto>
                        {
                            new() { Id = "src-hum-001-value", Name = "值1", PlcAddress = "D504", Unit = "%", LimitMin = 300, LimitMax = 800 },
                        },
                    },
                    new()
                    {
                        Id = "src-trig-001", DeviceId = "device-001", Name = "温度触发采集", Type = "温湿度",
                        TriggerAddress = "D510",
                        Values = new List<DataSourceValueConfigDto>
                        {
                            new() { Id = "src-trig-001-value", Name = "值1", PlcAddress = "D502", Unit = "℃" },
                        },
                    },
                },
            },
            new()
            {
                Id = "device-002",
                Name = "注塑机2",
                OkCountAddress = "D200",
                NgCountAddress = "D202",
                StatusCountAddress = "D204",
                ProductionResetAddress = "D206",
                RecipeAddress = "D208",
                RecipeValue = 60,
                Alarms = new()
                {
                    new() { Id = "alm-003", Name = "高温报警", PlcAddress = "M200" },
                    new() { Id = "alm-004", Name = "门未关报警", PlcAddress = "M201" },
                },
                Defects = new()
                {
                    new() { Name = "划痕", PlcAddress = "D210" },
                },
                CounterAlarms = new()
                {
                    new() { Name = "停机次数", PlcAddress = "D212", MaxValue = 5 },
                },
                Sources = new()
                {
                    new()
                    {
                        Id = "src-temp-002", DeviceId = "device-002", Name = "车间温度", Type = "温湿度",
                        Values = new List<DataSourceValueConfigDto>
                        {
                            new() { Id = "src-temp-002-value", Name = "值1", PlcAddress = "D552", Unit = "℃", LimitMin = 200, LimitMax = 300 },
                        },
                    },
                    new()
                    {
                        Id = "src-trig-002", DeviceId = "device-002", Name = "温度触发采集", Type = "温湿度",
                        TriggerAddress = "D560",
                        Values = new List<DataSourceValueConfigDto>
                        {
                            new() { Id = "src-trig-002-value", Name = "值1", PlcAddress = "D552", Unit = "℃" },
                        },
                    },
                },
            },
            new()
            {
                Id = "device-003",
                Name = "组装机1",
                OkCountAddress = "D300",
                NgCountAddress = "D302",
                StatusCountAddress = "D304",
                ProductionResetAddress = "D306",
                RecipeAddress = "D308",
                RecipeValue = 100,
                Alarms = new()
                {
                    new() { Id = "alm-005", Name = "缺料报警", PlcAddress = "M300" },
                },
                Defects = new()
                {
                    new() { Name = "漏装", PlcAddress = "D310" },
                    new() { Name = "错装", PlcAddress = "D312" },
                },
                CounterAlarms = new()
                {
                    new() { Name = "连续NG", PlcAddress = "D314", MaxValue = 8 },
                },
                Sources = new()
                {
                    new()
                    {
                        Id = "src-temp-003", DeviceId = "device-003", Name = "车间温度", Type = "温湿度",
                        Values = new List<DataSourceValueConfigDto>
                        {
                            new() { Id = "src-temp-003-value", Name = "值1", PlcAddress = "D602", Unit = "℃", LimitMin = 200, LimitMax = 300 },
                        },
                    },
                },
            },
        };
    }

    // ──────────── TCP 代理（断线仿真用） ────────────

    /// <summary>
    /// 简单 TCP 双向转发代理：监听公开端口，将数据双向透明转发到内部 PLC 端口。
    /// 用于断线仿真：关闭/重启代理即可模拟 TCP 断连/恢复，无需调用 HslCommunication.ServerClose()
    /// （后者会在 AsyncAcceptCallback 中抛出未处理异常导致进程崩溃）。
    /// </summary>
    private sealed class TcpRelay : IDisposable
    {
        private TcpListener? _listener;
        private readonly List<TcpClient> _clients = [];
        private readonly int _listenPort;
        private readonly int _targetPort;
        private readonly IPAddress _listenAddress;
        private CancellationTokenSource _cts = new();
        private Task? _acceptTask;

        public TcpRelay(int listenPort, int targetPort, IPAddress listenAddress)
        {
            _listenPort = listenPort;
            _targetPort = targetPort;
            _listenAddress = listenAddress;
        }

        public void Start()
        {
            // 旧的 _cts 可能仍在被已停止循环的最后一次 await 引用，先 Dispose 再重建
            _cts.Dispose();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _listener = new TcpListener(_listenAddress, _listenPort);
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
            // 等待 accept 循环退出，避免与下一次 Start 竞争；忽略仍挂起的转发任务
            // （它们会随 client.Close 自然结束）
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
                catch { break; }  // 监听已停止或取消

                lock (_clients) _clients.Add(client);
                // 观察 RelayAsync 的异常，避免未观察的 Task 异常被吞到终结器
                var relayTask = RelayAsync(client, token);
                _ = relayTask.ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        var ex = t.Exception?.GetBaseException();
                        if (ex != null) SimLog.Warning($"[TcpRelay] 转发异常: {ex.Message}");
                    }
                }, TaskContinuationOptions.OnlyOnFaulted);
            }
        }

        private async Task RelayAsync(TcpClient client, CancellationToken token)
        {
            TcpClient? server = null;
            // 用 linked CTS：任一方向结束时取消另一方向，确保双向对称退出而非靠 close 间接触发异常
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            try
            {
                server = new TcpClient();
                await server.ConnectAsync("127.0.0.1", _targetPort, token).ConfigureAwait(false);

                var clientStream = client.GetStream();
                var serverStream = server.GetStream();

                var c2s = PumpAsync(clientStream, serverStream, linkedCts.Token);
                var s2c = PumpAsync(serverStream, clientStream, linkedCts.Token);
                // 任一方向结束（对端关闭或异常）即取消另一方向
                await Task.WhenAny(c2s, s2c).ConfigureAwait(false);
                linkedCts.Cancel();
                // 观察两个 Pump 的异常（吞掉因取消/关闭产生的 IOException）
                try { await Task.WhenAll(c2s, s2c).ConfigureAwait(false); } catch { }
            }
            catch { /* 连接断开属正常情况（断线仿真或客户端主动关闭） */ }
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
                if (n == 0) break;  // 对端关闭
                await to.WriteAsync(buffer.AsMemory(0, n), token).ConfigureAwait(false);
            }
        }
    }
}
