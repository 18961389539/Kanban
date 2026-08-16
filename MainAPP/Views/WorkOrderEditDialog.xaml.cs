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
/// </summary>
public partial class WorkOrderEditDialog : Window, INotifyPropertyChanged
{
    private string _orderNo = "";
    private string _productCode = "";
    private string _productName = "";
    private string? _selectedDeviceId;
    private string _targetQuantityText = "0";
    private DateTime _plannedStart = DateTime.Now;
    private DateTime _plannedEnd = DateTime.Now.AddHours(8);
    private string? _remark;

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

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        // 基本校验：工单号、产品编码、产品名为必填
        if (string.IsNullOrWhiteSpace(OrderNo))
        {
            HandyControl.Controls.Growl.Warning(Strings.K599);
            OrderNoBox.Focus();
            return;
        }
        if (string.IsNullOrWhiteSpace(ProductCode))
        {
            HandyControl.Controls.Growl.Warning(Strings.K600);
            ProductCodeBox.Focus();
            return;
        }
        if (string.IsNullOrWhiteSpace(ProductName))
        {
            HandyControl.Controls.Growl.Warning(Strings.K601);
            ProductNameBox.Focus();
            return;
        }
        // 设备必选：工单必须绑定设备，避免产生 DeviceId="" 的孤儿工单
        if (string.IsNullOrWhiteSpace(SelectedDeviceId))
        {
            HandyControl.Controls.Growl.Warning(Strings.K602);
            DeviceComboBox.Focus();
            return;
        }
        // 解析计划产量：必须为正整数（0 会导致进度条永远 0%）
        if (!int.TryParse(TargetQuantityText?.Trim(), out var qty) || qty <= 0)
        {
            HandyControl.Controls.Growl.Warning(Strings.K603);
            TargetQuantityBox.Focus();
            return;
        }
        if (PlannedEnd <= PlannedStart)
        {
            HandyControl.Controls.Growl.Warning(Strings.K604);
            PlannedEndPicker.Focus();
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
            TargetQuantity = qty,
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
    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>设备下拉选项（Id/Name 两字段）。</summary>
public class DeviceOption
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}
