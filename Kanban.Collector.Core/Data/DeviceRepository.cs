using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows.Data;
using Kanban.Core.Models;
using Kanban.Core.Services;
using Serilog;

namespace Kanban.Core.Data;

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
/// 线程安全：Devices / Runtimes 通过 <see cref="BindingOperations.EnableCollectionSynchronization"/> 注册锁，
/// 所有变更（Add/Clear/Remove/ReplaceAll）在 <see cref="_collectionLock"/> 内进行；
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
    /// 通过 BindingOperations.EnableCollectionSynchronization 注册给 WPF 绑定引擎，
    /// 绑定访问时也会取此锁；外部读取应使用 GetDevicesSnapshot / GetRuntimesSnapshot。
    /// </summary>
    private readonly object _collectionLock = new();

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

    private readonly AppSettings _appSettings;

    public string FilePath => _appSettings.GetFilePath("devices.json");

    /// <summary>
    /// 加载时若 devices.json 损坏，记录错误信息供调用方通知用户。null 表示加载正常。
    /// </summary>
    public string? LoadErrorMessage { get; private set; }

    public DeviceRepository(AppSettings appSettings)
    {
        _appSettings = appSettings;
        // 注册 WPF 绑定同步锁：WPF 绑定引擎访问 Devices / Runtimes 时会自动获取 _collectionLock，
        // 防止后台线程枚举与 UI 线程变更并发导致 InvalidOperationException。
        // 构造在 UI 线程进行（App DI），EnableCollectionSynchronization 需在任意线程访问集合前调用一次。
        BindingOperations.EnableCollectionSynchronization(Devices, _collectionLock);
        BindingOperations.EnableCollectionSynchronization(Runtimes, _collectionLock);
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
        // Remote 模式：Collector 是唯一写者，设备配置经 SignalR 推送落盘
        if (RemotePersistenceHook != null)
        {
            List<Device> remoteSnapshot;
            lock (_collectionLock)
            {
                foreach (var device in Devices)
                {
                    foreach (var a in device.Alarms) a.DeviceId = device.Id;
                    foreach (var d in device.Defects) d.DeviceId = device.Id;
                    foreach (var c in device.CountAlarms) c.DeviceId = device.Id;
                }
                remoteSnapshot = Devices.ToList();
            }
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
        lock (_collectionLock)
        {
            Devices.Clear();
            Runtimes.Clear();
            RuntimeMap.Clear();
            LoadErrorMessage = null;
        }

        if (!File.Exists(FilePath)) return;

        try
        {
            var json = File.ReadAllText(FilePath);
            var devices = JsonSerializer.Deserialize<List<Device>>(json, JsonOptions);
            if (devices != null)
            {
                lock (_collectionLock)
                {
                    foreach (var d in devices)
                    {
                        Devices.Add(d);
                        var runtime = new DeviceRuntime(d);
                        Runtimes.Add(runtime);
                        RuntimeMap[d.Id] = runtime;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // 文件损坏：备份原文件供用户恢复，而非直接覆写导致数据永久丢失
            Log.Warning(ex, "devices.json 解析失败，将备份原文件并回退空设备列表");
            var corruptPath = FilePath + ".corrupt";
            try
            {
                if (File.Exists(corruptPath))
                    File.Delete(corruptPath);
                File.Move(FilePath, corruptPath);
            }
            catch (Exception backupEx)
            {
                Log.Warning(backupEx, "devices.json 备份为 .corrupt 失败，原文件保留原位");
            }

            LoadErrorMessage = $"设备配置文件 devices.json 损坏（{ex.Message}），已备份为 devices.json.corrupt。" +
                               "已加载空设备列表，请重新配置设备后保存。";
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
            List<Device> remoteSnapshot;
            lock (_collectionLock)
            {
                foreach (var device in Devices)
                {
                    foreach (var a in device.Alarms) a.DeviceId = device.Id;
                    foreach (var d in device.Defects) d.DeviceId = device.Id;
                    foreach (var c in device.CountAlarms) c.DeviceId = device.Id;
                }
                remoteSnapshot = Devices.ToList();
            }
            // Task.Run 隔离同步上下文：RemotePersistenceHook 内部走 SignalR（真正异步），
            // 直接 .GetAwaiter().GetResult() 在 UI 线程调用会死锁。Task.Run 转入线程池执行，
            // 无 SynchronizationContext 回跳，避免死锁。仅 Remote 模式且需同步保存时命中此分支。
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
                foreach (var c in device.CountAlarms) c.DeviceId = device.Id;
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
        List<Device> snapshot;
        lock (_collectionLock)
        {
            foreach (var device in Devices)
            {
                foreach (var a in device.Alarms) a.DeviceId = device.Id;
                foreach (var d in device.Defects) d.DeviceId = device.Id;
                foreach (var c in device.CountAlarms) c.DeviceId = device.Id;
            }
            snapshot = Devices.ToList();
        }

        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        Services.AppSettings.WriteFileAtomically(path, json);
    }

    /// <summary>
    /// 反序列化导入的 JSON 文本为设备列表；解析失败返回 null（由调用方提示错误）。
    /// </summary>
    public List<Device>? ImportFromJson(string json)
        => JsonSerializer.Deserialize<List<Device>>(json, JsonOptions);

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
        lock (_collectionLock)
        {
            Devices.Clear();
            Runtimes.Clear();
            RuntimeMap.Clear();

            foreach (var device in deviceList)
            {
                Devices.Add(device);
                AddRuntime(device);
            }
        }
    }

    /// <summary>
    /// 同步新增设备的 Runtime
    /// </summary>
    public void AddRuntime(Device device)
    {
        var runtime = new DeviceRuntime(device);
        lock (_collectionLock)
        {
            Runtimes.Add(runtime);
        }
        RuntimeMap[device.Id] = runtime;
    }

    /// <summary>
    /// 同步删除设备的 Runtime
    /// </summary>
    public void RemoveRuntime(string deviceId)
    {
        if (RuntimeMap.TryRemove(deviceId, out var runtime))
        {
            lock (_collectionLock)
            {
                Runtimes.Remove(runtime);
            }
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
        if (RuntimeMap.TryGetValue(device.Id, out var existing))
            return existing;

        lock (_collectionLock)
        {
            if (RuntimeMap.TryGetValue(device.Id, out existing))
                return existing;
            var runtime = new DeviceRuntime(device);
            Runtimes.Add(runtime);
            RuntimeMap[device.Id] = runtime;
            return runtime;
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
    /// 按 Id 查找单个设备（锁内线性扫描，无快照拷贝分配）。
    /// 供高频路径使用（如 RemoteRuntimeSink 每 500ms×每设备一次），
    /// 避免 GetDevicesSnapshot().FirstOrDefault 的每次全量 ToList 分配。
    /// 设备数量达到百级时可改为 DeviceId 索引字典，当前量级线性扫描足够。
    /// </summary>
    public Device? GetDeviceById(string deviceId)
    {
        lock (_collectionLock)
        {
            foreach (var device in Devices)
            {
                if (device.Id == deviceId) return device;
            }
            return null;
        }
    }

    /// <summary>
    /// 获取 Runtimes 的线程安全快照副本。供后台采集线程枚举使用。
    /// </summary>
    public List<DeviceRuntime> GetRuntimesSnapshot()
    {
        lock (_collectionLock)
            return Runtimes.ToList();
    }
}
