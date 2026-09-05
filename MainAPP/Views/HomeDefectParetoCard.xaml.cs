namespace MainAPP.Views;

/// <summary>
/// 主页仪表板卡片 6：缺陷 TOP5 列表（与 WASM 缺陷卡对齐）。
/// 帕累托列表式卡片（累计占比 / 严重度配色）。DataContext 由父级 HomeView 传入。
/// </summary>
public partial class HomeDefectParetoCard
{
    public HomeDefectParetoCard()
    {
        InitializeComponent();
    }
}
