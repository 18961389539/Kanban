using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MainAPP.Data;
using MainAPP.Models;
using MainAPP.Services;

namespace MainAPP.Tests.Integration;

/// <summary>
/// UI 测试基础类：通过共享 <see cref="WpfStaFixture"/> 在单一 STA 线程上执行测试代码，
/// 避免多个 STA 线程并发创建 MaterialIcon 触发 Material.Icons 内部字典并发损坏。
/// 子类必须打 [Collection("WpfUi")] 标签，由 xUnit 注入 fixture。
/// </summary>
public abstract class WpfTestHost
{
    /// <summary>由 xUnit 通过 Collection Fixture 注入。</summary>
    protected WpfStaFixture Fixture { get; }

    /// <summary>捕获到的绑定错误列表（每个测试方法开始前重置）。</summary>
    protected List<string> BindingErrors { get; } = new();

    protected WpfTestHost(WpfStaFixture fixture)
    {
        Fixture = fixture;
    }

    /// <summary>
    /// 在共享 STA 线程中执行测试步骤。已注入 HandyControl 主题、自定义画刷、转换器。
    /// 串行执行保证 Material.Icons 不会并发损坏。
    /// </summary>
    protected void RunOnSta(Action<Application> testStep)
    {
        BindingErrors.Clear();
        CollectListenerScope? listener = null;
        try
        {
            listener = new CollectListenerScope(BindingErrors);
            PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
            Fixture.Invoke(app => testStep(app));
        }
        finally
        {
            if (listener != null)
                PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
        }
    }

    /// <summary>统计可视化树中包含指定文本的 TextBlock 数量。</summary>
    protected static int CountTextBlocks(DependencyObject root, string contains)
    {
        int count = 0;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock tb && tb.Text != null && tb.Text.Contains(contains)) count++;
            count += CountTextBlocks(child, contains);
        }
        return count;
    }

    /// <summary>查找可视化树中第一个包含指定文本的 TextBlock，未找到返回 null。</summary>
    protected static TextBlock? FindTextBlock(DependencyObject root, string contains)
    {
        for (int i =  0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock tb && tb.Text != null && tb.Text.Contains(contains)) return tb;
            var found = FindTextBlock(child, contains);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>查找可视化树中所有指定类型的元素。</summary>
    protected static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T matched) yield return matched;
            foreach (var d in FindVisualDescendants<T>(child)) yield return d;
        }
    }

    /// <summary>构造带 N 台设备的测试用 DeviceRepository（最多支持 5 台命名设备）。</summary>
    protected static (DeviceRepository repo, Device d1, Device d2) BuildTestRepository(int deviceCount = 2)
    {
        var appSettings = new AppSettings();
        var repo = new DeviceRepository(appSettings);
        var names = new[] { "注塑机A1", "焊接机器人B2", "检测机C3", "包装机D4", "激光机E5" };
        for (int i = 0; i < deviceCount && i < names.Length; i++)
        {
            var d = new Device { Name = names[i], RecipeName = $"配方-{i + 1}" };
            repo.Devices.Add(d);
            repo.Runtimes.Add(new DeviceRuntime(d));
        }
        return (repo,
            repo.Devices.Count > 0 ? repo.Devices[0] : null!,
            repo.Devices.Count > 1 ? repo.Devices[1] : null!);
    }

    /// <summary>
    /// 一次性 TraceListener：捕获 WPF 数据绑定错误到指定列表。
    /// </summary>
    private class CollectListenerScope : TraceListener, IDisposable
    {
        private readonly List<string> _errors;
        public CollectListenerScope(List<string> errors) => _errors = errors;
        public override void Write(string? message) { if (message != null) _errors.Add(message); }
        public override void WriteLine(string? message) { if (message != null) _errors.Add(message); }
    }
}
