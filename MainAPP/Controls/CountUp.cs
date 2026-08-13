using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace MainAPP.Controls;

/// <summary>
/// 数字滚动动画附加属性：绑定到 TextBlock，值变化时做 0.5s 数字滚动（EaseOut 缓动）。
/// 用法：
///   <TextBlock controls:CountUp.Value="{Binding Runtime.Oee}"
///              controls:CountUp.Format="{}{0:P0}" />
/// 说明：附加属性支持绑定（数据变化触发动画）；Format 复用 .NET 数字格式串；
/// 首次赋值（旧值 = 默认 0）不播放动画，直接显示终值，避免启动时数字"滚上来"。
/// </summary>
public static class CountUp
{
    /// <summary>目标数值（支持绑定；数据更新时自动播放滚动动画）。</summary>
    public static readonly DependencyProperty ValueProperty = DependencyProperty.RegisterAttached(
        "Value", typeof(double), typeof(CountUp),
        new FrameworkPropertyMetadata(double.NaN, OnValueChanged));

    /// <summary>显示格式（默认 {0:N0}）。</summary>
    public static readonly DependencyProperty FormatProperty = DependencyProperty.RegisterAttached(
        "Format", typeof(string), typeof(CountUp), new FrameworkPropertyMetadata("{0:N0}"));

    public static void SetValue(DependencyObject o, double v) => o.SetValue(ValueProperty, v);
    public static double GetValue(DependencyObject o) => (double)o.GetValue(ValueProperty);
    public static void SetFormat(DependencyObject o, string v) => o.SetValue(FormatProperty, v);
    public static string GetFormat(DependencyObject o) => (string)o.GetValue(FormatProperty);

    /// <summary>单次滚动时长（毫秒）。</summary>
    public const double DurationMs = 500;

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        if (e.NewValue is not double to || double.IsNaN(to)) return;
        var format = GetFormat(d);

        // 首次赋值（NaN → 值）直接显示终值，不播放动画
        if (e.OldValue is not double from || double.IsNaN(from))
        {
            tb.Text = string.Format(format, to);
            return;
        }

        // 值相同无需动画
        if (Math.Abs(from - to) < 1e-9)
        {
            tb.Text = string.Format(format, to);
            return;
        }

        var anim = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(DurationMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };
        var clock = anim.CreateClock();
        clock.Completed += (_, _) => tb.Text = string.Format(format, to);
        clock.CurrentTimeInvalidated += (_, _) =>
        {
            // 手动取进度插值（含缓动），保证 TextBlock 的 Text 逐帧更新
            if (clock.CurrentProgress is { } progress)
            {
                var eased = anim.EasingFunction?.Ease(progress) ?? progress;
                tb.Text = string.Format(format, from + (to - from) * eased);
            }
            else
            {
                tb.Text = string.Format(format, to);
            }
        };
        clock.Controller?.Begin();
    }
}
