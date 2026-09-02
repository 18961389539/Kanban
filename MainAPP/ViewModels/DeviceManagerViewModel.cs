using System.Collections.Generic;
using MainAPP.Resources;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
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

public partial class DeviceManagerViewModel : ObservableObject, IDeviceManagerHost, IDisposable, INavigationPageLifecycle
{
    private readonly IPlcDataAcquisitionService _dataAcquisitionService;
    private readonly DeviceRepository _deviceRepository;
    private readonly IDialogService _dialog;
    private readonly DeviceConfigIOService _configIO;
    private readonly PlcConnectionManager? _connectionManager;
    private readonly IPlcAddressCodecResolver? _addressCodecResolver;
    private readonly IPlcRuntimeProfileProvider? _profileProvider;
    private readonly UserSession _userSession;
    private readonly IDeviceSetupWizardService? _deviceSetupWizard;
    private DeviceAuditSnapshot _lastSavedDeviceAuditSnapshot = new(0, []);
    private long _configurationRevision;

    // 设备列表由 DeviceRepository（DI 单例）持有，ViewModel 直接引用
    public ObservableCollection<Device> Devices => _deviceRepository.Devices;

    /// <summary>设备列表子 VM（搜索/状态筛选/摘要与过滤视图）。</summary>
    public DeviceListViewModel DeviceList { get; }

    /// <summary>设备 Id 到冲突 PLC 地址的摘要，供列表项显示和点击定位。</summary>
    public IReadOnlyDictionary<string, string> AddressConflictSummaries { get; private set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>设备 Id 到列表首个冲突地址所属 Tab 的映射，供冲突点击定位。</summary>
    public IReadOnlyDictionary<string, int> AddressConflictTabIndices { get; private set; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    /// <summary>最近一次保存校验产生的全部错误。</summary>
    public IReadOnlyList<DeviceConfigError> ValidationErrors { get; private set; } = [];

    /// <summary>设备配置页统一的页面内操作反馈。</summary>
    public OperationFeedback Feedback { get; } = new();

    /// <summary>当前选中设备的配置错误，显示在设备参数表单顶部。</summary>
    public IReadOnlyList<DeviceConfigError> CurrentDeviceValidationErrors =>
        SelectedDevice == null
            ? []
            : ValidationErrors.Where(error => ReferenceEquals(error.Device, SelectedDevice)).ToArray();

    public bool HasCurrentDeviceValidationErrors => CurrentDeviceValidationErrors.Count > 0;

    /// <summary>当前点击的冲突地址，供设备参数表单聚焦对应输入框。</summary>
    [ObservableProperty]
    private string _focusedAddressConflict = string.Empty;

    /// <summary>地址冲突聚焦请求序号；相同地址连续点击时也能触发视图重新聚焦。</summary>
    [ObservableProperty]
    private long _addressConflictFocusRequest;

    /// <summary>报警管理子 VM（报警 CRUD + CSV 导入导出）。</summary>
    public DeviceAlarmManagerViewModel AlarmManagerVm { get; }

    /// <summary>缺陷管理子 VM（缺陷 CRUD）。</summary>
    public DeviceDefectManagerViewModel DefectManagerVm { get; }

    /// <summary>计数报警管理子 VM（计数报警 CRUD + 清空当前值）。</summary>
    public DeviceCounterAlarmManagerViewModel CounterAlarmManagerVm { get; }

    /// <summary>数据采集源管理子 VM（温湿度/能耗等 CRUD，设计稿《采集模块扩展设计方案》）。</summary>
    public DeviceDataSourceManagerViewModel DataSourceManagerVm { get; }

    /// <summary>工单管理子 VM（按设备过滤 + 6 个工单命令）。</summary>
    public DeviceWorkOrderViewModel WorkOrders { get; }

    /// <summary>PLC 命令子 VM（写配方 / OEE 清零 / 读地址 + 状态栏）。</summary>
    public DevicePlcCommandViewModel PlcCommands { get; }

    [ObservableProperty]
    private Device? _selectedDevice;

    /// <summary>切换设备未保存确认的抑制开关：程序内主动切设备（冲突跳转/错误定位/导入恢复）时不弹确认。</summary>
    private bool _suppressSelectionGuard;
    /// <summary>上一次「已通过确认」的设备选择，用于未保存保护回退。</summary>
    private Device? _selectionGuardPrevious;

    /// <summary>单台设备 JSON 导出/导入的序列化选项（与 DeviceRepository 持久化口径一致）。</summary>
    private static readonly JsonSerializerOptions DeviceJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// 离开设备管理页前调用：存在未保存更改时弹二次确认，返回 true 表示允许离开。
    /// 供 MainWindowViewModel.Navigate 在切换离页前拦截（该入口是所有导航的统一汇合点）；
    /// 干净状态直接放行不弹框。权限/Viewer 拒绝的导航不会到达此检查。
    /// </summary>
    public bool MayDiscardUnsavedAndLeave()
    {
        if (!IsDirty) return true;
        var confirm = _dialog.Show(
            Strings.K734,
            Strings.M118, MessageBoxButton.YesNo, MessageBoxImage.Warning);
        return confirm == MessageBoxResult.Yes;
    }

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

    partial void OnIsDirtyChanged(bool value)
    {
        SaveCommand.NotifyCanExecuteChanged();
        if (!IsLoading)
        {
            if (value) Feedback.Warning(Strings.Ux_StatusUnsaved);
            else Feedback.Success(Strings.Ux_StatusSaved);
        }
    }

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
        DataSourceCsvIOService dataSourceCsvIO,
        WorkOrderRepository workOrderRepo,
        IWorkOrderService workOrderService,
        UserSession userSession,
        PlcConnectionManager? connectionManager = null,
        IPlcAddressCodecResolver? addressCodecResolver = null,
        IPlcRuntimeProfileProvider? profileProvider = null,
        IDeviceSetupWizardService? deviceSetupWizard = null)
    {
        _deviceRepository = deviceRepository;
        _dataAcquisitionService = dataAcquisitionService;
        _dialog = dialog;
        _configIO = configIO;
        _userSession = userSession;
        _deviceSetupWizard = deviceSetupWizard;
        _userSession.PropertyChanged += OnUserSessionPropertyChanged;
        _connectionManager = connectionManager;
        _addressCodecResolver = addressCodecResolver;
        _profileProvider = profileProvider;
        _configIO.BackupAvailabilityChanged += OnBackupAvailabilityChanged;
        DeviceList = new DeviceListViewModel(deviceRepository);

        // 构造子 VM（报警/缺陷/计数报警管理），传入各自所需的共享依赖与父级宿主引用。
        // 子 VM 通过 IDeviceManagerHost 订阅 SelectedDevice/IsLoading 变化并回写脏标记，
        // 实现跨 Tab 联动而无需双向引用。CSV IO 服务仅用于构造对应子 VM，父级不再直接持有。
        AlarmManagerVm = new DeviceAlarmManagerViewModel(dialog, alarmCsvIO, dataAcquisitionService, this);
        DefectManagerVm = new DeviceDefectManagerViewModel(dialog, defectCsvIO, this);
        CounterAlarmManagerVm = new DeviceCounterAlarmManagerViewModel(dialog, plcCommands, counterAlarmCsvIO, this);
        DataSourceManagerVm = new DeviceDataSourceManagerViewModel(dialog, this, dataSourceCsvIO);
        WorkOrders = new DeviceWorkOrderViewModel(dialog, this, workOrderRepo, workOrderService, deviceRepository);
        PlcCommands = new DevicePlcCommandViewModel(dialog, this, plcCommands);

        // 订阅设备集合与每个设备的属性/子集合变更，用于维护脏标记
        _dirtyTracker = new DirtyTracker(() =>
        {
            Interlocked.Increment(ref _configurationRevision);
            IsDirty = true;
            ScheduleAddressConflictRefresh();
        });
        _deviceRepository.Devices.CollectionChanged += OnDevicesCollectionChanged;
        foreach (var d in Devices) _dirtyTracker.AttachDevice(d);

        // 订阅运行时集合与每个运行时的状态变更，用于驱动设备列表状态色点实时刷新
        _deviceRepository.Runtimes.CollectionChanged += OnRuntimesCollectionChanged;
        foreach (var rt in _deviceRepository.Runtimes) AttachRuntime(rt);

        _lastSavedDeviceAuditSnapshot = CreateDeviceAuditSnapshot();
        if (_connectionManager != null)
            _connectionManager.PropertyChanged += OnConnectionPropertyChanged;
    }

    private bool _pageActive;

    /// <inheritdoc />
    public void OnPageEnter()
    {
        _pageActive = true;
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (!_pageActive) return;
            RefreshAddressConflictFlag();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <inheritdoc />
    public void OnPageExit()
    {
        _pageActive = false;
        _conflictDebounceTimer?.Stop();
    }

    /// <summary>设备全量配置审计快照（全字段、全设备），委托 <see cref="DeviceAuditService"/> 生成。</summary>
    private DeviceAuditSnapshot CreateDeviceAuditSnapshot() => DeviceAuditService.CreateSnapshot(Devices);

    /// <summary>
    /// 外部整体替换设备列表（Remote 拉取 / 导入 / 恢复备份 / 样本数据）后同步审计基线，
    /// 避免下一次保存把「上一次保存」误记为「替换前状态」。调用点须在替换完成后调用。
    /// </summary>
    public void SyncAuditBaseline() => _lastSavedDeviceAuditSnapshot = CreateDeviceAuditSnapshot();

    [RelayCommand(CanExecute = nameof(CanAddDevice))]
    private void AddDevice()
    {
        if (!CanManageDevices || IsLoading) return;
        var newDevice = _deviceSetupWizard?.Show(Devices.ToArray(), ResolveAddressCodec());
        if (newDevice == null)
        {
            // 测试宿主或未启用窗口服务时保留原有的内存创建路径；生产宿主始终注册向导。
            if (_deviceSetupWizard != null) return;
            var baseName = string.Format(Strings.F135, Devices.Count + 1);
            var newName = EnsureUniqueName(baseName, Devices.Select(d => d.Name));
            newDevice = new Device { Name = newName };
        }

        Devices.Add(newDevice);
        _deviceRepository.AddRuntime(newDevice);
        DeviceList.SearchKeyword = string.Empty;
        SelectedDevice = newDevice;
        SelectedTabIndex = (int)DeviceManagerTab.Parameters;
        MarkDirty();
        if (_deviceSetupWizard != null)
            _dialog.NotifySuccess(string.Format(Strings.Ux_DeviceWizardCreated, newDevice.Name));
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

    // 设备配置变更命令统一要求工程师权限，避免仅依赖页面导航权限。
    private bool CanEditSelected() => SelectedDevice != null && !IsLoading && CanManageDevices;

    // 未注入连接管理器（本地/单机或测试场景）视为未连接，PLC 写命令应禁用，
    // 避免离线时误执行写配方/清零点/OEE 清零等操作（审查修复 2026-08-15）。
    public bool IsPlcConnected => _connectionManager?.IsConnected ?? false;

    public bool CanManageDevices => _userSession.IsEngineerOrAbove;

    public bool IsConfigurationReadOnly => !CanManageDevices;

    private void OnUserSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(UserSession.IsEngineerOrAbove)) return;

        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            _ = dispatcher.InvokeAsync(() => OnUserSessionPropertyChanged(sender, e));
            return;
        }

        OnPropertyChanged(nameof(CanManageDevices));
        OnPropertyChanged(nameof(IsConfigurationReadOnly));
        AddDeviceCommand.NotifyCanExecuteChanged();
        RemoveDeviceCommand.NotifyCanExecuteChanged();
        CopyDeviceCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        ExportConfigCommand.NotifyCanExecuteChanged();
        ImportConfigCommand.NotifyCanExecuteChanged();
        RollbackToBackupCommand.NotifyCanExecuteChanged();
        SeedSampleDevicesCommand.NotifyCanExecuteChanged();
    }

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

    private void OnBackupAvailabilityChanged(object? sender, EventArgs e)
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            _ = dispatcher.InvokeAsync(() => OnBackupAvailabilityChanged(sender, e));
            return;
        }

        RollbackToBackupCommand.NotifyCanExecuteChanged();
    }

    // 只有存在未保存变更时才允许保存；空列表同样可以保存，以持久化删除全部设备。
    private bool CanSave() => IsDirty && !IsLoading && CanManageDevices;

    private bool CanAddDevice() => !IsLoading && CanManageDevices;

    private bool CanImportConfig() => !IsLoading && CanManageDevices;

    private bool CanExportConfig() => !IsLoading && CanManageDevices;

    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private void RemoveDevice(Device? device)
    {
        if (!CanEditSelected()) return;
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
    /// 选中冲突设备并切换到冲突地址所属 Tab，地址输入框由视图根据 FocusedAddressConflict 聚焦。
    /// </summary>
    [RelayCommand]
    private void SelectAddressConflict(Device? device)
    {
        if (device == null) return;
        _suppressSelectionGuard = true;
        SelectedDevice = device;
        _suppressSelectionGuard = false;
        SelectedTabIndex = AddressConflictTabIndices.TryGetValue(device.Id, out var tabIndex)
            ? tabIndex
            : (int)DeviceManagerTab.Parameters;
        var conflictAddress = AddressConflictSummaries.TryGetValue(device.Id, out var summary)
            ? summary.Split(',', StringSplitOptions.TrimEntries)[0]
            : string.Empty;
        FocusedAddressConflict = SelectAddressConflictItem(device, SelectedTabIndex, conflictAddress)
            ?? conflictAddress;
        AddressConflictFocusRequest++;
    }

    private string? SelectAddressConflictItem(Device device, int tabIndex, string address)
    {
        var codec = ResolveAddressCodec();
        var key = codec.CanonicalKey(address);
        bool Matches(string? candidate) => !string.IsNullOrWhiteSpace(candidate)
            && string.Equals(codec.CanonicalKey(candidate), key, StringComparison.OrdinalIgnoreCase);

        switch ((DeviceManagerTab)tabIndex)
        {
            case DeviceManagerTab.Parameters:
                foreach (var candidate in new[]
                {
                    device.OkCountAddress,
                    device.NgCountAddress,
                    device.StatusCountAddress,
                    device.ProductionResetAddress,
                    device.RecipeAddress,
                })
                {
                    if (Matches(candidate)) return candidate;
                }
                return null;
            case DeviceManagerTab.Alarms:
                var alarm = device.Alarms.FirstOrDefault(item => Matches(item.PlcAddress));
                AlarmManagerVm.SelectedAlarm = alarm;
                return alarm?.PlcAddress;
            case DeviceManagerTab.Defects:
                var defect = device.Defects.FirstOrDefault(item => Matches(item.PlcAddress));
                DefectManagerVm.SelectedDefect = defect;
                return defect?.PlcAddress;
            case DeviceManagerTab.CounterAlarms:
                var counterAlarm = device.CounterAlarms.FirstOrDefault(item => Matches(item.PlcAddress));
                CounterAlarmManagerVm.SelectedCounterAlarm = counterAlarm;
                return counterAlarm?.PlcAddress;
            case DeviceManagerTab.Sources:
                var source = device.Sources.FirstOrDefault(item =>
                    Matches(item.TriggerAddress) || item.Values.Any(value => Matches(value.PlcAddress)));
                DataSourceManagerVm.SelectedSource = source;
                var value = source?.Values.FirstOrDefault(item => Matches(item.PlcAddress));
                DataSourceManagerVm.SelectedValue = value;
                return value?.PlcAddress ?? source?.TriggerAddress;
            default:
                return null;
        }
    }

    /// <summary>
    /// 复制选中设备：深拷贝其配置（名称/PLC 地址/配方/目标周期 + 报警/缺陷/计数报警子集合），
    /// 生成新 Id 与唯一名称（源名 + " 副本"），加入设备列表并置脏。
    /// 便于快速搭建结构相似的同类设备，避免逐项手填。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private void CopyDevice()
    {
        if (!CanEditSelected()) return;
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
    private static Device CloneDevice(Device src, string? idOverride = null)
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
        // 副本默认使用新 Id；导入恢复时可指定保留源 Id（须在任何子配置创建前设置，
        // 子集合的 DeviceId 与确定性 Id 生成都依赖此值）
        if (!string.IsNullOrWhiteSpace(idOverride))
            copy.Id = idOverride;

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
        foreach (var source in src.Sources)
        {
            var sourceCopy = new DataSource
            {
                DeviceId = copy.Id,
                Name = source.Name,
                Type = source.Type,
                Enabled = source.Enabled,
                Description = source.Description,
                TriggerAddress = source.TriggerAddress,
                TriggerValue = source.TriggerValue,
                AckValue = source.AckValue,
            };
            foreach (var value in source.Values)
            {
                var valueCopy = new DataSourceValue
                {
                    Name = value.Name,
                    PlcAddress = value.PlcAddress,
                    Unit = value.Unit,
                    Enabled = value.Enabled,
                    DataType = value.DataType,
                    StringLength = value.StringLength,
                    LimitMin = value.LimitMin,
                    LimitMax = value.LimitMax,
                    FloatLimitMin = value.FloatLimitMin,
                    FloatLimitMax = value.FloatLimitMax,
                    Hysteresis = value.Hysteresis,
                    ConfirmSeconds = value.ConfirmSeconds,
                    ExpectedValue = value.ExpectedValue,
                    FloatExpectedValue = value.FloatExpectedValue,
                    BoolExpectedValue = value.BoolExpectedValue,
                    StringExpectedValue = value.StringExpectedValue,
                };
                foreach (var enumValue in value.EnumValues)
                    valueCopy.EnumValues.Add(new DataSourceEnumValue { Value = enumValue.Value, DisplayName = enumValue.DisplayName });
                sourceCopy.Values.Add(valueCopy);
            }
            copy.Sources.Add(sourceCopy);
        }
        return copy;
    }

    /// <summary>
    /// 拖拽排序：将 dragged 设备移动到 target 设备在列表中的位置（位置即持久化顺序）。
    /// 仅排序不增删，不重挂事件订阅；移动后标记脏使顺序变更可被保存。
    /// </summary>
    public void MoveDevice(Device dragged, Device target)
    {
        if (!CanManageDevices || IsLoading || dragged == null || target == null || ReferenceEquals(dragged, target)) return;
        var from = Devices.IndexOf(dragged);
        var to = Devices.IndexOf(target);
        if (from < 0 || to < 0 || from == to) return;
        Devices.Move(from, to);
        MarkDirty();
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task Save()
    {
        if (!CanSave()) return;
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
            Feedback.Error(string.Format(Strings.F067, errors.Count));
            _dialog.NotifyWarning(string.Format(Strings.F067, errors.Count));
            if (_dialog.ShowConfigErrors(errors) is { } selectedError)
                NavigateToError(selectedError);
            return;
        }

        // 防重入：保存期间置 IsLoading（禁用 PLC 写命令 + Save 自身），防止双击/并发触发两次保存
        IsLoading = true;
        Feedback.Working(Strings.Ux_StatusSaving);
        try
        {
            // 前后值摘要：全设备 + 全配置字段（DeviceAuditService.CreateSnapshot）。
            var before = _lastSavedDeviceAuditSnapshot;
            var after = CreateDeviceAuditSnapshot();
            var saveRevision = Interlocked.Read(ref _configurationRevision);

            // SaveAllAsync 会在进入异步等待前创建深拷贝；仅抑制这一段的 DeviceId 回填事件，
            // 远程持久化等待期间仍必须让用户编辑产生新的配置修订号。
            Task persistenceTask;
            _dirtyTracker.IsSuppressed = true;
            try
            {
                persistenceTask = _deviceRepository.SaveAllAsync();
            }
            finally
            {
                _dirtyTracker.IsSuppressed = false;
            }
            await persistenceTask;

            // Remote 保存可能刚刚生成 Collector 的 devices.json.bak，保存成功后立即刷新回滚按钮状态。
            if (_configIO.IsRemote)
                await _configIO.RefreshRemoteBackupAvailabilityAsync();

            // 保存后同步所有设备运行时的 TargetCycle
            foreach (var device in Devices)
                _deviceRepository.SyncTargetCycle(device.Id, device.TargetCycle);

            _lastSavedDeviceAuditSnapshot = after;
            // 非阻断提示：0 值阈值报警（仅记录不触发）仍可正常保存，但提醒用户其不会触发报警
            var zeroThresholdCount = Devices.Sum(d => d.CounterAlarms.Count(c => c.MaxValue <= 0));
            Feedback.Success(zeroThresholdCount > 0
                ? string.Format(Strings.K651, after.Count, zeroThresholdCount)
                : Strings.Ux_StatusSaved);
            _dialog.NotifySuccess(zeroThresholdCount > 0
                ? string.Format(Strings.K651, after.Count, zeroThresholdCount)
                : Strings.M009);
            IsDirty = Interlocked.Read(ref _configurationRevision) != saveRevision;
            AuditLog.Record("Device.Update", "Device", null,
                before: before,
                after: after,
                detail: string.Format(Strings.F_DevicesSaved, after.Count));
        }
        catch (System.Exception ex)
        {
            Feedback.Error(string.Format(Strings.F066, ex.Message));
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
        var codec = ResolveAddressCodec();
        var report = AddressConflictService.Compute(Devices, codec);
        AddressConflictSummaries = report.Summaries;
        AddressConflictTabIndices = report.TargetTabs;
        AddressConflictCount = report.ConflictCount;
        HasAddressConflicts = report.ConflictCount > 0;
        OnPropertyChanged(nameof(AddressConflictSummaries));
        OnPropertyChanged(nameof(AddressConflictTabIndices));
    }

    private IPlcAddressCodec ResolveAddressCodec()
        => _profileProvider?.Current?.AddressCodec
           ?? _addressCodecResolver?.Current
           ?? new MitsubishiAddressCodec();

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
        {
            _suppressSelectionGuard = true;
            SelectedDevice = err.Device;
            _suppressSelectionGuard = false;
        }
        SelectedTabIndex = err.TargetTabIndex;
    }

    // ──────────── 导入 / 导出 / 恢复（委托 DeviceConfigIOService） ────────────

    /// <summary>
    /// 导出当前全部设备配置到用户选择的 JSON 文件（原子写入，备份上一版本）。
    /// 仅导出内存中的配置、不触发持久化或脏标记变化（导出是只读操作）。
    /// 用户取消保存对话框则不写文件。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExportConfig))]
    private void ExportConfig()
    {
        if (!CanExportConfig()) return;
        _configIO.ExportConfig(Devices.Count);
    }

    /// <summary>
    /// 从用户选择的 JSON 文件导入设备配置，整体替换当前内存中的设备（含运行时状态）。
    /// 导入前二次确认（替换会丢弃当前未保存的配置），导入后标记脏并提示用户保存以持久化。
    /// 文件解析失败或文件无设备数据时给出对应提示，不替换。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanImportConfig))]
    private void ImportConfig()
    {
        if (!CanImportConfig()) return;
        var imported = _configIO.ImportConfig(Devices.Count);
        if (imported == null) return;

        _suppressSelectionGuard = true;
        SelectedDevice = Devices.FirstOrDefault();
        _suppressSelectionGuard = false;
        SaveCommand.NotifyCanExecuteChanged();
        RemoveDeviceCommand.NotifyCanExecuteChanged();
        DeviceList.RefreshDeviceList();
        RefreshAddressConflictFlag();
        MarkDirty();
    }

    // ──────────── 全部设备校验（列表头入口，不落盘） ────────────

    private bool CanValidateAll() => !IsLoading && CanManageDevices;

    /// <summary>
    /// 主动触发全量配置校验（不保存）：聚合全部设备校验错误并弹出可定位的错误清单；
    /// 无错误时提示通过数量。与保存时的校验共用同一错误源（DeviceConfigValidator）。
    /// 修复（2026-08-30）：此前校验只在点击保存时触发，用户无法在保存前独立确认配置健康状态。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanValidateAll))]
    private void ValidateAllDevices()
    {
        if (!CanValidateAll()) return;
        var errors = DeviceConfigValidator.CollectValidationErrors(
            Devices,
            _profileProvider?.Current.AddressCodec ?? _addressCodecResolver?.Current);
        ValidationErrors = errors;
        OnPropertyChanged(nameof(ValidationErrors));
        OnPropertyChanged(nameof(CurrentDeviceValidationErrors));
        OnPropertyChanged(nameof(HasCurrentDeviceValidationErrors));

        if (errors.Count == 0)
        {
            var msg = string.Format(Strings.K725, Devices.Count);
            Feedback.Success(msg);
            _dialog.NotifySuccess(msg);
            return;
        }

        Feedback.Error(string.Format(Strings.F067, errors.Count));
        if (_dialog.ShowConfigErrors(errors) is { } selectedError)
            NavigateToError(selectedError);
    }

    // ──────────── 单台设备 JSON 导出 / 导入（备份与恢复单机配置） ────────────

    private bool CanExportDevice() => SelectedDevice != null && !IsLoading && CanManageDevices;

    private bool CanImportDevice() => !IsLoading && CanManageDevices;

    /// <summary>
    /// 导出选中设备（含全部子配置）为独立 JSON 文件，便于单机备份/迁移。
    /// 仅序列化，不触发脏标记或持久化。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExportDevice))]
    private async Task ExportDevice()
    {
        var src = SelectedDevice;
        if (src == null || !CanExportDevice()) return;
        var defaultName = (src.Name ?? "device").Trim();
        foreach (var c in System.IO.Path.GetInvalidFileNameChars()) defaultName = defaultName.Replace(c, '_');
        var path = _dialog.ShowSaveFileDialog(Strings.M226, $"{defaultName}.json", Strings.K695);
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var json = JsonSerializer.Serialize(src, DeviceJsonOptions);
            // P1-8 修复 2026-09-02：写盘移出 UI 线程
            await Task.Run(() => System.IO.File.WriteAllText(path, json));
            _dialog.NotifySuccess(string.Format(Strings.K730, src.Name));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "导出单台设备配置失败");
            _dialog.NotifyError(string.Format(Strings.F090, ex.Message));
        }
    }

    /// <summary>
    /// 从 JSON 文件导入单台设备（新增保留源 Id；与现有设备 Id 冲突或解析失败时拒绝导入）。
    /// 子配置经 CloneDevice 深拷贝并重新挂接 DeviceId，与复制设备共用同一条安全路径。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanImportDevice))]
    private async Task ImportDevice()
    {
        if (!CanImportDevice()) return;
        var path = _dialog.ShowOpenFileDialog(Strings.M227, Strings.K695);
        if (string.IsNullOrEmpty(path)) return;

        Device? imported;
        try
        {
            // P1-8 修复 2026-09-02：读盘移出 UI 线程
            var json = await Task.Run(() => System.IO.File.ReadAllText(path));
            imported = JsonSerializer.Deserialize<Device>(json, DeviceJsonOptions);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "单台设备导入文件解析失败");
            imported = null;
        }

        if (imported == null || string.IsNullOrWhiteSpace(imported.Id))
        {
            _dialog.NotifyError(Strings.K731);
            return;
        }
        if (Devices.Any(d => string.Equals(d.Id, imported.Id, StringComparison.OrdinalIgnoreCase)))
        {
            _dialog.NotifyError(string.Format(Strings.K732, imported.Id));
            return;
        }

        // 保留文件中的源 Id（恢复语义）；重名时追加后缀避免混淆
        var copy = CloneDevice(imported, imported.Id);
        copy.Name = EnsureUniqueName(imported.Name, Devices.Select(d => d.Name));
        Devices.Add(copy);
        _deviceRepository.AddRuntime(copy);
        SelectedDevice = copy;
        MarkDirty();
        RefreshAddressConflictFlag();
        _dialog.NotifySuccess(string.Format(Strings.K733, copy.Name));
    }

    /// <summary>
    /// 恢复上一版本：Local 模式读取本地备份，Remote 模式由 Collector 读取其自有备份并返回权威快照。
    /// Remote 回滚已在 Collector 落盘，成功后保持干净；Local 回滚仍标记脏，要求用户点击保存。
    /// 密码验证在命令体内执行，避免直接 Execute 绕过安全门禁。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRollbackToBackup))]
    private async Task RollbackToBackupAsync()
    {
        if (!CanRollbackToBackup()) return;

        var password = _dialog.ShowPasswordInput(Strings.M119, Strings.M167);
        if (string.IsNullOrEmpty(password))
            return;
        if (!_userSession.VerifyCurrentPassword(password))
        {
            _dialog.NotifyWarning(Strings.M010);
            return;
        }

        // 权限可能在密码对话框期间发生变化，命令体再次检查，避免角色降级后继续覆盖配置。
        if (!CanRollbackToBackup()) return;

        var remote = _configIO.IsRemote;
        IsLoading = true;
        try
        {
            var restored = await _configIO.RollbackToBackupAsync();
            if (restored == null) return;

            _suppressSelectionGuard = true;
            SelectedDevice = Devices.FirstOrDefault();
            _suppressSelectionGuard = false;
            DeviceList.RefreshDeviceList();
            RefreshAddressConflictFlag();
            if (remote)
            {
                SyncAuditBaseline();
                IsDirty = false;
            }
            else
            {
                MarkDirty();
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool CanRollbackToBackup() => _configIO.HasBackup && !IsLoading && CanManageDevices;

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
    [RelayCommand(CanExecute = nameof(CanSeedSampleDevices))]
    private void SeedSampleDevices()
    {
        if (!CanSeedSampleDevices()) return;
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
        RefreshAddressConflictFlag();
        MarkDirty();
        _dialog.NotifySuccess(string.Format(Strings.F114, samples.Count));
    }

    private bool CanSeedSampleDevices() => !IsLoading && CanManageDevices;

    partial void OnSelectedDeviceChanged(Device? value)
    {
        // 切换设备前未保存保护：存在未保存更改时弹确认，拒绝则回退选择
        // 修复（2026-08-30）：配置多、Tab 层级深，误切列表会静默丢弃整页编辑内容。
        if (!_suppressSelectionGuard)
        {
            var previous = _selectionGuardPrevious;
            if (value != null && !ReferenceEquals(value, previous) && IsDirty)
            {
                var confirm = _dialog.Show(
                    Strings.K727,
                    Strings.M118, MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.Yes)
                {
                    // 拒绝切换：回退原选择（置位抑制避免递归触发），本次不更新确认记录
                    _suppressSelectionGuard = true;
                    SelectedDevice = previous;
                    _suppressSelectionGuard = false;
                    return;
                }
            }
            // 已确认或本来允许切换：推进确认记录
            _selectionGuardPrevious = value;
        }

        // 各子 VM（报警/缺陷/计数报警/工单/PLC）通过 DeviceChildManagerViewModel 订阅宿主 PropertyChanged 自动同步 SelectedDevice，无需在此手动赋值。
        OnPropertyChanged(nameof(CurrentDeviceValidationErrors));
        OnPropertyChanged(nameof(HasCurrentDeviceValidationErrors));
        SaveCommand.NotifyCanExecuteChanged();
        AddDeviceCommand.NotifyCanExecuteChanged();
        RemoveDeviceCommand.NotifyCanExecuteChanged();
        CopyDeviceCommand.NotifyCanExecuteChanged();
        ImportConfigCommand.NotifyCanExecuteChanged();
        ExportConfigCommand.NotifyCanExecuteChanged();
        RollbackToBackupCommand.NotifyCanExecuteChanged();
        SeedSampleDevicesCommand.NotifyCanExecuteChanged();
        ValidateAllDevicesCommand.NotifyCanExecuteChanged();
        ExportDeviceCommand.NotifyCanExecuteChanged();
        ImportDeviceCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsLoadingChanged(bool value)
    {
        // IsLoading 变化时刷新设备编辑类命令可用状态；PLC 命令可用状态由 PlcCommands 子 VM 订阅 IsLoading 自行刷新
        SaveCommand.NotifyCanExecuteChanged();
        AddDeviceCommand.NotifyCanExecuteChanged();
        RemoveDeviceCommand.NotifyCanExecuteChanged();
        CopyDeviceCommand.NotifyCanExecuteChanged();
        ImportConfigCommand.NotifyCanExecuteChanged();
        ExportConfigCommand.NotifyCanExecuteChanged();
        RollbackToBackupCommand.NotifyCanExecuteChanged();
        SeedSampleDevicesCommand.NotifyCanExecuteChanged();
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

    private void MarkDirty()
    {
        _dirtyTracker.MarkDirty();
        SaveCommand.NotifyCanExecuteChanged();
    }

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
        _userSession.PropertyChanged -= OnUserSessionPropertyChanged;
        _configIO.BackupAvailabilityChanged -= OnBackupAvailabilityChanged;
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
        DataSourceManagerVm.Detach();
        WorkOrders.Detach();
        PlcCommands.Detach();
    }
}
