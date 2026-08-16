using Kanban.Collector.Core.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Windows.Data;

namespace MainAPP.Services;

/// <summary>
/// WPF 集合绑定同步注册器（Core 去 WPF 化后的 UI 侧收口，2026-08-13）：
/// Core 仓库（DeviceRepository/WorkOrderRepository/RecipeStore）的 ObservableCollection 由后台
/// 采集线程写入、WPF UI 线程绑定读取。BindingOperations.EnableCollectionSynchronization 是 WPF
/// 专属 API（WindowsBase），只能存在于 UI 进程（MainAPP）；无头 Collector 不注册。
/// 作为 IHostedService 随宿主启动执行（早于任何视图实例化/绑定）：生产 App（App.OnStartup 的
/// Host.StartAsync）与 E2E TestHost 共用 AddMainAppCoreServices 入口，均自动生效。
/// </summary>
public sealed class WpfCollectionBindingRegistrar : IHostedService
{
    private readonly DeviceRepository _devices;
    private readonly WorkOrderRepository _workOrders;
    private readonly RecipeStore _recipes;
    private readonly ILogger<WpfCollectionBindingRegistrar> _logger;

    public WpfCollectionBindingRegistrar(
        DeviceRepository devices,
        WorkOrderRepository workOrders,
        RecipeStore recipes,
        ILogger<WpfCollectionBindingRegistrar> logger)
    {
        _devices = devices;
        _workOrders = workOrders;
        _recipes = recipes;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // 注册集合绑定同步锁：WPF 绑定引擎访问集合时会自动获取 SyncRoot，防止后台采集线程
        // 变更与 UI 线程枚举并发导致 InvalidOperationException。
        // EnableCollectionSynchronization 为全局注册，不要求 UI 线程调用；重复注册同集合以
        // 同锁调用是幂等的（宿主启动一次，无并发风险）。
        BindingOperations.EnableCollectionSynchronization(_devices.Devices, _devices.SyncRoot);
        BindingOperations.EnableCollectionSynchronization(_devices.Runtimes, _devices.SyncRoot);
        BindingOperations.EnableCollectionSynchronization(_workOrders.WorkOrders, _workOrders.SyncRoot);
        BindingOperations.EnableCollectionSynchronization(_recipes.Recipes, _recipes.SyncRoot);
        _logger.LogInformation("WPF 集合绑定同步锁注册完成（Devices/Runtimes/WorkOrders/Recipes）");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
