using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using Serilog;

namespace Kanban.Collector.Core.Data;

/// <summary>
/// 设备仓储抽象接口：供 ViewModel / Service 依赖，解耦具体实现。
/// </summary>
public interface IDeviceRepository
{
    ObservableCollection<Device> Devices { get; }
    ObservableCollection<DeviceRuntime> Runtimes { get; }
    string FilePath { get; }
    string? LoadErrorMessage { get; }
    Func<IReadOnlyList<Device>, Task>? RemotePersistenceHook { get; set; }
    Task SaveAllAsync();
    void LoadAll();
    void SaveAll();
    void ExportToFile(string path);
    List<Device>? ImportFromJson(string json);
    void ReplaceAll(IEnumerable<Device> newDevices);
    void AddRuntime(Device device);
    void RemoveRuntime(string deviceId);
    void SyncTargetCycle(string deviceId, int targetCycle);
    DeviceRuntime EnsureRuntime(Device device);
    List<Device> GetDevicesSnapshot();
    Device? GetDeviceById(string deviceId);
    List<DeviceRuntime> GetRuntimesSnapshot();
    ConcurrentDictionary<string, DeviceRuntime> RuntimeMap { get; }
}

/// <summary>
/// 设备仓储（DI 单例）：封装内存设备列表的访问与持久化操作。
/// 设备配置持久化到 JSON 文件（devices.json），启动时加载、保存时全量覆写。
/// 线程安全：Devices / Runtimes 的所有变更（Add/Clear/Remove/ReplaceAll）在 <see cref="SyncRoot"/> 内进行；
/// WPF 绑定同步锁由 UI 进程（MainAPP）经 <see cref="SyncRoot"/> 注册（Core 不依赖 WPF，见
/// MainAPP.Services.WpfCollectionBindingRegistrar）；
/// 后台采集线程通过 <see cref="GetDevicesSnapshot"/> / <see cref="GetRuntimesSnapshot"/> 获取快照副本，
/// 避免 ObservableCollection 在并发枚举时抛 InvalidOperationException。
/// RuntimeMap 改用 ConcurrentDictionary 以支持并发读写。
/// </summary>
public class DeviceRepository : IDeviceRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// 保护 Devices / Runtimes 变更的锁对象。
    /// 外部读取应使用 GetDevicesSnapshot / GetRuntimesSnapshot；WPF 绑定进程（MainAPP）经
    /// <see cref="SyncRoot"/> 注册给绑定引擎（BindingOperations.EnableCollectionSynchronization）。
    /// </summary>
    private readonly object _collectionLock = new();

    /// <summary>
    /// 集合同步锁（只读暴露）：供 WPF 绑定引擎注册跨线程同步（MainAPP 启动时调用
    /// BindingOperations.EnableCollectionSynchronization(Devices/Runtimes, SyncRoot)）。
    /// Core 自身不依赖 WPF API，无头 Collector 进程不注册。
    /// </summary>
    public object SyncRoot => _collectionLock;

    /// <summary>
    /// 内存中的设备配置列表
    /// </summary>
    public ObservableCollection<Device> Devices { get; } = new();

    /// <summary>
    /// 设备运行时状态集合（与 Devices 同步）
    /// </summary>
    public ObservableCollection<DeviceRuntime> Runtimes { get; } = new();

    /// <summary>
    /// DeviceId → DeviceRuntime 快速查找。改用 ConcurrentDictionary 以支持后台采集线程读 + UI 线程增删的并发访问。
    /// </summary>
    public ConcurrentDictionary<string, DeviceRuntime> RuntimeMap { get; } = new();

    /// <summary>
    /// DeviceId → Device 快速查找索引（与 <see cref="Devices"/> 同步维护，LoadAll/ReplaceAll 时更新）。
    /// 消除 GetDeviceById 的 O(n) 线性扫描——高频路径（RemoteRuntimeSink 每 500ms × 每设备）由线性退化到 O(1)。
    /// </summary>
    public ConcurrentDictionary<string, Device> DeviceMap { get; } = new();

    private readonly AppSettings _appSettings;

    public string FilePath => _appSettings.GetFilePath("devices.json");

    /// <summary>
    /// 加载时若 devices.json 损坏，记录错误信息供调用方通知用户。null 表示加载正常。
    /// </summary>
    public string? LoadErrorMessage { get; private set; }

    public DeviceRepository(AppSettings appSettings)
    {
        _appSettings = appSettings;
        // 注：WPF 绑定同步锁（EnableCollectionSynchronization）不在此注册——那是 UI 进程关注点，
        // 由 MainAPP 的 WpfCollectionBindingRegistrar 在宿主启动时经 SyncRoot 注册（Core 不依赖 WPF）。
    }

    /// <summary>
    /// 设备配置远程持久化委托（Remote 模式由 MainAPP 注入：经 SignalR 推给 Collector 落盘 devices.json）。
    /// Local 模式为 null。异步签名避免 UI 线程阻塞等待网络。
    /// </summary>
    public Func<IReadOnlyList<Device>, Task>? RemotePersistenceHook { get; set; }

    /// <summary>
    /// 异步保存全部设备。Remote 模式下委托 Collector 落盘（不写本地）；
    /// Local 模式走本地原子写（等价于 <see cref="SaveAll"/>）。
    /// </summary>
    public async Task SaveAllAsync()
    {
        // Remote 模式：Collector 是唯一写者，设备配置经 SignalR 推送落盘。
        if (RemotePersistenceHook != null)
        {
            var remoteSnapshot = CreateDeepSnapshot();
            await RemotePersistenceHook(remoteSnapshot);
            return;
        }

        SaveAll();
    }

    /// <summary>
    /// 从 devices.json 加载所有设备到内存，并同步创建对应的 DeviceRuntime。
    /// 启动时调用一次。文件不存在时 Devices 保持空集合。
    /// 文件损坏时备份原文件为 devices.json.corrupt，记录错误信息到 LoadErrorMessage 供调用方通知用户，
    /// 避免静默清空后 SaveAll 覆写导致原始配置永久丢失。
    /// </summary>
    public void LoadAll()
    {
        if (!File.Exists(FilePath))
        {
            lock (_collectionLock)
            {
                ReplaceStateLocked([]);
                LoadErrorMessage = null;
            }
            return;
        }

        try
        {
            var json = File.ReadAllText(FilePath);
            var devices = JsonSerializer.Deserialize<List<Device>>(json, JsonOptions)
                          ?? throw new InvalidDataException("设备配置为空或格式无效");
            NormalizeAndValidate(devices, migrateLegacy: true);
            lock (_collectionLock)
            {
                ReplaceStateLocked(devices);
                LoadErrorMessage = null;
            }
        }
        catch (JsonException ex)
        {
            HandleCorruptFile(ex);
        }
        catch (InvalidDataException ex)
        {
            HandleCorruptFile(ex);
        }
        catch (IOException ex)
        {
            LoadErrorMessage = $"设备配置文件读取失败：{ex.Message}。原文件已保留。";
            Log.Warning(ex, "读取 devices.json 失败，保留当前内存配置");
        }
        catch (UnauthorizedAccessException ex)
        {
            LoadErrorMessage = $"设备配置文件无权访问：{ex.Message}。原文件已保留。";
            Log.Warning(ex, "访问 devices.json 权限不足，保留当前内存配置");
        }
    }

    /// <summary>
    /// 将内存中所有设备全量写入 devices.json。
    /// 写入前回填子项的 DeviceId，确保 JSON 中数据完整。
    /// 采用原子写入（写临时文件 → 重命名），避免写入过程中崩溃导致文件损坏。
    /// Remote 模式下若设置了 <see cref="RemotePersistenceHook"/>，改为委托 Collector 落盘（MainAPP 不写本地）。
    /// </summary>
    public void SaveAll()
    {
        // Remote 模式：Collector 是唯一写者，设备配置经 SignalR 推送落盘。
        // 同步版本仅用于退出/迁移等同步上下文：阻塞等待推送完成，避免 fire-and-forget 丢数据。
        // 常规保存请使用 SaveAllAsync。
        if (RemotePersistenceHook != null)
        {
            var remoteSnapshot = CreateDeepSnapshot();
            // Task.Run 隔离同步上下文：RemotePersistenceHook 内部走 SignalR（真正异步）。
            Task.Run(() => RemotePersistenceHook(remoteSnapshot)).GetAwaiter().GetResult();
            return;
        }

        _appSettings.EnsureDirectory();

        List<Device> snapshot;
        lock (_collectionLock)
        {
            // 回填 DeviceId 到子集合（确保 JSON 中数据完整）
            foreach (var device in Devices)
            {
                foreach (var a in device.Alarms) a.DeviceId = device.Id;
                foreach (var d in device.Defects) d.DeviceId = device.Id;
                foreach (var c in device.CounterAlarms) c.DeviceId = device.Id;
                foreach (var s in device.Sources) s.DeviceId = device.Id;
            }
            snapshot = Devices.ToList();
        }

        var json = JsonSerializer.Serialize(snapshot, JsonOptions);

        // 原子写入：复用 AppSettings 的实现，避免写入中途崩溃产生截断的 JSON 文件
        Services.AppSettings.WriteFileAtomically(FilePath, json);
    }

    /// <summary>
    /// 将当前内存中的设备配置序列化并原子写入指定路径（导入/导出用）。
    /// 与 SaveAll 一致先回填子项 DeviceId，保证导出文件数据完整。
    /// </summary>
    public void ExportToFile(string path)
    {
        var snapshot = CreateDeepSnapshot();
        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        Services.AppSettings.WriteFileAtomically(path, json);
    }

    /// <summary>
    /// 反序列化导入的 JSON 文本为设备列表；格式或结构无效时返回 null。
    /// </summary>
    public List<Device>? ImportFromJson(string json)
    {
        var devices = JsonSerializer.Deserialize<List<Device>>(json, JsonOptions);
        if (devices == null) return null;
        NormalizeAndValidate(devices, migrateLegacy: true);
        return devices;
    }

    /// <summary>
    /// 用导入的设备列表整体替换内存中的设备配置（导入/导出用）。
    /// 替换后同步 Runtimes / RuntimeMap，保证设备列表与运行时状态一致；
    /// 解除旧设备的事件订阅（依赖 CollectionChanged 的 OldItems 处理），
    /// 并重新挂载新设备的事件订阅（AddRuntime + CollectionChanged 的 NewItems 处理）。
    /// 仅替换内存数据，不持久化（由调用方决定是否保存）。
    /// 整个替换在 _collectionLock 内进行，避免后台采集线程观察到半替换状态。
    /// </summary>
    public void ReplaceAll(IEnumerable<Device> newDevices)
    {
        var deviceList = newDevices as IList<Device> ?? newDevices.ToList();
        NormalizeAndValidate(deviceList, migrateLegacy: true);
        lock (_collectionLock)
            ReplaceStateLocked(deviceList);
    }

    /// <summary>同步新增设备的 Runtime；同一设备 Id 已存在时保持幂等。</summary>
    public void AddRuntime(Device device)
    {
        lock (_collectionLock)
        {
            if (RuntimeMap.ContainsKey(device.Id)) return;
            AddRuntimeLocked(device);
        }
    }

    /// <summary>同步删除设备的 Runtime。</summary>
    public void RemoveRuntime(string deviceId)
    {
        lock (_collectionLock)
        {
            if (RuntimeMap.TryRemove(deviceId, out var runtime))
                Runtimes.Remove(runtime);
        }
    }

    /// <summary>
    /// 同步 TargetCycle 变更到 Runtime
    /// </summary>
    public void SyncTargetCycle(string deviceId, int targetCycle)
    {
        if (RuntimeMap.TryGetValue(deviceId, out var runtime))
            runtime.SyncTargetCycle(targetCycle);
    }

    /// <summary>
    /// Remote 模式：确保指定设备存在 Runtime（不存在则按设备配置创建）。
    /// 供 KanbanDataClient 快照灌入前调用，避免与 UI 线程的 Add/Clear/Remove 并发。
    /// </summary>
    public DeviceRuntime EnsureRuntime(Device device)
    {
        lock (_collectionLock)
        {
            if (RuntimeMap.TryGetValue(device.Id, out var existing))
                return existing;
            return AddRuntimeLocked(device);
        }
    }

    /// <summary>
    /// 获取 Devices 的线程安全快照副本。供后台采集线程枚举使用，
    /// 避免与 UI 线程的 Add/Clear/Remove 并发导致 InvalidOperationException。
    /// </summary>
    public List<Device> GetDevicesSnapshot()
    {
        lock (_collectionLock)
            return Devices.ToList();
    }

    /// <summary>
    /// 按 Id 查找单个设备（DeviceMap 索引 O(1)，无锁、无快照拷贝分配）。
    /// 供高频路径使用（如 RemoteRuntimeSink 每 500ms×每设备一次）——索引与 Devices 在
    /// LoadAll/ReplaceAll 内同步维护。
    /// </summary>
    public Device? GetDeviceById(string deviceId)
        => DeviceMap.TryGetValue(deviceId, out var device) ? device : null;

    /// <summary>
    /// 获取 Runtimes 的线程安全快照副本。供后台采集线程枚举使用。
    /// </summary>
    public List<DeviceRuntime> GetRuntimesSnapshot()
    {
        lock (_collectionLock)
            return Runtimes.ToList();
    }

    private void NormalizeAndValidate(IList<Device> devices, bool migrateLegacy)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in devices)
        {
            if (string.IsNullOrWhiteSpace(device.Id))
                device.Id = Guid.NewGuid().ToString("N");
            if (!ids.Add(device.Id))
                throw new InvalidDataException($"设备 Id 重复：{device.Id}");

            if (migrateLegacy)
            {
                foreach (var source in device.Sources)
                {
                    try
                    {
                        var hadLegacy = source.Values.Count == 0 && source.ExtensionData?.ContainsKey("PlcAddress") == true;
                        source.MigrateLegacySingleValue();
                        if (hadLegacy)
                            Log.Information("数据源 {Source}（设备 {Device}）已从旧版单值格式迁移为值项格式，值项默认名「值1」", source.Name, device.Name);
                    }
                    catch (Exception ex) when (ex is FormatException or InvalidOperationException or KeyNotFoundException)
                    {
                        throw new InvalidDataException($"数据源「{source.Name}」旧版字段迁移失败", ex);
                    }
                }
            }

            NormalizeChildIds(device);
            if (migrateLegacy)
            {
                /* migration is performed above before child validation */
            }
        }
    }

    private static void NormalizeChildIds(Device device)
    {
        var alarmIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var alarm in device.Alarms)
        {
            alarm.DeviceId = device.Id;
            if (string.IsNullOrWhiteSpace(alarm.Id))
                alarm.Id = !string.IsNullOrWhiteSpace(alarm.PlcAddress)
                    ? $"{device.Id}_{alarm.PlcAddress}"
                    : Guid.NewGuid().ToString("N");
            if (!alarmIds.Add(alarm.Id))
                throw new InvalidDataException($"设备「{device.Name}」报警 Id 无效或重复");
        }

        ValidateChildIds(device.Defects, "缺陷", device.Name, x => x.Id = Guid.NewGuid().ToString("N"), x => x.DeviceId = device.Id);
        ValidateChildIds(device.CounterAlarms, "计数报警", device.Name, x => x.Id = Guid.NewGuid().ToString("N"), x => x.DeviceId = device.Id);

        var sourceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in device.Sources)
        {
            source.DeviceId = device.Id;
            if (string.IsNullOrWhiteSpace(source.Id)) source.Id = Guid.NewGuid().ToString("N");
            if (!sourceIds.Add(source.Id))
                throw new InvalidDataException($"设备「{device.Name}」采集源 Id 无效或重复");
            var valueIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in source.Values)
            {
                if (string.IsNullOrWhiteSpace(value.Id)) value.Id = Guid.NewGuid().ToString("N");
                if (!valueIds.Add(value.Id))
                    throw new InvalidDataException($"采集源「{source.Name}」值项 Id 无效或重复");
            }
        }
    }

    private static void ValidateChildIds<T>(IEnumerable<T> items, string kind, string deviceName, Action<T> assignId, Action<T> assignDevice)
        where T : class
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            assignDevice(item);
            var id = item switch
            {
                Defect d => d.Id,
                CounterAlarm c => c.Id,
                _ => string.Empty
            };
            if (string.IsNullOrWhiteSpace(id))
            {
                assignId(item);
                id = item switch { Defect d => d.Id, CounterAlarm c => c.Id, _ => string.Empty };
            }
            if (!ids.Add(id))
                throw new InvalidDataException($"设备「{deviceName}」{kind} Id 无效或重复");
        }
    }

    private void ReplaceStateLocked(IList<Device> devices)
    {
        Devices.Clear();
        DeviceMap.Clear();
        Runtimes.Clear();
        RuntimeMap.Clear();
        foreach (var device in devices)
        {
            Devices.Add(device);
            DeviceMap[device.Id] = device;
            AddRuntimeLocked(device);
        }
    }

    private DeviceRuntime AddRuntimeLocked(Device device)
    {
        var runtime = new DeviceRuntime(device);
        Runtimes.Add(runtime);
        RuntimeMap[device.Id] = runtime;
        return runtime;
    }

    private List<Device> CreateDeepSnapshot()
    {
        lock (_collectionLock)
        {
            foreach (var device in Devices)
                NormalizeChildIds(device);
            var json = JsonSerializer.Serialize(Devices.ToList(), JsonOptions);
            return JsonSerializer.Deserialize<List<Device>>(json, JsonOptions)
                   ?? new List<Device>();
        }
    }

    private void HandleCorruptFile(Exception ex)
    {
        Log.Warning(ex, "devices.json 解析或结构校验失败，将备份原文件");
        var corruptPath = FilePath + ".corrupt";
        var backedUp = false;
        try
        {
            if (File.Exists(corruptPath)) File.Delete(corruptPath);
            File.Move(FilePath, corruptPath);
            backedUp = true;
        }
        catch (Exception backupEx)
        {
            Log.Warning(backupEx, "devices.json 备份为 .corrupt 失败，原文件保留原位");
        }

        LoadErrorMessage = backedUp
            ? $"设备配置文件 devices.json 无效（{ex.Message}），已备份为 devices.json.corrupt。当前内存配置未改变。"
            : $"设备配置文件 devices.json 无效（{ex.Message}），备份失败，原文件已保留。当前内存配置未改变。";
    }
}
