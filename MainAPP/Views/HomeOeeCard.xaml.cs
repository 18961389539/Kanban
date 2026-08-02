namespace MainAPP.Views;

/// <summary>
/// 主页仪表板卡片 4：OEE 综合效率（OEE 主环 + 可用率/性能率/合格率三环小卡，公式收至 ToolTip）。
/// 仅 XAML 视图，无代码逻辑。DataContext 由父级 HomeView 传入。
/// </summary>
public partial class HomeOeeCard
{
    public HomeOeeCard()
    {
        InitializeComponent();
    }
}
