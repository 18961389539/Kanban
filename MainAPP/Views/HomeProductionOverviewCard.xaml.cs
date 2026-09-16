namespace MainAPP.Views;

/// <summary>
/// 主页仪表板卡片 2：生产概览（当前速度进度条 + 工单 2×1 KPI；节拍已迁至设备状态卡）。
/// 仅 XAML 视图，无代码逻辑。DataContext 由父级 HomeView 传入。
/// </summary>
public partial class HomeProductionOverviewCard
{
    public HomeProductionOverviewCard()
    {
        InitializeComponent();
    }
}
