using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Models;
using MainAPP.Models;
using Kanban.Collector.Core.Services;
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
            // 冲刷共享 STA Dispatcher：把本测试期间遗留的排队操作（DispatcherTimer 回调、
            // async 封送、Growl 等）在当前测试返回前执行完。这些操作可能访问 DI 的
            // IServiceProvider；若拖到下一个测试的 Invoke 里执行（此时容器已释放）会抛
            // ObjectDisposedException('IServiceProvider')，表现为间歇性的“下一测试失败”。
            DrainDispatcher();
        }
    }

    /// <summary>执行所有优先级不低于 ContextIdle 的排队 Dispatcher 操作，消除跨测试的遗留状态。</summary>
    protected void DrainDispatcher()
    {
        try
        {
            Fixture.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        }
        catch
        {
            // 冲刷失败（如个别遗留操作自身抛异常）不阻塞当前测试结果判定
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
