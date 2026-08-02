using System.Diagnostics;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using MainAPP.Services;
using MainAPP.ViewModels;
using Serilog;

namespace MainAPP.Views;

/// <summary>
/// SettingsView.xaml 的交互逻辑
/// </summary>
public partial class SettingsView : UserControl
{
    // 构造时刻：用于测量从构造到首次 Loaded（首次渲染完成）的耗时
    private readonly long _ctorTicks = Stopwatch.GetTimestamp();

    private SettingsViewModel? _settingsVm;

    public SettingsView()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// 首次渲染完成（首次切到设置页时触发一次）。
    /// 记录构造→Loaded 的耗时，判断页面首次加载是否是卡顿源。
    /// 同时订阅“界面字号”变化，切换时实时应用全局字号缩放（FontSizeManager）。
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        var elapsedMs = (Stopwatch.GetTimestamp() - _ctorTicks) * 1000.0 / Stopwatch.Frequency;
        Log.Debug("视图 {View} 首次 Loaded，构造→Loaded 耗时 {ElapsedMs:F1}ms", "SettingsView", elapsedMs);

        // 大屏远距可读性：设置页切换“界面字号”时实时应用，无需先点保存即可预览效果。
        if (DataContext is SettingsViewModel vm)
        {
            _settingsVm = vm;
            vm.AppSettings.PropertyChanged += OnAppSettingsPropertyChanged;
            FontSizeManager.ApplyScale(vm.AppSettings.UiScale);
        }
    }

    private void OnAppSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.UiScale) && _settingsVm != null)
            FontSizeManager.ApplyScale(_settingsVm.AppSettings.UiScale);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_settingsVm != null)
        {
            _settingsVm.AppSettings.PropertyChanged -= OnAppSettingsPropertyChanged;
            _settingsVm = null;
        }
        Unloaded -= OnUnloaded;
    }

}
