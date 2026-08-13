using System.Windows;
using Material.Icons;
using Material.Icons.WPF;
using MainAPP.Resources;

namespace MainAPP.Controls;

/// <summary>
/// 统一空状态组件：图标 + 主文案 + 可选辅助提示。
/// 各页面在无数据时显示，替代手写 StackPanel。
/// </summary>
public partial class EmptyState : System.Windows.Controls.UserControl
{
    /// <summary>Material.Icons 图标类型依赖属性</summary>
    public static readonly DependencyProperty IconKindProperty =
        DependencyProperty.Register(nameof(IconKind), typeof(MaterialIconKind),
            typeof(EmptyState), new PropertyMetadata(MaterialIconKind.InboxOutline));

    /// <summary>主文案依赖属性</summary>
    public static readonly DependencyProperty MessageProperty =
        DependencyProperty.Register(nameof(Message), typeof(string),
            typeof(EmptyState), new PropertyMetadata(Strings.K583));

    /// <summary>辅助提示依赖属性（可选，为空时不显示）</summary>
    public static readonly DependencyProperty HintProperty =
        DependencyProperty.Register(nameof(Hint), typeof(string),
            typeof(EmptyState), new PropertyMetadata(""));

    public MaterialIconKind IconKind
    {
        get => (MaterialIconKind)GetValue(IconKindProperty);
        set => SetValue(IconKindProperty, value);
    }

    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public string Hint
    {
        get => (string)GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    public EmptyState()
    {
        InitializeComponent();
    }
}
