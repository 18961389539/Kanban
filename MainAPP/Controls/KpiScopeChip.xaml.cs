using System.Windows;
using System.Windows.Controls;

namespace MainAPP.Controls;

public enum KpiScopeKind
{
    WorkOrder,
    Shift,
    Realtime,
    Range
}

/// <summary>
/// 首页数字旁的口径芯片：工单时间窗 / 当班会话 / 实时。
/// </summary>
public partial class KpiScopeChip : UserControl
{
    public static readonly DependencyProperty KindProperty =
        DependencyProperty.Register(nameof(Kind), typeof(KpiScopeKind), typeof(KpiScopeChip),
            new PropertyMetadata(KpiScopeKind.WorkOrder));

    public KpiScopeKind Kind
    {
        get => (KpiScopeKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public KpiScopeChip()
    {
        InitializeComponent();
    }
}
