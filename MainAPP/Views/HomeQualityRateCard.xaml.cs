namespace MainAPP.Views;

/// <summary>
/// 主页仪表板卡片 5：当前班次良率（当班总产量 + 累计良率，下为小时良品柱与累计良率折线）。
/// 仅 XAML 视图，无代码逻辑。DataContext 由父级 HomeView 传入。
/// </summary>
public partial class HomeQualityRateCard
{
    public HomeQualityRateCard()
    {
        InitializeComponent();
    }
}
