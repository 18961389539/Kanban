using System.Collections.Generic;
using MainAPP.Resources;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Models;
using MainAPP.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using Serilog;

namespace MainAPP.ViewModels;

/// <summary>
/// 设备列表按状态筛选枚举（用于设备管理器状态筛选下拉）。
/// </summary>
public enum DeviceStatusFilter
{
    All,
    Running,
    Alarm,
    Paused,
    Offline,
}

/// <summary>
/// 状态筛选下拉项（值 + 中文标签）。
/// </summary>
public class StatusFilterOption
{
    public DeviceStatusFilter Value { get; set; }
    public string Label { get; set; } = string.Empty;
}

public partial class DeviceManagerViewModel : ObservableObject, IDeviceManagerHost, IDisposable
{
    private readonly IPlcDataAcquisitionService _dataAcquisitionService;
    private readonly DeviceRepository _deviceRepository;
    private readonly IDialogService _dialog;
    private readonly DeviceConfigIOService _configIO;
    private readonly PlcConnectionManager? _connectionManager;
    private readonly IPlcAddressCodecResolver? _addressCodecResolver;
    private readonly IPlcRuntimeProfileProvider? _profileProvider;
    private readonly UserSession _userSession;
    private DeviceAuditSnapshot _lastSavedDeviceAuditSnapshot = new(0, []);

    // 设备列表由 DeviceRepository（DI 单例）持有，ViewModel 直接引用
    public ObservableCollection<Device> Devices => _deviceRepository.Devices;

    /// <summary>设备列表子 VM（搜索/状态筛选/摘要与过滤视图）。</summary>
    public DeviceListViewModel DeviceList { get; }

    /// <summary>设备 Id 到冲突 PLC 地址的摘要，供列表项显示和点击定位。</summary>
    public IReadOnlyDictionary<string, string> AddressConflictSummaries { get; private set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>最近一次保存校验产生的全部错误。</summary>
    public IReadOnlyList<DeviceConfigError> ValidationErrors { get; private set; } = [];

    /// <summary>当前选中设备的配置错误，显示在设备参数表单顶部。</summary>
    public IReadOnlyList<DeviceConfigError> CurrentDeviceValidationErrors =>
        SelectedDevice == null
            ? []
            : ValidationErrors.Where(error => ReferenceEquals(error.Device, SelectedDevice)).ToArray();

    public bool HasCurrentDeviceValidationErrors => CurrentDeviceValidationErrors.Count > 0;

    /// <summary>当前点击的冲突地址，供设备参数表单聚焦对应输入框。</summary>
    [ObservableProperty]
    private string _focusedAddressConflict = string.Empty;

    /// <summary>报警管理子 VM（报警 CRUD + CSV 导入导出）。</summary>
    public DeviceAlarmManagerViewModel AlarmManagerVm { get; }

    /// <summary>缺陷管理子 VM（缺陷 CRUD）。</summary>
    public DeviceDefectManagerViewModel DefectManagerVm { get; }

    /// <summary>计数报警管理子 VM（计数报警 CRUD + 清空当前值）。</summary>
    public DeviceCounterAlarmManagerViewModel CounterAlarmManagerVm { get; }

    /// <summary>工单管理子 VM（按设备过滤 + 6 个工单命令）。</summary>
    public DeviceWorkOrderViewModel WorkOrders { get; }

    /// <summary>PLC 命令子 VM（写配方 / OEE 清零 / 读地址 + 状态栏）。</summary>
    public DevicePlcCommandViewModel PlcCommands { get; }

    [ObservableProperty]
    private Device? _selectedDevice;

    /// <summary>
    /// PLC 写入中标志：写入配方 / 清空计数报警当前值期间为 true，UI 显示加载覆盖层。
    /// UI 自动化测试可监听此属性（或 ByName("加载中")）等待异步操作完成。
    /// </summary>
    [ObservableProperty]
    private bool _isLoading;

    /// <summary>
    /// 是否有未保存到磁盘的改动。任意设备配置（名称/地址/子项增删等）变更后置 true，
    /// 保存成功后置 false。运行时字段（如计数报警当前值）变更不计入，避免误报。
    /// UI 据此在标题区显示"● 未保存"、列表项名旁显示"*"，降低未保存配置被忽略的风险。
    /// </summary>
    [ObservableProperty]
    private bool _isDirty;

    /// <summary>
    /// 是否存在跨设备 PLC 地址冲突（两台及以上设备共用同一地址，会导致数据串台）。
    /// 编辑时实时刷新，供列表标题区显示冲突警告标记。
    /// </summary>
    [ObservableProperty]
    private bool _hasAddressConflicts;

    /// <summary>
    /// 跨设备冲突的地址数量（实时）。
    /// </summary>
    [ObservableProperty]
    private int _addressConflictCount;

    /// <summary>
    /// 设备详情选项卡当前索引（0=设备参数, 1=报警管理, 2=缺陷管理, 3=计数报警）。
    /// 供保存校验错误"定位"时切换到出错项所在选项卡；与 XAML TabControl.SelectedIndex 双向绑定。
    /// </summary>
    [ObservableProperty]
    private int _selectedTabIndex;

    /// <summary>
    /// 设备运行时映射（DeviceId → DeviceRuntime），供设备列表项实时显示状态色点。
    /// 运行时状态字（StatusWord）变化时通过 OnPropertyChanged 通知，驱动列表色点刷新。
    /// 注意：返回的是稳定的 Dictionary 引用，刷新依赖本属性主动抛出 PropertyChanged。
    /// </summary>
    public IDictionary<string, DeviceRuntime> DeviceRuntimeMap => _deviceRepository.RuntimeMap;

    private readonly DirtyTracker _dirtyTracker;

    public DeviceManagerViewModel(
        DeviceRepository deviceRepository,
        IPlcDataAcquisitionService dataAcquisitionService,
        IDialogService dialog,
        DeviceConfigIOService configIO,
        DevicePlcCommandHandler plcCommands,
        AlarmCsvIOService alarmCsvIO,
        DefectCsvIOService defectCsvIO,
        CounterAlarmCsvIOService counterAlarmCsvIO,
        WorkOrderRepository workOrderRepo,
        IWorkOrderService workOrderService,
        UserSession userSession,
        PlcConnectionManager? connectionManager = null,
        IPlcAddressCodecResolver? addressCodecResolver = null,
        IPlcRuntimeProfileProvider? profileProvider = null)
    {
        _deviceRepository = deviceRepository;
        _dataAcquisitionService = dataAcquisitionService;
        _dialog = dialog;
        _configIO = configIO;
        _userSession = userSession;
        _connectionManager = connectionManager;
        _addressCodecResolver = addressCodecResolver;
        _profileProvider = profileProvider;
        DeviceList = new DeviceListViewModel(deviceRepository);

        // 构造子 VM（报警/缺陷/计数报警管理），传入各自所需的共享依赖与父级宿主引用。
        // 子 VM 通过 IDeviceManagerHost 订阅 SelectedDevice/IsLoading 变化并回写脏标记，
        // 实现跨 Tab 联动而无需双向引用。CSV IO 服务仅用于构造对应子 VM，父级不再直接持有。
        AlarmManagerVm = new DeviceAlarmManagerViewModel(dialog, alarmCsvIO, dataAcquisitionService, this);
        DefectManagerVm = new DeviceDefectManagerViewModel(dialog, defectCsvIO, this);
        CounterAlarmManagerVm = new DeviceCounterAlarmManagerViewModel(dialog, plcCommands, counterAlarmCsvIO, this);
        WorkOrders = new DeviceWorkOrderViewModel(dialog, this, workOrderRepo, workOrderService, deviceRepository);
        PlcCommands = new DevicePlcCommandViewModel(dialog, this, plcCommands);

        // 订阅设备集合与每个设备的属性/子集合变更，用于维护脏标记
        _dirtyTracker = new DirtyTracker(() => { IsDirty = true; ScheduleAddressConflictRefresh(); });
        _deviceRepository.Devices.CollectionChanged += OnDevicesCollectionChanged;
        foreach (var d in Devices) _dirtyTracker.AttachDevice(d);

        // 订阅运行时集合与每个运行时的状态变更，用于驱动设备列表状态色点实时刷新
        _deviceRepository.Runtimes.CollectionChanged += OnRuntimesCollectionChanged;
        foreach (var rt in _deviceRepository.Runtimes) AttachRuntime(rt);

        // 初始计算跨设备地址冲突标记（LoadAll 已在 ViewModel 构造前完成）
        _lastSavedDeviceAuditSnapshot = CreateDeviceAuditSnapshot();
        RefreshAddressConflictFlag();
        if (_connectionManager != null)
            _connectionManager.PropertyChanged += OnConnectionPropertyChanged;
    }

    /// <summary>设备全量配置审计快照（全字段、全设备），委托 <see cref="DeviceAuditService"/> 生成。</summary>
    private DeviceAuditSnapshot CreateDeviceAuditSnapshot() => DeviceAuditService.CreateSnapshot(Devices);

    /// <summary>
    /// 外部整体替换设备列表（Remote 拉取 / 导入 / 恢复备份 / 样本数据）后同步审计基线，
    /// 避免下一次保存把「上一次保存」误记为「替换前状态」。调用点须在替换完成后调用。
    /// </summary>
    public void SyncAuditBaseline() => _lastSavedDeviceAuditSnapshot = CreateDeviceAuditSnapshot();

    [RelayCommand]
    private void AddDevice()
    {
        // 保证新设备名唯一：若默认名冲突则追加数字后缀
        var baseName = string.Format(Strings.F135, Devices.Count + 1);
        var newName = EnsureUniqueName(baseName, Devices.Select(d => d.Name));
        var newDevice = new Device { Name = newName };
        Devices.Add(newDevice);
        _deviceRepository.AddRuntime(newDevice);
        DeviceList.SearchKeyword = string.Empty;
        SelectedDevice = newDevice;
        MarkDirty();
        // 注意：不在此处刷新审计基线——基线语义是「上次已保存」状态，
        // 新增后尚未保存，下一次保存的 before 应反映「新增前」的持久化状态。
    }

    /// <summary>
    /// 生成不与 existing 冲突的唯一名称：若 baseName 已存在则追加 " (2)"、" (3)"...
    /// 内部可见供报警/缺陷/计数报警子 VM 复用，避免重复实现。
    /// </summary>
    internal static string EnsureUniqueName(string baseName, IEnumerable<string> existing)
    {
        var set = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        if (!set.Contains(baseName)) return baseName;
        for (int i = 2; ; i++)
        {
            var candidate = $"{baseName} ({i})";
            if (!set.Contains(candidate)) return candidate;
        }
    }

    // 删除按钮的启用条件：必须选中设备且不在 PLC 写入中（避免异步回调访问已删除设备）
    private bool CanEditSelected() => SelectedDevice != null && !IsLoading;

    // 未注入连接管理器（本地/单机或测试场景）视为未连接，PLC 写命令应禁用，
    // 避免离线时误执行写配方/清零点/OEE 清零等操作（审查修复 2026-08-15）。
    public bool IsPlcConnected => _connectionManager?.IsConnected ?? false;

    private void OnConnectionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PlcConnectionManager.IsConnected)) return;

        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            _ = dispatcher.InvokeAsync(() => OnConnectionPropertyChanged(sender, e));
            return;
        }

        OnPropertyChanged(nameof(IsPlcConnected));
    }

    // 保存按钮的启用条件：至少有一个设备。
    // 不依赖 SelectedDevice，避免删除选中设备后 SelectedDevice=null 导致 Save 按钮变灰无法持久化删除操作
    // 保存期间 IsLoading=true（防双击并发保存 + 禁用 PLC 写命令），故 CanSave 需排除 IsLoading
    private bool CanSave() => Devices.Count > 0 && !IsLoading;

    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private void RemoveDevice(Device? device)
    {
        var target = device ?? SelectedDevice;
        if (target == null) return;

        // 使用 HC MessageBox（深色主题）进行 YesNo 确认，返回 MessageBoxResult 与原 API 一致。
        var result = _dialog.Show(
            string.Format(Strings.F175, target.Name),
            Strings.M118, MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        // 报警/缺陷/计数报警的选中状态由各子 VM 订阅 SelectedDevice 变化自动清空
        // （下方 SelectedDevice = null 触发同步），无需在此显式处理。

        // 删除设备前清理采集服务中该设备的残留内存状态（产量基线 + 报警状态），避免内存泄漏
        _dataAcquisitionService.RemoveDeviceState(target);
        _deviceRepository.RemoveRuntime(target.Id);

        // 先清空 SelectedDevice 再从 Devices 移除，避免 Devices.Remove 触发 CollectionChanged
        // 后 UI 短暂渲染"已 Detach 但仍选中"的瞬态。
        if (SelectedDevice == target)
            SelectedDevice = null;
        Devices.Remove(target);

        // 设备数量变化后刷新 Save 按钮可用状态（删除最后一个设备时 CanSave 应变 false）
        SaveCommand.NotifyCanExecuteChanged();

        _dialog.NotifySuccess(Strings.M008);
        MarkDirty();
        // 不在此处刷新审计基线：删除尚未保存，before 应反映「删除前」的持久化状态。
    }

    /// <summary>
    /// 选中冲突设备并切换到设备参数 Tab，地址输入框由视图根据 FocusedAddressConflict 聚焦。
    /// </summary>
    [RelayCommand]
    private void SelectAddressConflict(Device? device)
    {
        if (device == null) return;
        SelectedDevice = device;
        SelectedTabIndex = 0;
        FocusedAddressConflict = AddressConflictSummaries.TryGetValue(device.Id, out var summary)
            ? summary.Split(',', StringSplitOptions.TrimEntries)[0]
            : string.Empty;
    }

    /// <summary>
    /// 复制选中设备：深拷贝其配置（名称/PLC 地址/配方/目标周期 + 报警/缺陷/计数报警子集合），
    /// 生成新 Id 与唯一名称（源名 + " 副本"），加入设备列表并置脏。
    /// 便于快速搭建结构相似的同类设备，避免逐项手填。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private void CopyDevice()
    {
        var src = SelectedDevice;
        if (src == null) return;

        var copy = CloneDevice(src);
        copy.Name = EnsureUniqueName(string.Format(Strings.F039, src.Name), Devices.Select(d => d.Name));
        Devices.Add(copy);
        _deviceRepository.AddRuntime(copy);
        SelectedDevice = copy;
        MarkDirty();
        _dialog.NotifySuccess(string.Format(Strings.F100, src.Name, copy.Name));
    }

    /// <summary>
    /// 深拷贝设备配置（不含 Id/Name：Name 由调用方保证唯一，Id 由 Device 构造自动生成）。
    /// 子集合逐项克隆并重新挂接 DeviceId（报警的确定性 Id 由此再生），避免与原设备共享引用。
    /// </summary>
    private static Device CloneDevice(Device src)
    {
        var copy = new Device
        {
            OkCountAddress = src.OkCountAddress,
            NgCountAddress = src.NgCountAddress,
            StatusCountAddress = src.StatusCountAddress,
            ProductionResetAddress = src.ProductionResetAddress,
            RecipeName = src.RecipeName,
            RecipeValue = src.RecipeValue,
            RecipeAddress = src.RecipeAddress,
            TargetCycle = src.TargetCycle,
        };

        foreach (var a in src.Alarms)
        {
            // 先设 DeviceId 再设 PlcAddress，确保 OnPlcAddressChanged 能基于新 DeviceId 生成确定性 Id
            copy.Alarms.Add(new Alarm
            {
                DeviceId = copy.Id,
                Name = a.Name,
                PlcAddress = a.PlcAddress,
                Description = a.Description,
                Level = a.Level,
            });
        }
        foreach (var d in src.Defects)
        {
            copy.Defects.Add(new Defect
            {
                DeviceId = copy.Id,
                Name = d.Name,
                PlcAddress = d.PlcAddress,
                Severity = d.Severity,
                Category = d.Category,
            });
        }
        foreach (var c in src.CounterAlarms)
        {
            copy.CounterAlarms.Add(new CounterAlarm
            {
                DeviceId = copy.Id,
                Name = c.Name,
                PlcAddress = c.PlcAddress,
                MaxValue = c.MaxValue,
                Description = c.Description,
                Unit = c.Unit,
                Enabled = c.Enabled,
            });
        }
        return copy;
    }

    /// <summary>
    /// 拖拽排序：将 dragged 设备移动到 target 设备在列表中的位置（位置即持久化顺序）。
    /// 仅排序不增删，不重挂事件订阅；移动后标记脏使顺序变更可被保存。
    /// </summary>
    public void MoveDevice(Device dragged, Device target)
    {
        if (dragged == null || target == null || ReferenceEquals(dragged, target)) return;
        var from = Devices.IndexOf(dragged);
        var to = Devices.IndexOf(target);
        if (from < 0 || to < 0 || from == to) return;
        Devices.Move(from, to);
        MarkDirty();
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task Save()
    {
        // 聚合全部配置错误（不再逐个 return），统一列出并支持点击定位到出错设备/选项卡
        var errors = DeviceConfigValidator.CollectValidationErrors(
            Devices,
            _profileProvider?.Current.AddressCodec ?? _addressCodecResolver?.Current);
        ValidationErrors = errors;
        OnPropertyChanged(nameof(ValidationErrors));
        OnPropertyChanged(nameof(CurrentDeviceValidationErrors));
        OnPropertyChanged(nameof(HasCurrentDeviceValidationErrors));
        if (errors.Count > 0)
        {
            _dialog.NotifyWarning(string.Format(Strings.F067, errors.Count));
            return;
        }

        // 防重入：保存期间置 IsLoading（禁用 PLC 写命令 + Save 自身），防止双击/并发触发两次保存
        IsLoading = true;
        try
        {
            // 保存内部会回填子项 DeviceId（触发属性变更），临时抑制脏标记避免自我触发。
            // Remote 模式下 SaveAllAsync 经 SignalR 推给 Collector 落盘（异步，不阻塞 UI 线程）
            // 前后值摘要：全设备 + 全配置字段（DeviceAuditService.CreateSnapshot）。
            var before = _lastSavedDeviceAuditSnapshot;
            var after = CreateDeviceAuditSnapshot();
            _dirtyTracker.IsSuppressed = true;
            await _deviceRepository.SaveAllAsync();

            // 保存后同步所有设备运行时的 TargetCycle
            foreach (var device in Devices)
                _deviceRepository.SyncTargetCycle(device.Id, device.TargetCycle);

            _lastSavedDeviceAuditSnapshot = after;
            // 非阻断提示：0 值阈值报警（仅记录不触发）仍可正常保存，但提醒用户其不会触发报警
            var zeroThresholdCount = Devices.Sum(d => d.CounterAlarms.Count(c => c.MaxValue <= 0));
            _dialog.NotifySuccess(zeroThresholdCount > 0
                ? string.Format(Strings.K651, after.Count, zeroThresholdCount)
                : Strings.M009);
            IsDirty = false;
            AuditLog.Record("Device.Update", "Device", null,
                before: before,
                after: after,
                detail: string.Format(Strings.F_DevicesSaved, after.Count));
        }
        catch (System.Exception ex)
        {
            _dialog.NotifyError(string.Format(Strings.F066, ex.Message));
            Log.Error(ex, "保存设备配置失败");
        }
        finally
        {
            _dirtyTracker.IsSuppressed = false;
            IsLoading = false;
        }
    }

    /// <summary>
    /// 重算跨设备地址冲突标记（实时）。在 MarkDirty 与构造时调用，驱动列表标题区的冲突警告标记。
    /// </summary>
    private void RefreshAddressConflictFlag()
    {
        var report = AddressConflictService.Compute(Devices);
        AddressConflictSummaries = report.Summaries;
        AddressConflictCount = report.ConflictCount;
        HasAddressConflicts = report.ConflictCount > 0;
        OnPropertyChanged(nameof(AddressConflictSummaries));
    }

    [RelayCommand]
    private void FocusValidationError(DeviceConfigError? error)
    {
        if (error == null) return;
        NavigateToError(error);
    }

    /// <summary>
    /// 校验错误定位：选中对应设备并切换到目标选项卡。
    /// err.Device 为 null 时仅切换选项卡（设备可能已被外部移除）。
    /// </summary>
    private void NavigateToError(DeviceConfigError err)
    {
        if (err.Device != null)
            SelectedDevice = err.Device;
        SelectedTabIndex = err.TargetTabIndex;
    }

    // ──────────── 导入 / 导出 / 恢复（委托 DeviceConfigIOService） ────────────

    /// <summary>
    /// 导出当前全部设备配置到用户选择的 JSON 文件（原子写入，备份上一版本）。
    /// 仅导出内存中的配置、不触发持久化或脏标记变化（导出是只读操作）。
    /// 用户取消保存对话框则不写文件。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private void ExportConfig()
    {
        _configIO.ExportConfig(Devices.Count);
    }

    /// <summary>
    /// 从用户选择的 JSON 文件导入设备配置，整体替换当前内存中的设备（含运行时状态）。
    /// 导入前二次确认（替换会丢弃当前未保存的配置），导入后标记脏并提示用户保存以持久化。
    /// 文件解析失败或文件无设备数据时给出对应提示，不替换。
    /// </summary>
    [RelayCommand]
    private void ImportConfig()
    {
        var imported = _configIO.ImportConfig(Devices.Count);
        if (imported == null) return;

        SelectedDevice = Devices.FirstOrDefault();
        SaveCommand.NotifyCanExecuteChanged();
        RemoveDeviceCommand.NotifyCanExecuteChanged();
        DeviceList.RefreshDeviceList();
        SyncAuditBaseline();
        MarkDirty();
    }

    /// <summary>
    /// 恢复上一版本：复用 AppSettings.WriteFileAtomically 每次保存前留下的 devices.json.bak，
    /// 把上一次保存前的配置加载回内存（走已验证的 ReplaceAll 路径，自动重建 Runtimes/订阅），
    /// 标记脏并提示用户点击保存以持久化。二次确认防止误覆盖当前未保存改动。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRollbackToBackup))]
    private void RollbackToBackup()
    {
        // 权限验证：恢复上一版本会覆盖当前未保存的设备配置，需工程师或以上角色
        if (!_userSession.IsEngineerOrAbove)
        {
            _dialog.NotifyWarning(Strings.M336);
            return;
        }

        var restored = _configIO.RollbackToBackup();
        if (restored == null) return;

        SelectedDevice = Devices.FirstOrDefault();
        SaveCommand.NotifyCanExecuteChanged();
        RemoveDeviceCommand.NotifyCanExecuteChanged();
        DeviceList.RefreshDeviceList();
        RefreshAddressConflictFlag();
        SyncAuditBaseline();
        MarkDirty();
    }

    private bool CanRollbackToBackup() => _configIO.HasBackup;

    /// <summary>
    /// 是否为 DEBUG 编译版本。绑定到"生成虚拟设备"按钮的 Visibility，
    /// 避免发布版暴露虚拟数据生成功能。Release 编译时按钮折叠。
    /// </summary>
    public bool IsDebugBuild => MainAPP.Helpers.BuildInfo.IsDebug;

    /// <summary>
    /// 生成 20 台虚拟设备用于 UI 预览/调试。覆盖 4 个 Tab 的所有字段：
    /// 设备参数（PLC 地址 + 配方）、报警管理（不同级别）、缺陷管理（不同严重等级 + 类别）、
    /// 计数报警（不同单位 + 阈值）。所有 PLC 地址跨设备唯一（由 SampleDeviceBuilder 内部校验），
    /// 避免冲突告警干扰预览。若当前已有设备，提示是否替换；点击保存后才会写入磁盘。
    /// </summary>
    [RelayCommand]
    private void SeedSampleDevices()
    {
        // 权限验证：生成虚拟数据需工程师或以上角色
        if (!_userSession.IsEngineerOrAbove)
        {
            _dialog.NotifyWarning(Strings.M336);
            return;
        }

        if (Devices.Count > 0)
        {
            var confirm = _dialog.Show(
                string.Format(Strings.F092, Devices.Count),
                Strings.M169, MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;
        }

        var samples = SampleDeviceBuilder.BuildSampleDevices();
        _deviceRepository.ReplaceAll(samples);
        SelectedDevice = Devices.FirstOrDefault();
        SaveCommand.NotifyCanExecuteChanged();
        RemoveDeviceCommand.NotifyCanExecuteChanged();
        DeviceList.RefreshDeviceList();
        SyncAuditBaseline();
        MarkDirty();
        _dialog.NotifySuccess(string.Format(Strings.F114, samples.Count));
    }

    partial void OnSelectedDeviceChanged(Device? value)
    {
        // 各子 VM（报警/缺陷/计数报警/工单/PLC）通过 DeviceChildManagerViewModel 订阅宿主 PropertyChanged 自动同步 SelectedDevice，无需在此手动赋值。
        OnPropertyChanged(nameof(CurrentDeviceValidationErrors));
        OnPropertyChanged(nameof(HasCurrentDeviceValidationErrors));
        SaveCommand.NotifyCanExecuteChanged();
        RemoveDeviceCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsLoadingChanged(bool value)
    {
        // IsLoading 变化时刷新设备编辑类命令可用状态；PLC 命令可用状态由 PlcCommands 子 VM 订阅 IsLoading 自行刷新
        SaveCommand.NotifyCanExecuteChanged();
        RemoveDeviceCommand.NotifyCanExecuteChanged();
    }

    public void ReportPlcOperation(PlcOpResult result) => PlcCommands.ReportPlcOperation(result);

    public void ReportPlcOperationStarted() => PlcCommands.ReportPlcOperationStarted();

    // ──────────── 脏标记维护 ────────────

    private void OnDevicesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 仅排序（Move）不增删设备，事件订阅保持不变，无需重挂
        if (e.Action == NotifyCollectionChangedAction.Move) return;

        if (e.NewItems != null)
            foreach (Device d in e.NewItems) _dirtyTracker.AttachDevice(d);
        if (e.OldItems != null)
            foreach (Device d in e.OldItems) _dirtyTracker.DetachDevice(d);
        // 不在此 MarkDirty：避免启动时 LoadAll 的批量 Add 误报未保存；
        // 集合增删的脏标记由各增删命令显式调用 MarkDirty()。
    }

    // ──────────── 运行时状态变更（驱动列表色点实时刷新） ────────────

    private void OnRuntimesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (DeviceRuntime rt in e.NewItems) AttachRuntime(rt);
        if (e.OldItems != null)
            foreach (DeviceRuntime rt in e.OldItems) DetachRuntime(rt);
        // 运行时集合变更（设备增删）→ 通知列表色点刷新（新设备默认离线）
        OnPropertyChanged(nameof(DeviceRuntimeMap));
    }

    private void AttachRuntime(DeviceRuntime rt) => rt.PropertyChanged += OnRuntimePropertyChanged;

    private void DetachRuntime(DeviceRuntime rt) => rt.PropertyChanged -= OnRuntimePropertyChanged;

    private void OnRuntimePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 仅状态字变化才驱动刷新（采集线程每轮轮询；稳态下 CommunityToolkit 不会重复抛事件）
        if (e.PropertyName != nameof(DeviceRuntime.StatusWord)) return;
        OnPropertyChanged(nameof(DeviceRuntimeMap)); // 列表色点实时刷新
    }

    private void MarkDirty() => _dirtyTracker.MarkDirty();

    /// <summary>
    /// 冲突重算防抖（审查修复 2026-08-13）：编辑输入每击键触发 MarkDirty → 全量跨设备
    /// 地址冲突重算（两两比较 O(N²·A)），设备多时输入明显卡顿——200ms 防抖合并连续击键。
    /// 无 Dispatcher 环境（单元测试）同步执行保持原行为。
    /// </summary>
    private System.Windows.Threading.DispatcherTimer? _conflictDebounceTimer;

    private void ScheduleAddressConflictRefresh()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted)
        {
            RefreshAddressConflictFlag();
            return;
        }
        if (_conflictDebounceTimer == null)
        {
            _conflictDebounceTimer = new System.Windows.Threading.DispatcherTimer(
                System.Windows.Threading.DispatcherPriority.Background, dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(200),
            };
            _conflictDebounceTimer.Tick += (_, _) =>
            {
                _conflictDebounceTimer.Stop();
                RefreshAddressConflictFlag();
            };
        }
        _conflictDebounceTimer.Stop();
        _conflictDebounceTimer.Start();
    }

    /// <summary>
    /// IDeviceManagerHost.MarkDirty 的显式实现：供报警/缺陷/计数报警子 VM 回写脏标记。
    /// 路由到私有 MarkDirty，复用 DirtyTracker 抑制逻辑（保存期间子项回填触发的变更不计入）。
    /// </summary>
    void IDeviceManagerHost.MarkDirty() => MarkDirty();

    /// <summary>
    /// 主窗口关闭前的未保存确认：存在未保存修改时弹确认框，
    /// 用户选"是"允许关闭（丢弃修改），选"否"取消关闭。返回 true 表示允许关闭。
    /// 供 MainWindow.Closing 调用，使关闭拦截逻辑可单元测试。
    /// </summary>
    public bool TryCloseWithDirtyCheck()
    {
        if (!IsDirty) return true;
        var result = _dialog.Show(
            Strings.M173,
            Strings.M174,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        return result == MessageBoxResult.Yes;
    }

    /// <summary>
    /// 离开设备管理页前确认未保存修改。确认离开后保留脏状态，返回页面时仍可继续保存。
    /// </summary>
    public bool TryLeaveWithDirtyCheck()
    {
        if (!IsDirty) return true;
        var result = _dialog.Show(
            Strings.M_UnsavedChangesLeave,
            Strings.M174,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return false;
        return true;
    }

    /// <summary>
    /// 解绑所有外部事件订阅，防止 ViewModel 被 DI 容器释放后仍持有
    /// DeviceRepository.Devices/Runtimes 及各 Device/Runtime 的事件引用，
    /// 避免因事件未解绑导致的内存泄漏与僵尸回调。
    /// </summary>
    /// <remarks>
    /// 订阅点：构造函数中订阅 Devices/Runtimes.CollectionChanged 并对已有项调用 AttachDevice/AttachRuntime；
    /// OnDevicesCollectionChanged/OnRuntimesCollectionChanged 在增删时动态 Attach/Detach。
    /// 此处统一解绑所有当前已 Attach 的 Device/Runtime，并取消顶层集合订阅。
    /// </remarks>
    public void Dispose()
    {
        _conflictDebounceTimer?.Stop();
        if (_connectionManager != null)
            _connectionManager.PropertyChanged -= OnConnectionPropertyChanged;
        _deviceRepository.Devices.CollectionChanged -= OnDevicesCollectionChanged;
        _deviceRepository.Runtimes.CollectionChanged -= OnRuntimesCollectionChanged;

        _dirtyTracker.DetachAll(Devices);

        foreach (var rt in _deviceRepository.Runtimes)
            DetachRuntime(rt);

        DeviceList.Dispose();

        // 解绑子 VM 对父级 PropertyChanged 的订阅，避免僵尸回调
        AlarmManagerVm.Detach();
        DefectManagerVm.Detach();
        CounterAlarmManagerVm.Detach();
        WorkOrders.Detach();
        PlcCommands.Detach();
    }
}
