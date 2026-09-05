using System;
using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using Kanban.Collector.Core.Entities;
using MainAPP.Resources;

namespace MainAPP.Views;

/// <summary>
/// 工单编辑对话框。参照 <see cref="PasswordInputDialog"/> 模式：
/// 构造注入 template（null=新增），ShowDialog 返回 true 时通过 <see cref="Result"/> 获取结果。
///
/// 校验（易用性 P1-7 改造）：原先只在点「确认」后逐条弹 Growl，一次只暴露一个问题，
/// 用户要反复点确认才能填完一张单。现改为实现 <see cref="INotifyDataErrorInfo"/>：
/// 编辑过的字段即时给出字段级错误提示，提交时一次性暴露全部问题并自动定位到第一个错误字段。
/// 未编辑过的字段不提前报错，避免刚打开的空表单一片红。
/// </summary>
public partial class WorkOrderEditDialog : Window, INotifyPropertyChanged, INotifyDataErrorInfo
{
    private string _orderNo = "";
    private string _productCode = "";
    private string _productName = "";
    private string? _selectedDeviceId;
    private string _targetQuantityText = "0";
    private DateTime _plannedStart = DateTime.Now;
    private DateTime _plannedEnd = DateTime.Now.AddHours(8);
    private string? _remark;

    /// <summary>逐字段错误表（字段名 → 错误文案）。</summary>
    private readonly Dictionary<string, string> _errors = new(StringComparer.Ordinal);

    /// <summary>已编辑过的字段名：只有编辑过的字段才显示错误。</summary>
    private readonly HashSet<string> _touched = new(StringComparer.Ordinal);

    /// <summary>表单字段顺序：决定「第一条错误」与焦点定位的先后（与 XAML 视觉顺序一致）。</summary>
    private static readonly string[] FieldOrder =
    [
        nameof(OrderNo),
        nameof(ProductCode),
        nameof(ProductName),
        nameof(SelectedDeviceId),
        nameof(TargetQuantityText),
        nameof(PlannedEnd),
    ];

    /// <summary>
    /// 可选设备列表（由调用方传入，绑定到 ComboBox）。
    /// 使用 ObservableCollection 以便 XAML 绑定即时反映。
    /// </summary>
    public ObservableCollection<DeviceOption> AvailableDevices { get; } = new();

    public WorkOrderEditDialog(WorkOrder? template, IReadOnlyList<(string Id, string Name)>? availableDevices = null)
    {
        InitializeComponent();
        DataContext = this;

        if (availableDevices != null)
        {
            foreach (var (id, name) in availableDevices)
                AvailableDevices.Add(new DeviceOption { Id = id, Name = name });
        }

        if (template != null)
        {
            // 编辑模式：预填字段（拷贝值，避免修改调用方传入的对象）
            // template.Id == 0 时为"新增并预填设备"模式（如从设备管理页新增工单），标题显示"新增工单"
            Title = template.Id == 0 ? "新增工单" : "编辑工单";
            _orderNo = template.OrderNo;
            _productCode = template.ProductCode;
            _productName = template.ProductName;
            _selectedDeviceId = template.DeviceId;
            _targetQuantityText = template.TargetQuantity.ToString();
            _plannedStart = template.PlannedStart == default ? DateTime.Now : template.PlannedStart;
            _plannedEnd = template.PlannedEnd == default ? DateTime.Now.AddHours(8) : template.PlannedEnd;
            _remark = template.Remark;
            OriginalId = template.Id;
            OriginalStatus = template.Status;
            OriginalCreatedAt = template.CreatedAt;
            OriginalDeviceName = template.DeviceName;
        }
        else
        {
            Title = "新增工单";
        }

        // 若未选中设备且列表非空，默认选中第一项
        if (string.IsNullOrEmpty(_selectedDeviceId) && AvailableDevices.Count > 0)
            _selectedDeviceId = AvailableDevices[0].Id;
    }

    /// <summary>原工单 Id（编辑模式保留，新增模式为 0）。</summary>
    public int OriginalId { get; }
    public WorkOrderStatus OriginalStatus { get; }
    public DateTime OriginalCreatedAt { get; }
    public string OriginalDeviceName { get; } = "";

    public string OrderNo { get => _orderNo; set => SetField(ref _orderNo, value); }
    public string ProductCode { get => _productCode; set => SetField(ref _productCode, value); }
    public string ProductName { get => _productName; set => SetField(ref _productName, value); }
    public string? SelectedDeviceId { get => _selectedDeviceId; set => SetField(ref _selectedDeviceId, value); }
    public string TargetQuantityText { get => _targetQuantityText; set => SetField(ref _targetQuantityText, value); }
    public DateTime PlannedStart { get => _plannedStart; set => SetField(ref _plannedStart, value); }
    public DateTime PlannedEnd { get => _plannedEnd; set => SetField(ref _plannedEnd, value); }
    public string? Remark { get => _remark; set => SetField(ref _remark, value); }

    /// <summary>用户确认后的工单结果。仅当 ShowDialog 返回 true 时有效。</summary>
    public WorkOrder? Result { get; private set; }

    // ──────────── INotifyDataErrorInfo ────────────

    /// <summary>是否存在校验错误（XAML 可绑定，如错误汇总的可见性）。</summary>
    public bool HasErrors => _errors.Count > 0;

    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

    public IEnumerable GetErrors(string? propertyName)
    {
        if (propertyName is null) return Array.Empty<string>();
        return _errors.TryGetValue(propertyName, out var message)
            ? new[] { message }
            : Array.Empty<string>();
    }

    /// <summary>写入/清除某字段的错误，仅在状态真正变化时通知（避免无谓的绑定刷新）。</summary>
    private void SetError(string propertyName, string? message)
    {
        var hadError = _errors.ContainsKey(propertyName);
        if (message is null)
        {
            if (!hadError) return;
            _errors.Remove(propertyName);
        }
        else
        {
            if (hadError && string.Equals(_errors[propertyName], message, StringComparison.Ordinal)) return;
            _errors[propertyName] = message;
        }
        ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(propertyName));
        OnPropertyChanged(nameof(HasErrors));
    }

    /// <summary>校验单个字段。未编辑过的字段不报错（避免打开空表单即一片红）。</summary>
    private void ValidateProperty(string propertyName)
    {
        string? error = propertyName switch
        {
            nameof(OrderNo) => string.IsNullOrWhiteSpace(OrderNo) ? Strings.K599 : null,
            nameof(ProductCode) => string.IsNullOrWhiteSpace(ProductCode) ? Strings.K600 : null,
            nameof(ProductName) => string.IsNullOrWhiteSpace(ProductName) ? Strings.K601 : null,
            // 设备必选：避免产生 DeviceId="" 的孤儿工单
            nameof(SelectedDeviceId) => string.IsNullOrWhiteSpace(SelectedDeviceId) ? Strings.K602 : null,
            // 计划产量必须为正整数（0 会导致进度条永远 0%）
            nameof(TargetQuantityText) =>
                int.TryParse(TargetQuantityText?.Trim(), out var qty) && qty > 0 ? null : Strings.K603,
            // 起止时间是交叉校验：任一变化都重算 PlannedEnd 的错误
            nameof(PlannedStart) or nameof(PlannedEnd) =>
                PlannedEnd <= PlannedStart ? Strings.K604 : null,
            _ => null,
        };

        // 时间错误统一挂在 PlannedEnd 上（与原来提交校验的提示位置一致）
        var target = propertyName is nameof(PlannedStart) ? nameof(PlannedEnd) : propertyName;

        if (!_touched.Contains(target) && !_touched.Contains(propertyName))
        {
            SetError(target, null);
            return;
        }
        SetError(target, error);
    }

    /// <summary>提交前：把所有字段标记为已编辑并重算，使从未点过的字段也能一次性暴露问题。</summary>
    private void ValidateAll()
    {
        foreach (var name in FieldOrder)
            _touched.Add(name);
        _touched.Add(nameof(PlannedStart));
        foreach (var name in FieldOrder)
            ValidateProperty(name);
    }

    /// <summary>按表单顺序取第一条错误文案（提示顺序与视觉顺序一致）。</summary>
    private string FirstErrorMessage
    {
        get
        {
            foreach (var name in FieldOrder)
                if (_errors.TryGetValue(name, out var message))
                    return message;
            return string.Empty;
        }
    }

    /// <summary>把焦点移到第一个出错的字段，省得用户自己找是哪一项不合规。</summary>
    private void FocusFirstInvalidField()
    {
        foreach (var name in FieldOrder)
        {
            if (!_errors.ContainsKey(name)) continue;
            switch (name)
            {
                case nameof(OrderNo): OrderNoBox.Focus(); return;
                case nameof(ProductCode): ProductCodeBox.Focus(); return;
                case nameof(ProductName): ProductNameBox.Focus(); return;
                case nameof(SelectedDeviceId): DeviceComboBox.Focus(); return;
                case nameof(TargetQuantityText): TargetQuantityBox.Focus(); return;
                case nameof(PlannedEnd): PlannedEndPicker.Focus(); return;
            }
        }
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        // 提交时一次性校验全部字段（而不是逐条弹 toast、一次只报一个问题）
        ValidateAll();
        if (HasErrors)
        {
            FocusFirstInvalidField();
            HandyControl.Controls.Growl.Warning(FirstErrorMessage);
            return;
        }

        // 查找选中设备名快照
        var deviceName = AvailableDevices.FirstOrDefault(d => d.Id == SelectedDeviceId)?.Name ?? OriginalDeviceName;

        Result = new WorkOrder
        {
            Id = OriginalId,
            OrderNo = OrderNo.Trim(),
            ProductCode = ProductCode.Trim(),
            ProductName = ProductName.Trim(),
            DeviceId = SelectedDeviceId ?? "",
            DeviceName = deviceName,
            // 校验已保证可解析且 > 0
            TargetQuantity = int.Parse(TargetQuantityText!.Trim()),
            PlannedStart = PlannedStart,
            PlannedEnd = PlannedEnd,
            Status = OriginalStatus,
            Remark = string.IsNullOrWhiteSpace(Remark) ? null : Remark.Trim(),
            CreatedAt = OriginalCreatedAt == default ? DateTime.Now : OriginalCreatedAt,
            UpdatedAt = DateTime.Now,
        };

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        if (name is not null)
        {
            // 标记为「已编辑」后即时校验：错误随输入实时更新，不必等到点「确认」。
            // 构造函数里是直接写字段（不经过属性），所以打开对话框不会误触发。
            _touched.Add(name);
            ValidateProperty(name);
        }
    }
}

/// <summary>设备下拉选项（Id/Name 两字段）。</summary>
public class DeviceOption
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}
