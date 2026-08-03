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
using Kanban.Core.Data;
using Kanban.Core.Entities;
using Kanban.Core.Models;
using MainAPP.Models;
using Kanban.Core.Services;
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
    private readonly DevicePlcCommandHandler _plcCommands;
    private readonly WorkOrderRepository _workOrderRepo;
    private readonly IWorkOrderService _workOrderService;
    private readonly PlcConnectionManager? _connectionManager;
    private readonly IPlcAddressCodecResolver? _addressCodecResolver;
    private readonly IPlcRuntimeProfileProvider? _profileProvider;

    // 设备列表由 DeviceRepository（DI 单例）持有，ViewModel 直接引用
    public ObservableCollection<Device> Devices => _deviceRepository.Devices;

    public ICollectionView FilteredDevices { get; }

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

    /// <summary>
    /// 设备列表摘要：总数及各运行状态数量，供标题区快速判断当前产线配置状态。
    /// </summary>
    public string DeviceSummaryText
    {
        get
        {
            var running = 0;
            var alarm = 0;
            var paused = 0;
            var initial = 0;
            foreach (var device in Devices)
            {
                var status = _deviceRepository.RuntimeMap.TryGetValue(device.Id, out var runtime)
                    ? runtime.StatusWord
                    : (int)DeviceStatus.Unknown;
                switch (status)
                {
                    case (int)DeviceStatus.Running:
                        running++;
                        break;
                    case (int)DeviceStatus.Alarm:
                        alarm++;
                        break;
                    case (int)DeviceStatus.Paused:
                        paused++;
                        break;
                    default:
                        initial++;
                        break;
                }
            }

            return string.Format(Strings.F023, Devices.Count, running, alarm, paused, initial);
        }
    }

    /// <summary>报警管理子 VM（报警 CRUD + CSV 导入导出）。</summary>
    public DeviceAlarmManagerViewModel AlarmManagerVm { get; }

    /// <summary>缺陷管理子 VM（缺陷 CRUD）。</summary>
    public DeviceDefectManagerViewModel DefectManagerVm { get; }

    /// <summary>计数报警管理子 VM（计数报警 CRUD + 清空当前值）。</summary>
    public DeviceCountAlarmManagerViewModel CountAlarmManagerVm { get; }

    [ObservableProperty]
    private string _searchKeyword = string.Empty;

    [ObservableProperty]
    private Device? _selectedDevice;

    /// <summary>
    /// 当前选中设备的工单过滤视图（按 SelectedDevice.DeviceId 过滤，Running 优先排序）。
    /// 绑定到设备管理页"工单"Tab 的列表。
    /// </summary>
    public ICollectionView SelectedDeviceWorkOrders { get; }

    /// <summary>工单 Tab 中当前选中的工单。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditWorkOrderCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteWorkOrderCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartWorkOrderCommand))]
    [NotifyCanExecuteChangedFor(nameof(CompleteWorkOrderCommand))]
    [NotifyCanExecuteChangedFor(nameof(AbortWorkOrderCommand))]
    private WorkOrder? _selectedWorkOrder;

    /// <summary>
    /// PLC 写入中标志：写入配方 / 清空计数报警当前值期间为 true，UI 显示加载覆盖层。
    /// UI 自动化测试可监听此属性（或 ByName("加载中")）等待异步操作完成。
    /// </summary>
    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _plcOperationStatus = string.Empty;

    [ObservableProperty]
    private string _plcOperationStatusType = "None";

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
    /// 设备状态筛选条件。与搜索关键字叠加生效；变更后实时刷新列表（含按状态筛选时同步过滤）。
    /// </summary>
    [ObservableProperty]
    private DeviceStatusFilter _statusFilter = DeviceStatusFilter.All;

    /// <summary>
    /// 状态筛选下拉选项（全部 / 运行 / 报警 / 待机 / 离线）。
    /// </summary>
    public IReadOnlyList<StatusFilterOption> StatusFilterOptions { get; } = [
        new() { Value = DeviceStatusFilter.All, Label = Strings.M040 },
        new() { Value = DeviceStatusFilter.Running, Label = "运行" },
        new() { Value = DeviceStatusFilter.Alarm, Label = "报警" },
        new() { Value = DeviceStatusFilter.Paused, Label = "待机" },
        new() { Value = DeviceStatusFilter.Offline, Label = "初始/未连接" },
    ];

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

    // 保存过程中临时抑制脏标记（SaveAll 会回填子项 DeviceId 触发属性变更）
    private bool _suppressDirty;

    /// <summary>
    /// 运行时字段（仅由采集线程写入、不持久化），其 PropertyChanged 不计入未保存标记。
    /// </summary>
    private static readonly HashSet<string> RuntimeProperties = new(StringComparer.Ordinal)
    {
        nameof(Alarm.StartTime),
        nameof(Alarm.EndTime),
        nameof(Defect.Count),
        nameof(CountAlarm.CurrentValue),
        nameof(CountAlarm.IsTriggered),
    };

    public DeviceManagerViewModel(
        DeviceRepository deviceRepository,
        IPlcDataAcquisitionService dataAcquisitionService,
        IDialogService dialog,
        DeviceConfigIOService configIO,
        DevicePlcCommandHandler plcCommands,
        AlarmCsvIOService alarmCsvIO,
        WorkOrderRepository workOrderRepo,
        IWorkOrderService workOrderService,
        PlcConnectionManager? connectionManager = null,
        IPlcAddressCodecResolver? addressCodecResolver = null,
        IPlcRuntimeProfileProvider? profileProvider = null)
    {
        _deviceRepository = deviceRepository;
        _dataAcquisitionService = dataAcquisitionService;
        _dialog = dialog;
        _configIO = configIO;
        _plcCommands = plcCommands;
        _workOrderRepo = workOrderRepo;
        _workOrderService = workOrderService;
        _connectionManager = connectionManager;
        _addressCodecResolver = addressCodecResolver;
        _profileProvider = profileProvider;
        FilteredDevices = CollectionViewSource.GetDefaultView(Devices);

        // 构造子 VM（报警/缺陷/计数报警管理），传入各自所需的共享依赖与父级宿主引用。
        // 子 VM 通过 IDeviceManagerHost 订阅 SelectedDevice/IsLoading 变化并回写脏标记，
        // 实现跨 Tab 联动而无需双向引用。alarmCsvIO 仅用于构造报警子 VM，父级不再直接持有。
        AlarmManagerVm = new DeviceAlarmManagerViewModel(dialog, alarmCsvIO, dataAcquisitionService, this);
        DefectManagerVm = new DeviceDefectManagerViewModel(this);
        CountAlarmManagerVm = new DeviceCountAlarmManagerViewModel(dialog, plcCommands, this);

        // 当前设备工单过滤视图：按 SelectedDevice.DeviceId 过滤，Running 优先排序
        // 使用独立的 ListCollectionView（不能用 GetDefaultView，否则与 WorkOrderManagerViewModel 共享同一视图导致 Filter 互相覆盖）
        SelectedDeviceWorkOrders = new ListCollectionView(_workOrderRepo.WorkOrders);
        SelectedDeviceWorkOrders.Filter = FilterWorkOrderByDevice;
        SelectedDeviceWorkOrders.SortDescriptions.Add(
            new SortDescription(nameof(WorkOrder.Status), ListSortDirection.Descending));

        // 订阅设备集合与每个设备的属性/子集合变更，用于维护脏标记
        _deviceRepository.Devices.CollectionChanged += OnDevicesCollectionChanged;
        foreach (var d in Devices) AttachDevice(d);

        // 订阅运行时集合与每个运行时的状态变更，用于驱动设备列表状态色点实时刷新
        _deviceRepository.Runtimes.CollectionChanged += OnRuntimesCollectionChanged;
        foreach (var rt in _deviceRepository.Runtimes) AttachRuntime(rt);

        // 初始计算跨设备地址冲突标记（LoadAll 已在 ViewModel 构造前完成）
        RefreshAddressConflictFlag();
        if (_connectionManager != null)
            _connectionManager.PropertyChanged += OnConnectionPropertyChanged;
    }

    /// <summary>
    /// 强制刷新设备列表视图（LoadAll 后 CollectedView 可能未立即响应 Reset + Add 序列）
    /// </summary>
    public void RefreshDeviceList()
    {
        FilteredDevices.Refresh();
        Log.Information("DeviceManagerViewModel.RefreshDeviceList：Devices.Count={Count}, FilteredDevices.Filter={Filter}",
            Devices.Count, FilteredDevices.Filter == null ? "null" : "set");
    }

    partial void OnSearchKeywordChanged(string value) => ApplyFilter();

    partial void OnStatusFilterChanged(DeviceStatusFilter value) => ApplyFilter();

    /// <summary>
    /// 组合「搜索关键字 + 状态筛选」应用过滤。两者为空/全部时清空过滤（null）以保证所有项可见。
    /// </summary>
    private void ApplyFilter()
    {
        var keyword = (SearchKeyword ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(keyword) && StatusFilter == DeviceStatusFilter.All)
        {
            FilteredDevices.Filter = null;
            return;
        }

        var filter = StatusFilter;
        FilteredDevices.Filter = item => item is Device d
            && (string.IsNullOrEmpty(keyword) || DeviceMatchesKeyword(d, keyword))
            && StatusMatches(d, filter);
    }

    /// <summary>
    /// 搜索关键字是否命中设备：匹配设备名、各 PLC 地址、报警名/地址、缺陷名、计数报警名/地址（不区分大小写）。
    /// 便于在设备较多时按地址或报警名快速定位设备。
    /// </summary>
    private static bool DeviceMatchesKeyword(Device d, string keyword)
    {
        if ((d.Name ?? string.Empty).Contains(keyword, System.StringComparison.OrdinalIgnoreCase)) return true;
        if ((d.OkCountAddress ?? string.Empty).Contains(keyword, System.StringComparison.OrdinalIgnoreCase)) return true;
        if ((d.NgCountAddress ?? string.Empty).Contains(keyword, System.StringComparison.OrdinalIgnoreCase)) return true;
        if ((d.StatusCountAddress ?? string.Empty).Contains(keyword, System.StringComparison.OrdinalIgnoreCase)) return true;
        if ((d.ProductionResetAddress ?? string.Empty).Contains(keyword, System.StringComparison.OrdinalIgnoreCase)) return true;
        if ((d.RecipeAddress ?? string.Empty).Contains(keyword, System.StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var a in d.Alarms)
        {
            if ((a.Name ?? string.Empty).Contains(keyword, System.StringComparison.OrdinalIgnoreCase)) return true;
            if ((a.PlcAddress ?? string.Empty).Contains(keyword, System.StringComparison.OrdinalIgnoreCase)) return true;
        }
        foreach (var def in d.Defects)
            if ((def.Name ?? string.Empty).Contains(keyword, System.StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var c in d.CountAlarms)
        {
            if ((c.Name ?? string.Empty).Contains(keyword, System.StringComparison.OrdinalIgnoreCase)) return true;
            if ((c.PlcAddress ?? string.Empty).Contains(keyword, System.StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private bool StatusMatches(Device d, DeviceStatusFilter filter)
    {
        if (filter == DeviceStatusFilter.All) return true;
        int status = (int)DeviceStatus.Unknown;
        if (_deviceRepository.RuntimeMap.TryGetValue(d.Id, out var rt))
            status = rt.StatusWord;
        return filter switch
        {
            DeviceStatusFilter.Running => status == (int)DeviceStatus.Running,
            DeviceStatusFilter.Alarm => status == (int)DeviceStatus.Alarm,
            DeviceStatusFilter.Paused => status == (int)DeviceStatus.Paused,
            DeviceStatusFilter.Offline => status == (int)DeviceStatus.Unknown,
            _ => true,
        };
    }

    [RelayCommand]
    private void AddDevice()
    {
        // 保证新设备名唯一：若默认名冲突则追加数字后缀
        var baseName = string.Format(Strings.F135, Devices.Count + 1);
        var newName = EnsureUniqueName(baseName, Devices.Select(d => d.Name));
        var newDevice = new Device { Name = newName };
        Devices.Add(newDevice);
        _deviceRepository.AddRuntime(newDevice);
        SearchKeyword = string.Empty;
        SelectedDevice = newDevice;
        MarkDirty();
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

    /// <summary>
    /// PLC 写入/读取类命令的可用性：选中设备且不在加载中。
    /// IsLoading 期间禁用可避免并发写入与 UI 重入。
    /// </summary>
    private bool CanExecutePlcWrite() => SelectedDevice != null
        && !IsLoading
        && (_connectionManager?.IsConnected ?? true);

    public bool IsPlcConnected => _connectionManager?.IsConnected ?? true;

    private void OnConnectionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PlcConnectionManager.IsConnected)) return;

        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            _ = dispatcher.InvokeAsync(() => OnConnectionPropertyChanged(sender, e));
            return;
        }

        OnPropertyChanged(nameof(IsPlcConnected));
        WriteRecipeCommand.NotifyCanExecuteChanged();
        ResetProductionCommand.NotifyCanExecuteChanged();
        ReadPlcValueCommand.NotifyCanExecuteChanged();
    }

    // 保存按钮的启用条件：至少有一个设备。
    // 不依赖 SelectedDevice，避免删除选中设备后 SelectedDevice=null 导致 Save 按钮变灰无法持久化删除操作
    private bool CanSave() => Devices.Count > 0;

    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private void RemoveDevice(Device? device)
    {
        var target = device ?? SelectedDevice;
        if (target == null) return;

        // 使用 HC MessageBox（深色主题）进行 YesNo 确认，返回 MessageBoxResult 与原 API 一致。
        var result = _dialog.Show(
            string.Format(Strings.F175, target.Name),
            "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
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
        foreach (var c in src.CountAlarms)
        {
            copy.CountAlarms.Add(new CountAlarm
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

        try
        {
            // 保存内部会回填子项 DeviceId（触发属性变更），临时抑制脏标记避免自我触发。
            // Remote 模式下 SaveAllAsync 经 SignalR 推给 Collector 落盘（异步，不阻塞 UI 线程）
            _suppressDirty = true;
            await _deviceRepository.SaveAllAsync();

            // 保存后同步所有设备运行时的 TargetCycle
            foreach (var device in Devices)
                _deviceRepository.SyncTargetCycle(device.Id, device.TargetCycle);

            _dialog.NotifySuccess(Strings.M009);
            IsDirty = false;
        }
        catch (System.Exception ex)
        {
            _dialog.NotifyError(string.Format(Strings.F066, ex.Message));
        }
        finally
        {
            _suppressDirty = false;
        }
    }

    /// <summary>
    /// 重算跨设备地址冲突标记（实时）。在 MarkDirty 与构造时调用，驱动列表标题区的冲突警告标记。
    /// </summary>
    private void RefreshAddressConflictFlag()
    {
        var conflicts = DeviceConfigValidator.CollectCrossDeviceConflicts(Devices);
        var summaries = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var conflict in conflicts)
        {
            var address = ExtractConflictAddress(conflict.Message);
            if (string.IsNullOrEmpty(address)) continue;
            var devicesWithAddress = Devices.Where(d => DeviceConfigValidator.GetDeviceAddresses(d)
                .Any(a => string.Equals(a?.Trim(), address, StringComparison.OrdinalIgnoreCase)));
            foreach (var device in devicesWithAddress)
            {
                if (!summaries.TryGetValue(device.Id, out var addresses))
                {
                    addresses = [];
                    summaries[device.Id] = addresses;
                }
                if (!addresses.Contains(address, StringComparer.OrdinalIgnoreCase))
                    addresses.Add(address);
            }
        }
        AddressConflictSummaries = summaries.ToDictionary(
            pair => pair.Key,
            pair => string.Join(", ", pair.Value),
            StringComparer.OrdinalIgnoreCase);
        AddressConflictCount = conflicts.Count;
        HasAddressConflicts = conflicts.Count > 0;
        OnPropertyChanged(nameof(AddressConflictSummaries));
    }

    [RelayCommand]
    private void FocusValidationError(DeviceConfigError? error)
    {
        if (error == null) return;
        NavigateToError(error);
    }

    private static string ExtractConflictAddress(string message)
    {
        const string prefix = "地址冲突「";
        var start = message.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0) return string.Empty;
        start += prefix.Length;
        var end = message.IndexOf('」', start);
        return end > start ? message[start..end] : string.Empty;
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
        RefreshDeviceList();
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
        // 安全验证：恢复上一版本会覆盖当前未保存的设备配置，需密码确认防止误触
        const string expectedPassword = "123456";
        var password = _dialog.ShowPasswordInput(Strings.M001, "恢复上一版本将覆盖当前未保存的设备配置，请输入密码以继续：");
        if (password != expectedPassword)
        {
            _dialog.NotifyWarning(Strings.M010);
            return;
        }

        var restored = _configIO.RollbackToBackup();
        if (restored == null) return;

        SelectedDevice = Devices.FirstOrDefault();
        SaveCommand.NotifyCanExecuteChanged();
        RemoveDeviceCommand.NotifyCanExecuteChanged();
        RefreshDeviceList();
        RefreshAddressConflictFlag();
        MarkDirty();
    }

    private bool CanRollbackToBackup() => _configIO.HasBackup;

    /// <summary>
    /// 是否为 DEBUG 编译版本。绑定到"生成虚拟设备"按钮的 Visibility，
    /// 避免发布版暴露虚拟数据生成功能。Release 编译时按钮折叠。
    /// </summary>
    public bool IsDebugBuild
    {
        get
        {
#if DEBUG
            return true;
#else
            return false;
#endif
        }
    }

    /// <summary>
    /// 生成 20 台虚拟设备用于 UI 预览/调试。覆盖 4 个 Tab 的所有字段：
    /// 设备参数（PLC 地址 + 配方）、报警管理（不同级别）、缺陷管理（不同严重等级 + 类别）、
    /// 计数报警（不同单位 + 阈值）。所有 PLC 地址跨设备唯一（由 SampleDeviceBuilder 内部校验），
    /// 避免冲突告警干扰预览。若当前已有设备，提示是否替换；点击保存后才会写入磁盘。
    /// </summary>
    [RelayCommand]
    private void SeedSampleDevices()
    {
        // 密码确认：防止误触生成虚拟数据
        const string expectedPassword = "123456";
        var password = _dialog.ShowPasswordInput(Strings.M001, "请输入密码以生成虚拟设备：");
        if (password != expectedPassword)
            return;

        if (Devices.Count > 0)
        {
            var confirm = _dialog.Show(
                string.Format(Strings.F092, Devices.Count),
                "生成虚拟设备", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;
        }

        var samples = SampleDeviceBuilder.BuildSampleDevices();
        _deviceRepository.ReplaceAll(samples);
        SelectedDevice = Devices.FirstOrDefault();
        SaveCommand.NotifyCanExecuteChanged();
        RemoveDeviceCommand.NotifyCanExecuteChanged();
        RefreshDeviceList();
        MarkDirty();
        _dialog.NotifySuccess(string.Format(Strings.F114, samples.Count));
    }

    partial void OnSelectedDeviceChanged(Device? value)
    {
        // 切换设备时清空工单选中（报警/缺陷/计数报警选中由各子 VM 订阅本属性变化自行清空）
        AlarmManagerVm.SelectedDevice = value;
        DefectManagerVm.SelectedDevice = value;
        CountAlarmManagerVm.SelectedDevice = value;
        SelectedWorkOrder = null;
        OnPropertyChanged(nameof(CurrentDeviceValidationErrors));
        OnPropertyChanged(nameof(HasCurrentDeviceValidationErrors));
        // 刷新当前设备工单过滤视图
        SelectedDeviceWorkOrders.Refresh();
        // 选中设备变化时刷新依赖 SelectedDevice 的命令可用状态（报警/缺陷/计数报警命令在各子 VM 内刷新）
        SaveCommand.NotifyCanExecuteChanged();
        RemoveDeviceCommand.NotifyCanExecuteChanged();
        WriteRecipeCommand.NotifyCanExecuteChanged();
        ResetProductionCommand.NotifyCanExecuteChanged();
        ReadPlcValueCommand.NotifyCanExecuteChanged();
        AddWorkOrderCommand.NotifyCanExecuteChanged();
    }

    /// <summary>工单过滤：仅显示当前选中设备的工单。</summary>
    private bool FilterWorkOrderByDevice(object obj)
        => obj is WorkOrder w && SelectedDevice != null && w.DeviceId == SelectedDevice.Id;

    partial void OnIsLoadingChanged(bool value)
    {
        // IsLoading 变化时刷新 PLC 写入类与设备编辑类命令可用状态，避免并发写入或删除
        RemoveDeviceCommand.NotifyCanExecuteChanged();
        WriteRecipeCommand.NotifyCanExecuteChanged();
        ResetProductionCommand.NotifyCanExecuteChanged();
        ReadPlcValueCommand.NotifyCanExecuteChanged();
        if (value)
        {
            PlcOperationStatus = "正在执行 PLC 操作...";
            PlcOperationStatusType = "Progress";
        }
        // 报警 CSV 导入/导出与计数报警清空命令由各子 VM 订阅 IsLoading 变化自行刷新
    }

    [ObservableProperty]
    private string _recipeStatus = string.Empty;

    [RelayCommand(CanExecute = nameof(CanExecutePlcWrite))]
    private async Task WriteRecipeAsync()
    {
        if (SelectedDevice == null) return;

        IsLoading = true;
        try
        {
            var result = await _plcCommands.WriteRecipeAsync(SelectedDevice);
            RecipeStatus = result.Status switch
            {
                PlcOpStatus.Success => string.Format(Strings.F072, System.DateTime.Now),
                PlcOpStatus.Info => result.Message,        // 未配置配方地址等跳过提示
                PlcOpStatus.Warning => string.Format(Strings.F197, result.Message),
                PlcOpStatus.Error => string.Format(Strings.F237, result.Message),
                _ => result.Message,
            };
            SetPlcOperationStatus(result);

            // 交互策略：Success/Info 仅更新 RecipeStatus（不打断用户）；Warning/Error 才弹 Growl 通知
            if (result.Status == PlcOpStatus.Warning || result.Status == PlcOpStatus.Error)
                NotifyPlcResult(result);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// 手动触发选中设备的 OEE 清零：触发 PLC 清零 + 同步软件侧 OEE 累计清零（产量+时间+报警）+ 基线清零窗口。
    /// 与班次切换自动触发的清零逻辑一致，此处仅做手动即时触发且只作用于选中设备。
    /// 该操作会清零当前产量/时间/报警累计且不可撤销，属危险写操作，需二次确认。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExecutePlcWrite))]
    private async Task ResetProductionAsync()
    {
        if (SelectedDevice == null) return;

        IsLoading = true;
        try
        {
            var result = await _plcCommands.ResetProductionAsync(
                SelectedDevice,
                device => _dialog.Show(
                    string.Format(Strings.F176, device.Name),
                    "确认 OEE 清零", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes);

            // Cancelled = 用户拒绝确认，不弹通知；其他状态照常通知
            if (result.Status != PlcOpStatus.Cancelled)
            {
                SetPlcOperationStatus(result);
                NotifyPlcResult(result);
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// 从 PLC 读取指定地址的当前值（D 字地址），用于调试/验证地址配置是否正确。
    /// CommandParameter 为 PLC 地址字符串。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExecutePlcWrite))]
    private async Task ReadPlcValueAsync(string? address)
    {
        IsLoading = true;
        try
        {
            var result = await _plcCommands.ReadPlcValueAsync(address);
            SetPlcOperationStatus(result);
            NotifyPlcResult(result);
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ──────────── 报警 / 缺陷 / 计数报警管理 ────────────
    // 上述三个 Tab 的 CRUD、CSV 导入导出与清空当前值（PLC 写）已拆分到子 VM：
    //   · AlarmManagerVm       —— 报警 CRUD + CSV 导入导出
    //   · DefectManagerVm      —— 缺陷 CRUD
    //   · CountAlarmManagerVm  —— 计数报警 CRUD + 清空当前值
    // 子 VM 通过 IDeviceManagerHost 订阅 SelectedDevice/IsLoading 变化并回写脏标记，
    // 各 Tab 的 XAML 绑定路径不变（DataContext 由 DeviceManagerView 指向各子 VM）。

    // ──────────── 工单管理（设备维度） ────────────
    // 业务逻辑（弹窗、状态机校验、二次确认、落库）已抽取到 IWorkOrderService，
    // 本区域仅负责命令转发与 SelectedWorkOrder 同步。

    /// <summary>新增工单按钮可用性：选中设备即可新增（预填当前设备）。</summary>
    private bool CanAddWorkOrder() => SelectedDevice != null;

    /// <summary>
    /// 新增工单：以当前选中设备预填模板打开编辑对话框（Id=0 表示新增）。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAddWorkOrder))]
    private async Task AddWorkOrder()
    {
        if (SelectedDevice == null) return;
        // 预填当前设备的模板（Id=0 → 对话框显示"新增工单"标题）
        var template = new WorkOrder
        {
            DeviceId = SelectedDevice.Id,
            DeviceName = SelectedDevice.Name,
        };
        var saved = await _workOrderService.AddWorkOrderAsync(template);
        if (saved != null) SelectedWorkOrder = saved;
    }

    [RelayCommand(CanExecute = nameof(CanEditWorkOrder))]
    private async Task EditWorkOrder()
    {
        if (SelectedWorkOrder == null) return;
        var saved = await _workOrderService.EditWorkOrderAsync(SelectedWorkOrder);
        if (saved != null) SelectedWorkOrder = saved;
    }

    private bool CanEditWorkOrder() => SelectedWorkOrder != null;

    [RelayCommand(CanExecute = nameof(CanDeleteWorkOrder))]
    private async Task DeleteWorkOrder()
    {
        if (SelectedWorkOrder == null) return;
        if (await _workOrderService.DeleteWorkOrderAsync(SelectedWorkOrder))
            SelectedWorkOrder = null;
    }

    private bool CanDeleteWorkOrder() => SelectedWorkOrder != null;

    [RelayCommand(CanExecute = nameof(CanStartWorkOrder))]
    private async Task StartWorkOrder()
    {
        if (SelectedWorkOrder == null) return;
        var saved = await _workOrderService.StartWorkOrderAsync(SelectedWorkOrder);
        if (saved != null) SelectedWorkOrder = saved;
    }

    private bool CanStartWorkOrder() => SelectedWorkOrder != null && SelectedWorkOrder.Status == WorkOrderStatus.Pending;

    [RelayCommand(CanExecute = nameof(CanCompleteWorkOrder))]
    private async Task CompleteWorkOrder()
    {
        if (SelectedWorkOrder == null) return;
        var saved = await _workOrderService.CompleteWorkOrderAsync(SelectedWorkOrder);
        if (saved != null) SelectedWorkOrder = saved;
    }

    private bool CanCompleteWorkOrder() => SelectedWorkOrder != null && SelectedWorkOrder.Status == WorkOrderStatus.Running;

    [RelayCommand(CanExecute = nameof(CanAbortWorkOrder))]
    private async Task AbortWorkOrder()
    {
        if (SelectedWorkOrder == null) return;
        var saved = await _workOrderService.AbortWorkOrderAsync(SelectedWorkOrder);
        if (saved != null) SelectedWorkOrder = saved;
    }

    private bool CanAbortWorkOrder() => SelectedWorkOrder != null
        && (SelectedWorkOrder.Status == WorkOrderStatus.Running || SelectedWorkOrder.Status == WorkOrderStatus.Pending);

    /// <summary>
    /// 将 PLC 命令结果按 Status 映射到对应级别的通知（Success→Growl.Success / Info→Info /
    /// Warning→Warning / Error→Error）。Cancelled 由调用方过滤，不应传入此方法。
    /// </summary>
    private void NotifyPlcResult(PlcOpResult result)
    {
        switch (result.Status)
        {
            case PlcOpStatus.Success:
                _dialog.NotifySuccess(result.Message);
                break;
            case PlcOpStatus.Info:
                _dialog.NotifyInfo(result.Message);
                break;
            case PlcOpStatus.Warning:
                _dialog.NotifyWarning(result.Message);
                break;
            case PlcOpStatus.Error:
                _dialog.NotifyError(result.Message);
                break;
            case PlcOpStatus.Cancelled:
                // 调用方应已过滤；不弹通知
                break;
        }
    }

    private void SetPlcOperationStatus(PlcOpResult result)
    {
        PlcOperationStatus = result.Status switch
        {
            PlcOpStatus.Success => string.Format(Strings.F125, result.Message),
            PlcOpStatus.Info => result.Message,
            PlcOpStatus.Warning => string.Format(Strings.F197, result.Message),
            PlcOpStatus.Error => string.Format(Strings.F087, result.Message),
            PlcOpStatus.Cancelled => "已取消",
            _ => result.Message,
        };
        PlcOperationStatusType = result.Status switch
        {
            PlcOpStatus.Success => "Success",
            PlcOpStatus.Warning => "Warning",
            PlcOpStatus.Error => "Error",
            PlcOpStatus.Cancelled => "None",
            _ => "Info",
        };
    }

    public void ReportPlcOperation(PlcOpResult result) => SetPlcOperationStatus(result);

    // ──────────── 脏标记维护 ────────────

    private void OnDevicesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 仅排序（Move）不增删设备，事件订阅保持不变，无需重挂
        if (e.Action == NotifyCollectionChangedAction.Move) return;

        if (e.NewItems != null)
            foreach (Device d in e.NewItems) AttachDevice(d);
        if (e.OldItems != null)
            foreach (Device d in e.OldItems) DetachDevice(d);
        // 不在此 MarkDirty：避免启动时 LoadAll 的批量 Add 误报未保存；
        // 集合增删的脏标记由各增删命令显式调用 MarkDirty()。
    }

    private void AttachDevice(Device d)
    {
        d.PropertyChanged += OnDevicePropertyChanged;
        d.Alarms.CollectionChanged += OnChildCollectionChanged;
        d.Defects.CollectionChanged += OnChildCollectionChanged;
        d.CountAlarms.CollectionChanged += OnChildCollectionChanged;
        foreach (var a in d.Alarms) a.PropertyChanged += OnChildItemPropertyChanged;
        foreach (var def in d.Defects) def.PropertyChanged += OnChildItemPropertyChanged;
        foreach (var c in d.CountAlarms) c.PropertyChanged += OnChildItemPropertyChanged;
    }

    private void DetachDevice(Device d)
    {
        d.PropertyChanged -= OnDevicePropertyChanged;
        d.Alarms.CollectionChanged -= OnChildCollectionChanged;
        d.Defects.CollectionChanged -= OnChildCollectionChanged;
        d.CountAlarms.CollectionChanged -= OnChildCollectionChanged;
        foreach (var a in d.Alarms) a.PropertyChanged -= OnChildItemPropertyChanged;
        foreach (var def in d.Defects) def.PropertyChanged -= OnChildItemPropertyChanged;
        foreach (var c in d.CountAlarms) c.PropertyChanged -= OnChildItemPropertyChanged;
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
        OnPropertyChanged(nameof(DeviceSummaryText));
    }

    private void AttachRuntime(DeviceRuntime rt) => rt.PropertyChanged += OnRuntimePropertyChanged;

    private void DetachRuntime(DeviceRuntime rt) => rt.PropertyChanged -= OnRuntimePropertyChanged;

    private void OnRuntimePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 仅状态字变化才驱动刷新（采集线程每轮轮询；稳态下 CommunityToolkit 不会重复抛事件）
        if (e.PropertyName != nameof(DeviceRuntime.StatusWord)) return;
        OnPropertyChanged(nameof(DeviceRuntimeMap)); // 列表色点实时刷新
        OnPropertyChanged(nameof(DeviceSummaryText));
        // 若正在按状态筛选，状态变化需同步过滤结果；CollectionView.Refresh 必须在 UI 线程执行
        if (StatusFilter != DeviceStatusFilter.All)
        {
            var view = FilteredDevices;
            var app = Application.Current;
            if (app == null || app.Dispatcher.HasShutdownStarted) return;
            if (app.Dispatcher.CheckAccess())
                view.Refresh();
            else
                // BeginInvoke 避免阻塞采集后台线程：Invoke 同步等待 UI 线程执行 Refresh，
                // 高频状态变更时会让采集线程被 UI 排队任务卡住，影响轮询节拍稳定性。
                app.Dispatcher.BeginInvoke(new Action(view.Refresh));
        }
    }

    private void OnDevicePropertyChanged(object? sender, PropertyChangedEventArgs e) => MarkDirty();

    private void OnChildCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (var item in e.NewItems)
                if (item is INotifyPropertyChanged np) np.PropertyChanged += OnChildItemPropertyChanged;
        if (e.OldItems != null)
            foreach (var item in e.OldItems)
                if (item is INotifyPropertyChanged np) np.PropertyChanged -= OnChildItemPropertyChanged;
        // 集合增删的脏标记由对应命令显式标记，此处仅维护事件订阅
    }

    private void OnChildItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 运行时字段（计数/当前值等）由采集线程写入，不计入未保存标记
        if (e.PropertyName != null && RuntimeProperties.Contains(e.PropertyName))
            return;
        MarkDirty();
    }

    private void MarkDirty()
    {
        if (_suppressDirty) return;
        IsDirty = true;
        RefreshAddressConflictFlag();
    }

    /// <summary>
    /// IDeviceManagerHost.MarkDirty 的显式实现：供报警/缺陷/计数报警子 VM 回写脏标记。
    /// 路由到私有 MarkDirty，复用 _suppressDirty 抑制逻辑（保存期间子项回填触发的变更不计入）。
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
            "设备配置有未保存的修改，确定退出吗？\n未保存的修改将在退出后丢失。",
            "未保存的修改",
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
            "设备配置有未保存的修改，确定离开设备管理页吗？\n修改会保留在当前会话中，返回设备管理页后仍可继续保存。",
            "未保存的修改",
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
        if (_connectionManager != null)
            _connectionManager.PropertyChanged -= OnConnectionPropertyChanged;
        _deviceRepository.Devices.CollectionChanged -= OnDevicesCollectionChanged;
        _deviceRepository.Runtimes.CollectionChanged -= OnRuntimesCollectionChanged;

        foreach (var d in Devices)
            DetachDevice(d);

        foreach (var rt in _deviceRepository.Runtimes)
            DetachRuntime(rt);

        // 解绑子 VM 对父级 PropertyChanged 的订阅，避免僵尸回调
        AlarmManagerVm.Detach();
        DefectManagerVm.Detach();
        CountAlarmManagerVm.Detach();
    }
}
