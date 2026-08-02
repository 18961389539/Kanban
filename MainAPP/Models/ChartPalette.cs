using OxyPlot;

namespace MainAPP.Models;

/// <summary>
/// 图表颜色统一管理。ChartService 与 OverviewViewModel 共用此常量类，
/// Brushes.xaml 中的 ChartOkSeriesBrush/ChartBaseSeriesBrush 等资源值必须与此保持一致。
/// </summary>
public static class ChartPalette
{
    // 文本与网格
    public static readonly OxyColor Text = OxyColor.FromRgb(0xE5, 0xE7, 0xEB);
    public static readonly OxyColor MutedText = OxyColor.FromRgb(0xA7, 0xAF, 0xBC);
    public static readonly OxyColor Grid = OxyColor.FromRgb(0x3A, 0x44, 0x53);
    public static readonly OxyColor LegendBackground = OxyColor.FromArgb(200, 0x1A, 0x20, 0x29);

    // 状态色（与 Brushes.xaml StatusRunBrush/StatusAlarmBrush/StatusPauseBrush 一致）
    public static readonly OxyColor Run = OxyColor.FromRgb(0x34, 0xD3, 0x99);
    public static readonly OxyColor Alarm = OxyColor.FromRgb(0xF8, 0x71, 0x71);
    public static readonly OxyColor Pause = OxyColor.FromRgb(0xFB, 0xBF, 0x24);
    public static readonly OxyColor Idle = OxyColor.FromRgb(0x6B, 0x72, 0x80);
    public static readonly OxyColor Border = OxyColor.FromRgb(0x1A, 0x20, 0x29);
    public static readonly OxyColor Axis = OxyColor.FromRgb(0x4A, 0x55, 0x68);
    public static readonly OxyColor GridStrong = OxyColor.FromRgb(0x2D, 0x37, 0x48);

    // 图表系列色
    public static readonly OxyColor Ok = OxyColor.FromRgb(0x81, 0x8C, 0xF8);
    public static readonly OxyColor Ng = OxyColor.FromRgb(0xF8, 0x71, 0x71);
    public static readonly OxyColor Oee = OxyColor.FromRgb(0x34, 0xD3, 0x99);
    public static readonly OxyColor Secondary = OxyColor.FromRgb(0xA9, 0xAE, 0xFF);
    public static readonly OxyColor Base = OxyColor.FromRgb(0x60, 0xA5, 0xFA);
    public static readonly OxyColor Loss = OxyColor.FromRgb(0xF8, 0x71, 0x71);
    public static readonly OxyColor Remain = OxyColor.FromRgb(0x9C, 0xA3, 0xAF);
    public static readonly OxyColor ShiftBg = OxyColor.FromArgb(20, 0x81, 0x8C, 0xF8);
    public static readonly OxyColor RunFill = OxyColor.FromArgb(120, 0x34, 0xD3, 0x99);
    public static readonly OxyColor AlarmFill = OxyColor.FromArgb(120, 0xF8, 0x71, 0x71);
    public static readonly OxyColor HeatmapZero = OxyColor.FromRgb(0x1F, 0x29, 0x37);
    public static readonly OxyColor HeatmapBorder = OxyColor.FromArgb(60, 0x1A, 0x20, 0x29);

    public static OxyColor Heatmap(double ratio)
    {
        ratio = Math.Clamp(ratio, 0, 1);
        return OxyColor.FromRgb(
            Blend(0x1E, 0x60, ratio),
            Blend(0x3A, 0xA5, ratio),
            Blend(0x5C, 0xFA, ratio));
    }

    private static byte Blend(byte from, byte to, double ratio) =>
        (byte)(from + (to - from) * ratio);
}
