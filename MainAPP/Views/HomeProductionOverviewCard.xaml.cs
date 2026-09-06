namespace MainAPP.Views;

/// <summary>
/// 主页仪表板卡片 2：生产概览（当前速度进度条 + 总产量/不良数/目标周期/实际周期 2x2 KPI）。
/// 仅 XAML 视图，无代码逻辑。DataContext 由父级 HomeView 传入。
/// </summary>
public partial class HomeProductionOverviewCard
{
    public HomeProductionOverviewCard()
    {
        InitializeComponent();
    }
}
