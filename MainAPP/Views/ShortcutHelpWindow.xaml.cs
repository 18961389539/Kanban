using System.Collections.Generic;
using System.Windows;
using MainAPP.Resources;

namespace MainAPP.Views;

/// <summary>
/// 快捷键速查独立小窗（隐藏入口：Ctrl+Shift+K）。
/// 与手册附录 C 保持同源：增改快捷键时同步本窗口与手册。
/// </summary>
public partial class ShortcutHelpWindow : Window
{
    /// <summary>快捷键条目：快捷键文本 + 本地化功能说明。</summary>
    public sealed record ShortcutEntry(string Shortcut, string Description);

    public IReadOnlyList<ShortcutEntry> Entries { get; } = new List<ShortcutEntry>
    {
        new("Ctrl+1 ~ Ctrl+9", Strings.Ux_Kb1),
        new("Ctrl+L", Strings.Ux_Kb2),
        new("F1", Strings.Ux_Kb3),
        new("F11", Strings.Ux_Kb4),
        new("F5", Strings.Ux_Kb5),
        new("Esc", Strings.Ux_Kb6),
        new("Ctrl+Enter", Strings.Ux_Kb7),
        new("Ctrl+R", Strings.Ux_Kb8),
        new("Ctrl+E", Strings.Ux_Kb9),
        new("Ctrl+S", Strings.Ux_Kb10),
    };

    public ShortcutHelpWindow()
    {
        InitializeComponent();
        DataContext = this;
    }
}