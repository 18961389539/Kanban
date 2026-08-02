using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MainAPP.Data;
using MainAPP.Models;
using MainAPP.Services;
using MainAPP.Tests;
using MainAPP.Tests.Unit;
using MainAPP.ViewModels;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// 设备页 ViewModel 纯单元测试：覆盖高优先级易用性修复——
/// ⑤ 编辑后脏标记提示（IsDirty）、② 手动 OEE 清零二次确认。
/// 不依赖真实 PLC / 数据库（FakePlcDriver + InMemoryHistoryService + 临时数据目录）。
/// 因 NewVm 会临时改写 KANBAN_DATA_DIR 进程环境变量，本类关闭并行化以避免干扰其他测试类。
/// </summary>
[CollectionDefinition("DeviceManagerVM", DisableParallelization = true)]
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class DeviceManagerVMCollectionDefinition { }

[Collection("DeviceManagerVM")]
public class DeviceManagerViewModelTests
{
    // 本地空 ILogger 实现，避免对 Microsoft.Extensions.Logging.Abstractions 程序集的静态类型依赖
    private sealed class TestNullLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }

    private static (DeviceManagerViewModel vm, FakeDialogService dialog, FakePlcDriver driver, string tmp) NewVm()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "kanban_dev_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        Directory.CreateDirectory(Path.Combine(tmp, "Config")); // ProductionBaselineStore 写 baselines.json 到 Config 子目录
        Environment.SetEnvironmentVariable("KANBAN_DATA_DIR", tmp);

        var appSettings = new AppSettings();
        var repo = new DeviceRepository(appSettings);
        var driver = new FakePlcDriver();
        var conn = new PlcConnectionManager(driver, appSettings);
        conn.EnsureConnected();
        var history = new InMemoryHistoryService();
        var store = new ProductionBaselineStore(appSettings);
        var dataAcq = new PlcDataAcquisitionService(
            driver, conn, appSettings, history, repo, store, new TestNullLogger<PlcDataAcquisitionService>());
        var dialog = new FakeDialogService();
        // 设备管理器拆分后，构造 VM 前需先组装 IO 与 PLC 命令两个协作服务
        var configIO = new DeviceConfigIOService(repo, dialog);
        var plcCommands = new DevicePlcCommandHandler(driver, conn, dataAcq);
        var alarmCsvIO = new AlarmCsvIOService(dialog);
        var dbProvider = new DatabaseProvider(appSettings);
        var workOrderRepo = new WorkOrderRepository(dbProvider, TestMapper.Instance);
        var workOrderService = new WorkOrderService(workOrderRepo, repo, dialog, history);
        var vm = new DeviceManagerViewModel(repo, dataAcq, dialog, configIO, plcCommands, alarmCsvIO, workOrderRepo, workOrderService);
        return (vm, dialog, driver, tmp);
    }

    private static void ConfigureAddresses(Device d)
    {
        d.OkCountAddress = "D100";
        d.NgCountAddress = "D102";
        d.StatusCountAddress = "D104";
        d.ProductionResetAddress = "D106";
        // TargetCycle 必须 > 0：保存校验会拦截 TargetCycle <= 0 的设备（OEE 性能率分母为 0 无意义）
        d.TargetCycle = 600;
    }

    [Fact]
    public void IsDirty_InitiallyFalse()
    {
        var (vm, _, _, tmp) = NewVm();
        Assert.False(vm.IsDirty);
        Directory.Delete(tmp, true);
    }

    [Fact]
    public void AddDevice_SetsDirty()
    {
        var (vm, _, _, tmp) = NewVm();
        vm.AddDeviceCommand.Execute(null);
        Assert.True(vm.IsDirty);
        Assert.Single(vm.Devices);
        Directory.Delete(tmp, true);
    }

    [Fact]
    public void EditDeviceName_AfterSave_SetsDirty()
    {
        var (vm, _, _, tmp) = NewVm();
        vm.AddDeviceCommand.Execute(null);
        ConfigureAddresses(vm.SelectedDevice!);
        vm.SaveCommand.Execute(null);
        Assert.False(vm.IsDirty); // 保存后清除

        vm.SelectedDevice!.Name = "改名设备";
        Assert.True(vm.IsDirty); // 改名后置脏
        Directory.Delete(tmp, true);
    }

    [Fact]
    public void Save_ClearsDirty()
    {
        var (vm, dialog, _, tmp) = NewVm();
        vm.AddDeviceCommand.Execute(null);
        ConfigureAddresses(vm.SelectedDevice!);
        vm.SaveCommand.Execute(null);
        Assert.False(vm.IsDirty);
        Assert.Empty(dialog.Warning);
        Directory.Delete(tmp, true);
    }

    [Fact]
    public void RuntimeFieldChange_DoesNotSetDirty()
    {
        var (vm, _, _, tmp) = NewVm();
        vm.AddDeviceCommand.Execute(null);
        ConfigureAddresses(vm.SelectedDevice!);
        // 计数报警 CRUD 已拆分到 CountAlarmManagerVm 子 VM（通过 host.SelectedDevice 同步联动）
        vm.CountAlarmManagerVm.AddCountAlarmCommand.Execute(null);
        // MaxValue 必须 > 0：保存校验会拦截 MaxValue <= 0 的计数报警（IsTriggered 永远为 true 导致误报）
        vm.SelectedDevice!.CountAlarms[0].MaxValue = 100;
        vm.SaveCommand.Execute(null); // 持久化，IsDirty=false
        Assert.False(vm.IsDirty);

        var ca = vm.SelectedDevice!.CountAlarms[0];
        ca.CurrentValue = 123; // 运行时字段变更（采集线程写入）
        Assert.False(vm.IsDirty); // 不应误报未保存
        Directory.Delete(tmp, true);
    }

    [Fact]
    public async Task ResetProduction_ConfirmNo_DoesNotTrigger()
    {
        var (vm, dialog, driver, tmp) = NewVm();
        vm.AddDeviceCommand.Execute(null);
        ConfigureAddresses(vm.SelectedDevice!);
        dialog.ShowResult = System.Windows.MessageBoxResult.No;

        await vm.ResetProductionCommand.ExecuteAsync(null);

        Assert.Contains(dialog.ShowCalls, c => c.Title == "确认 OEE 清零");
        Assert.DoesNotContain(dialog.Success, s => s.Contains("OEE 清零"));
        Assert.Empty(driver.WriteHistory); // 未向 PLC 写清零
        Directory.Delete(tmp, true);
    }

    [Fact]
    public async Task ResetProduction_ConfirmYes_TriggersClear()
    {
        var (vm, dialog, driver, tmp) = NewVm();
        vm.AddDeviceCommand.Execute(null);
        ConfigureAddresses(vm.SelectedDevice!);
        dialog.ShowResult = System.Windows.MessageBoxResult.Yes;

        await vm.ResetProductionCommand.ExecuteAsync(null);

        Assert.Contains(dialog.ShowCalls, c => c.Title == "确认 OEE 清零");
        Assert.Contains(dialog.Success, s => s.Contains("OEE 清零"));
        Assert.Contains(driver.WriteHistory, w => w.Address == "D106"); // 已写 PLC 清零
        Directory.Delete(tmp, true);
    }

    [Fact]
    public void DeviceRuntimeMap_ExposesRuntimeForAddedDevice()
    {
        var (vm, _, _, tmp) = NewVm();
        vm.AddDeviceCommand.Execute(null);
        var id = vm.SelectedDevice!.Id;
        Assert.True(vm.DeviceRuntimeMap.ContainsKey(id));
        Directory.Delete(tmp, true);
    }

    [Fact]
    public void StatusFilter_FiltersByRuntimeStatus()
    {
        var (vm, _, _, tmp) = NewVm();
        vm.AddDeviceCommand.Execute(null);
        var dev1 = vm.SelectedDevice!;
        dev1.Name = "运行设备";
        vm.AddDeviceCommand.Execute(null);
        var dev2 = vm.SelectedDevice!;
        dev2.Name = "报警设备";

        // 设置运行时状态字（此时 StatusFilter=All，仅触发色点刷新，不触发 Dispatcher.Refresh）
        vm.DeviceRuntimeMap[dev1.Id].StatusWord = (int)DeviceStatus.Running;
        vm.DeviceRuntimeMap[dev2.Id].StatusWord = (int)DeviceStatus.Alarm;

        // 全部：两条均可见
        Assert.Equal(2, vm.FilteredDevices.Cast<Device>().Count());

        // 按「运行」筛选：仅运行设备可见
        vm.StatusFilter = DeviceStatusFilter.Running;
        Assert.Single(vm.FilteredDevices.Cast<Device>());
        Assert.Equal("运行设备", vm.FilteredDevices.Cast<Device>().First().Name);

        // 按「报警」筛选：仅报警设备可见
        vm.StatusFilter = DeviceStatusFilter.Alarm;
        Assert.Single(vm.FilteredDevices.Cast<Device>());
        Assert.Equal("报警设备", vm.FilteredDevices.Cast<Device>().First().Name);

        // 恢复全部：两条均可见
        vm.StatusFilter = DeviceStatusFilter.All;
        Assert.Equal(2, vm.FilteredDevices.Cast<Device>().Count());
        Directory.Delete(tmp, true);
    }

    [Fact]
    public void StatusFilter_CombinesWithKeyword()
    {
        var (vm, _, _, tmp) = NewVm();
        vm.AddDeviceCommand.Execute(null);
        var dev1 = vm.SelectedDevice!;
        dev1.Name = "A机";
        vm.AddDeviceCommand.Execute(null);
        var dev2 = vm.SelectedDevice!;
        dev2.Name = "B机";

        vm.DeviceRuntimeMap[dev1.Id].StatusWord = (int)DeviceStatus.Running;
        vm.DeviceRuntimeMap[dev2.Id].StatusWord = (int)DeviceStatus.Running;

        vm.SearchKeyword = "B";
        vm.StatusFilter = DeviceStatusFilter.Running;
        Assert.Single(vm.FilteredDevices.Cast<Device>());
        Assert.Equal("B机", vm.FilteredDevices.Cast<Device>().First().Name);
        Directory.Delete(tmp, true);
    }

    [Fact]
    public void DeviceSummaryText_ReflectsRuntimeStatusCounts()
    {
        var (vm, _, _, tmp) = NewVm();
        vm.AddDeviceCommand.Execute(null);
        var running = vm.SelectedDevice!;
        vm.AddDeviceCommand.Execute(null);
        var alarm = vm.SelectedDevice!;
        vm.AddDeviceCommand.Execute(null);
        var paused = vm.SelectedDevice!;

        vm.DeviceRuntimeMap[running.Id].StatusWord = (int)DeviceStatus.Running;
        vm.DeviceRuntimeMap[alarm.Id].StatusWord = (int)DeviceStatus.Alarm;
        vm.DeviceRuntimeMap[paused.Id].StatusWord = (int)DeviceStatus.Paused;

        Assert.Equal("3 台 · 运行 1 · 报警 1 · 待机 1 · 初始 0", vm.DeviceSummaryText);
        Directory.Delete(tmp, true);
    }

    // ──────────── 导入 / 导出 ────────────

    [Fact]
    public void ExportConfig_WritesJsonFile()
    {
        var (vm, dialog, _, tmp) = NewVm();
        vm.AddDeviceCommand.Execute(null);
        ConfigureAddresses(vm.SelectedDevice!);
        vm.SaveCommand.Execute(null);
        Assert.Single(vm.Devices);

        var exportPath = Path.Combine(tmp, "export.json");
        dialog.SaveFilePath = exportPath;
        vm.ExportConfigCommand.Execute(null);

        Assert.True(File.Exists(exportPath));
        var json = File.ReadAllText(exportPath);
        Assert.Contains("D100", json); // 地址随设备配置导出
        Assert.Contains(dialog.Success, s => s.Contains("已导出"));
        Assert.False(vm.IsDirty); // 导出是只读操作，不应置脏
        Directory.Delete(tmp, true);
    }

    [Fact]
    public void ExportConfig_CancelDialog_WritesNothing()
    {
        var (vm, dialog, _, tmp) = NewVm();
        vm.AddDeviceCommand.Execute(null);
        ConfigureAddresses(vm.SelectedDevice!);
        vm.SaveCommand.Execute(null);

        dialog.SaveFilePath = null; // 用户取消保存对话框
        dialog.Success.Clear(); // 清除前序 Save 的成功通知，仅验证导出未写文件
        vm.ExportConfigCommand.Execute(null);

        Assert.Empty(dialog.Success);
        Directory.Delete(tmp, true);
    }

    [Fact]
    public void ImportConfig_ReplacesDevices_OnConfirmed()
    {
        var (vm, dialog, _, tmp) = NewVm();
        vm.AddDeviceCommand.Execute(null);
        ConfigureAddresses(vm.SelectedDevice!);
        vm.SaveCommand.Execute(null);
        Assert.Single(vm.Devices);

        // 准备一个含 2 台设备的导入文件
        var importPath = Path.Combine(tmp, "import.json");
        var repo2 = new DeviceRepository(new AppSettings());
        repo2.Devices.Add(new Device { Name = "导入设备A", OkCountAddress = "D10", NgCountAddress = "D12", StatusCountAddress = "D14", ProductionResetAddress = "D16" });
        repo2.Devices.Add(new Device { Name = "导入设备B", OkCountAddress = "D20", NgCountAddress = "D22", StatusCountAddress = "D24", ProductionResetAddress = "D26" });
        repo2.ExportToFile(importPath);

        dialog.ShowResult = System.Windows.MessageBoxResult.Yes;
        dialog.OpenFilePath = importPath;
        vm.ImportConfigCommand.Execute(null);

        Assert.Equal(2, vm.Devices.Count);
        Assert.Contains(vm.Devices, d => d.Name == "导入设备A");
        Assert.Contains(vm.Devices, d => d.Name == "导入设备B");
        Assert.Equal(2, vm.DeviceRuntimeMap.Count); // 运行时状态同步替换
        Assert.True(vm.IsDirty); // 导入后需保存以持久化
        Assert.Contains(dialog.Success, s => s.Contains("已导入"));
        Directory.Delete(tmp, true);
    }

    [Fact]
    public void ImportConfig_Cancel_KeepsCurrentDevices()
    {
        var (vm, dialog, _, tmp) = NewVm();
        vm.AddDeviceCommand.Execute(null);
        ConfigureAddresses(vm.SelectedDevice!);
        vm.SaveCommand.Execute(null);
        Assert.Single(vm.Devices);
        Assert.False(vm.IsDirty);

        var importPath = Path.Combine(tmp, "import.json");
        File.WriteAllText(importPath, "[{\"id\":\"x\",\"name\":\"外来设备\"}]");
        dialog.ShowResult = System.Windows.MessageBoxResult.No;
        dialog.OpenFilePath = importPath;
        vm.ImportConfigCommand.Execute(null);

        Assert.Single(vm.Devices); // 未替换
        Assert.False(vm.IsDirty); // 未置脏
        Assert.DoesNotContain(dialog.Success, s => s.Contains("已导入"));
        Directory.Delete(tmp, true);
    }

    [Fact]
    public void ImportConfig_BadJson_ShowsError()
    {
        var (vm, dialog, _, tmp) = NewVm();
        vm.AddDeviceCommand.Execute(null);
        ConfigureAddresses(vm.SelectedDevice!);
        vm.SaveCommand.Execute(null);

        var importPath = Path.Combine(tmp, "bad.json");
        File.WriteAllText(importPath, "{ this is not valid json ");
        dialog.OpenFilePath = importPath;
        vm.ImportConfigCommand.Execute(null);

        Assert.Single(vm.Devices); // 解析失败不替换
        Assert.Contains(dialog.Error, e => e.Contains("解析失败"));
        Directory.Delete(tmp, true);
    }

    // ──────────── 保存校验聚合 + 定位 ────────────

    [Fact]
    public void Save_AggregatesAllErrors_InsteadOfEarlyReturn()
    {
        var (vm, dialog, _, tmp) = NewVm();
        vm.AddDeviceCommand.Execute(null);
        var a = vm.SelectedDevice!;
        a.Name = "重复名";
        ConfigureAddresses(a); // 配置地址 + TargetCycle，避免引入与"聚合错误"无关的校验失败
        vm.AddDeviceCommand.Execute(null);
        var b = vm.SelectedDevice!;
        b.Name = "重复名"; // 与 a 同名 → 设备名重复
        // b 使用不同地址段，避免与 a 形成跨设备地址冲突（冲突会引入额外错误干扰聚合验证）
        b.OkCountAddress = "D200";
        b.NgCountAddress = "D202";
        b.StatusCountAddress = "D204";
        b.ProductionResetAddress = "D206";
        b.TargetCycle = 600;

        vm.SaveCommand.Execute(null);

        // 错误聚合到内联列表，而非逐个弹窗；错误项 = 设备名重复(1) = 1
        // （地址与 TargetCycle 均已配置且无跨设备冲突，仅剩同名冲突这一类错误）
        Assert.Single(vm.ValidationErrors);
        Assert.Empty(dialog.ConfigErrorCalls);
        Assert.Contains(dialog.Warning, w => w.Contains("保存失败"));
        Assert.DoesNotContain(dialog.Success, s => s.Contains("保存成功")); // 未执行持久化
        Directory.Delete(tmp, true);
    }

    [Fact]
    public void Save_ClickError_NavigatesToDeviceAndTab()
    {
        var (vm, dialog, _, tmp) = NewVm();
        vm.AddDeviceCommand.Execute(null);
        var a = vm.SelectedDevice!;
        a.Name = "A设备";
        vm.AddDeviceCommand.Execute(null);
        var b = vm.SelectedDevice!;
        b.Name = "B设备";
        // b 内两个同名报警（制造校验错误）；a 保持缺地址（制造校验错误）
        b.Alarms.Add(new Alarm { Name = "X", DeviceId = b.Id });
        b.Alarms.Add(new Alarm { Name = "X", DeviceId = b.Id });

        vm.SaveCommand.Execute(null);
        var alarmError = Assert.Single(vm.ValidationErrors, error => error.Device == b && error.TargetTabIndex == 1);
        vm.FocusValidationErrorCommand.Execute(alarmError);

        Assert.Equal(b, vm.SelectedDevice);
        Assert.Equal(1, vm.SelectedTabIndex); // 切到报警管理选项卡
        Directory.Delete(tmp, true);
    }

    // ──────────── 搜索扩展到地址 / 报警名 ────────────

    [Fact]
    public void Search_ByAlarmName_MatchesDevice()
    {
        var (vm, _, _, tmp) = NewVm();
        vm.AddDeviceCommand.Execute(null);
        var dev = vm.SelectedDevice!;
        dev.Name = "主机";
        dev.Alarms.Add(new Alarm { Name = "温度过高", DeviceId = dev.Id });
        vm.DeviceRuntimeMap[dev.Id].StatusWord = (int)DeviceStatus.Running;

        vm.SearchKeyword = "温度";
        Assert.Single(vm.FilteredDevices.Cast<Device>());
        Assert.Equal("主机", vm.FilteredDevices.Cast<Device>().First().Name);
        Directory.Delete(tmp, true);
    }

    [Fact]
    public void Search_ByOkAddress_MatchesDevice()
    {
        var (vm, _, _, tmp) = NewVm();
        vm.AddDeviceCommand.Execute(null);
        var dev = vm.SelectedDevice!;
        dev.Name = "设备X";
        dev.OkCountAddress = "D512";

        vm.SearchKeyword = "D512";
        Assert.Single(vm.FilteredDevices.Cast<Device>());
        Assert.Equal("设备X", vm.FilteredDevices.Cast<Device>().First().Name);
        Directory.Delete(tmp, true);
    }

    [Fact]
    public void Search_NoMatch_FiltersOutAll()
    {
        var (vm, _, _, tmp) = NewVm();
        vm.AddDeviceCommand.Execute(null);
        vm.SelectedDevice!.Name = "设备Y";

        vm.SearchKeyword = "不存在的关键字";
        Assert.Empty(vm.FilteredDevices.Cast<Device>());
        Directory.Delete(tmp, true);
    }

    // ──────────── 复制设备 ────────────

    [Fact]
    public void CopyDevice_ClonesConfig_NewId_UniqueName_MarksDirty()
    {
        var (vm, _, _, tmp) = NewVm();
        vm.AddDeviceCommand.Execute(null);
        var src = vm.SelectedDevice!;
        src.Name = "A机";
        src.OkCountAddress = "D100";
        src.TargetCycle = 120;
        src.Alarms.Add(new Alarm { Name = "报警1", PlcAddress = "M10", DeviceId = src.Id });
        src.Defects.Add(new Defect { Name = "缺陷1", PlcAddress = "D200", DeviceId = src.Id });
        src.CountAlarms.Add(new CountAlarm { Name = "计数1", PlcAddress = "D300", DeviceId = src.Id });

        vm.IsDirty = false; // 复位脏标记前置条件
        vm.CopyDeviceCommand.Execute(null);

        Assert.Equal(2, vm.Devices.Count);
        var copy = vm.Devices[1];
        Assert.NotEqual(src.Id, copy.Id); // 新设备 Id
        Assert.Equal("A机 副本", copy.Name); // 唯一名
        Assert.Equal("D100", copy.OkCountAddress);
        Assert.Equal(120, copy.TargetCycle);
        // 子集合深拷且重新挂接 DeviceId（报警确定性 Id 因此不同）
        Assert.Single(copy.Alarms);
        Assert.Equal("M10", copy.Alarms[0].PlcAddress);
        Assert.NotEqual(src.Alarms[0].Id, copy.Alarms[0].Id);
        Assert.Single(copy.Defects);
        Assert.Equal("D200", copy.Defects[0].PlcAddress);
        Assert.Single(copy.CountAlarms);
        Assert.Equal("D300", copy.CountAlarms[0].PlcAddress);
        Assert.True(vm.IsDirty); // 复制后置脏
        Assert.Equal(copy, vm.SelectedDevice);
        Directory.Delete(tmp, true);
    }

    [Fact]
    public void CopyDevice_NoSelection_DoesNothing()
    {
        var (vm, _, _, tmp) = NewVm();
        vm.SelectedDevice = null;
        vm.CopyDeviceCommand.Execute(null);
        Assert.Empty(vm.Devices);
        Directory.Delete(tmp, true);
    }

    // ──────────── 拖拽排序 ────────────

    [Fact]
    public void MoveDevice_ReordersAndMarksDirty()
    {
        var (vm, _, _, tmp) = NewVm();
        void AddNamed(string name) { vm.AddDeviceCommand.Execute(null); vm.SelectedDevice!.Name = name; }
        AddNamed("A");
        AddNamed("B");
        AddNamed("C");

        var a = vm.Devices[0];
        var c = vm.Devices[2];
        Assert.Equal("A", vm.Devices[0].Name);
        Assert.Equal("C", vm.Devices[2].Name);

        vm.IsDirty = false;
        vm.MoveDevice(a, c); // 把 A 移到 C（末尾）位置

        Assert.Equal("B", vm.Devices[0].Name);
        Assert.Equal("A", vm.Devices[2].Name); // A 已移到末尾
        Assert.True(vm.IsDirty);
        Directory.Delete(tmp, true);
    }

    [Fact]
    public void MoveDevice_SameTarget_NoOp()
    {
        var (vm, _, _, tmp) = NewVm();
        vm.AddDeviceCommand.Execute(null);
        var a = vm.SelectedDevice!;
        vm.IsDirty = false;
        vm.MoveDevice(a, a); // 拖到自身
        Assert.False(vm.IsDirty); // 不应误报
        Directory.Delete(tmp, true);
    }

    // ──────────── 跨设备地址冲突检测 ────────────

    [Fact]
    public void CrossDeviceConflict_DetectedLiveAndOnSave()
    {
        var (vm, dialog, _, tmp) = NewVm();
        vm.AddDeviceCommand.Execute(null);
        var d1 = vm.SelectedDevice!;
        ConfigureAddresses(d1);
        d1.Name = "设备1";
        vm.AddDeviceCommand.Execute(null);
        var d2 = vm.SelectedDevice!;
        ConfigureAddresses(d2); // 与 d1 完全相同的 4 个地址 → 4 处冲突
        d2.Name = "设备2";

        // 实时标记：两设备共用全部 4 个 PLC 地址
        Assert.True(vm.HasAddressConflicts);
        Assert.Equal(4, vm.AddressConflictCount);

        // 保存时聚合错误列表应含地址冲突项且不持久化
        vm.SaveCommand.Execute(null);
        Assert.Contains(vm.ValidationErrors, e => e.Message.Contains("地址冲突"));
        Assert.Equal("D100, D102, D104, D106", vm.AddressConflictSummaries[d1.Id]);
        Assert.Equal("D100, D102, D104, D106", vm.AddressConflictSummaries[d2.Id]);
        Assert.Contains(dialog.Warning, w => w.Contains("保存失败"));
        Assert.DoesNotContain(dialog.Success, s => s.Contains("保存成功"));
        Directory.Delete(tmp, true);
    }

    [Fact]
    public void SelectAddressConflict_SelectsDeviceAndParameterTab()
    {
        var (vm, _, _, tmp) = NewVm();
        vm.AddDeviceCommand.Execute(null);
        var first = vm.SelectedDevice!;
        ConfigureAddresses(first);
        vm.AddDeviceCommand.Execute(null);
        var second = vm.SelectedDevice!;
        ConfigureAddresses(second);

        vm.SelectAddressConflictCommand.Execute(first);

        Assert.Same(first, vm.SelectedDevice);
        Assert.Equal(0, vm.SelectedTabIndex);
        Assert.Equal("D100", vm.FocusedAddressConflict);
        Directory.Delete(tmp, true);
    }

    [Fact]
    public void TryLeaveWithDirtyCheck_NoBlocks_YesKeepsDirtyState()
    {
        var (vm, dialog, _, tmp) = NewVm();
        vm.AddDeviceCommand.Execute(null);
        vm.IsDirty = true;
        dialog.ShowResult = System.Windows.MessageBoxResult.No;
        Assert.False(vm.TryLeaveWithDirtyCheck());
        Assert.True(vm.IsDirty);

        dialog.ShowResult = System.Windows.MessageBoxResult.Yes;
        Assert.True(vm.TryLeaveWithDirtyCheck());
        Assert.True(vm.IsDirty);
        Directory.Delete(tmp, true);
    }

    [Fact]
    public void CrossDeviceConflict_DistinctAddresses_NoConflict()
    {
        var (vm, _, _, tmp) = NewVm();
        vm.AddDeviceCommand.Execute(null);
        var d1 = vm.SelectedDevice!;
        ConfigureAddresses(d1);
        d1.Name = "设备1";
        vm.AddDeviceCommand.Execute(null);
        var d2 = vm.SelectedDevice!;
        ConfigureAddresses(d2);
        d2.Name = "设备2";
        d2.OkCountAddress = "D110"; // 仅 OK 地址不同 → 剩 3 处冲突
        d2.NgCountAddress = "D112";
        d2.StatusCountAddress = "D114";
        d2.ProductionResetAddress = "D116";

        Assert.False(vm.HasAddressConflicts);
        Assert.Equal(0, vm.AddressConflictCount);
        Directory.Delete(tmp, true);
    }

    // ──────────── 关闭前脏数据提醒 ────────────

    [Fact]
    public void TryCloseWithDirtyCheck_NotDirty_ReturnsTrue()
    {
        var (vm, dialog, _, tmp) = NewVm();
        Assert.False(vm.IsDirty);
        Assert.True(vm.TryCloseWithDirtyCheck());
        Assert.Empty(dialog.ShowCalls); // 无未保存则不弹确认框
        Directory.Delete(tmp, true);
    }

    [Fact]
    public void TryCloseWithDirtyCheck_Dirty_UserNo_CancelsClose()
    {
        var (vm, dialog, _, tmp) = NewVm();
        vm.AddDeviceCommand.Execute(null); // 置脏
        Assert.True(vm.IsDirty);
        dialog.ShowResult = System.Windows.MessageBoxResult.No;
        Assert.False(vm.TryCloseWithDirtyCheck()); // 取消关闭
        Assert.Contains(dialog.ShowCalls, c => c.Title == "未保存的修改");
        Directory.Delete(tmp, true);
    }

    [Fact]
    public void TryCloseWithDirtyCheck_Dirty_UserYes_AllowsClose()
    {
        var (vm, dialog, _, tmp) = NewVm();
        vm.AddDeviceCommand.Execute(null);
        Assert.True(vm.IsDirty);
        dialog.ShowResult = System.Windows.MessageBoxResult.Yes;
        Assert.True(vm.TryCloseWithDirtyCheck()); // 允许关闭（丢弃修改）
        Assert.Contains(dialog.ShowCalls, c => c.Title == "未保存的修改");
        Directory.Delete(tmp, true);
    }
}
