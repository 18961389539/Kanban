using System.Windows;
using System.Windows.Controls;
using MainAPP.ViewModels;

namespace MainAPP.Views;

/// <summary>操作审计查询页（Admin 可见）：谁在何时对什么做了什么。</summary>
public partial class AuditQueryView : UserControl
{
    public AuditQueryView()
    {
        InitializeComponent();
    }

    /// <summary>时间预设 chip 点击：Tag 携带预设索引（1=今天 2=近7天 3=近30天 4=本月）。</summary>
    private void OnPresetChipClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe
            && fe.Tag is string tag
            && int.TryParse(tag, out var preset)
            && DataContext is AuditQueryViewModel vm)
        {
            vm.PresetIndex = preset;
        }
    }
}
