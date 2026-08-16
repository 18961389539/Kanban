using System;
using System.IO;
using System.Windows;
using LicenseManager.Services;
using Kanban.Collector.Core.Models;
using MainAPP.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using MainAPP.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// 设置页 ViewModel 纯单元测试（不依赖真实 PLC / 数据库 / UI）。
/// 重点覆盖 Save 的命令分支：校验失败、PLC 配置变更、班次变更确认(Yes/No)、保存异常，
/// 以及 AddShift / RemoveShift 逻辑。
/// 因异常路径测试需临时改写 KANBAN_DATA_DIR 进程环境变量，本类关闭并行化以避免干扰其他测试类。
/// </summary>
[CollectionDefinition("SettingsVM", DisableParallelization = true)]
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class SettingsVMCollectionDefinition { }

[Collection("SettingsVM")]
public class SettingsViewModelTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppSettings _appSettings;
    private readonly FakePlcDriver _driver = new();
    private readonly PlcConnectionManager _conn;
    private readonly FakeDialogService _dialog = new();
    private readonly LicenseStore _licenseStore;
    private readonly TrialRegistryBackupStub _registryBackup;
    private readonly TrialTracker _trialTracker;
    private readonly ActivationAttemptTracker _attemptTracker;
    private readonly LicenseGate _licenseGate;
    private readonly IServiceProvider _services;

    public SettingsViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "kanban_settingsvm_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        Environment.SetEnvironmentVariable("KANBAN_DATA_DIR", _tempDir);
        _appSettings = new AppSettings();
        _conn = new PlcConnectionManager(_driver, _appSettings);

        // 授权管理：用临时目录 + 内存版 RegistryBackup，避免污染测试机器
        _licenseStore = new LicenseStore(_tempDir);
        _registryBackup = new TrialRegistryBackupStub();
        _trialTracker = new TrialTracker(_licenseStore, _registryBackup);
        _attemptTracker = new ActivationAttemptTracker(_tempDir);
        _licenseGate = new LicenseGate(_licenseStore, _trialTracker, _attemptTracker);
        _licenseGate.CheckStatus();  // 初始化状态（首次启动 → 试用期）
        _services = new ServiceCollection().BuildServiceProvider();
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { /* best-effort */ }
        Environment.SetEnvironmentVariable("KANBAN_DATA_DIR", null);
    }

    private SettingsViewModel NewVm() => new(_appSettings, _conn, _dialog, _licenseGate, _services);

    /// <summary>
    /// 保存后审计前后值应反映「上次已保存」与「本次草稿」的真实差异，
    /// 而非把已编辑的草稿误记为 before（P1 修复回归测试）。
    /// </summary>
    [Fact]
    public void Save_AuditBeforeReflectsLastSavedNotEditedDraft()
    {
        _appSettings.PlcConfig.IpAddress = "192.168.1.2";
        var auditService = NSubstitute.Substitute.For<IAuditService>();
        AuditLog.ResetForTest();
        AuditLog.Initialize(auditService, () => "tester");
        try
        {
            var vm = NewVm(); // 构造时基线 = 192.168.1.2
            vm.DraftSettings.PlcConfig.IpAddress = "10.0.0.99"; // 编辑草稿
            vm.SaveCommand.Execute(null);

            var call = Assert.Single(auditService.ReceivedCalls());
            var args = call.GetArguments();
            Assert.Equal("Settings.Update", args[0]);
            Assert.Contains("192.168.1.2", (string)args[6]!); // before = 上次已保存的旧 IP
            Assert.Contains("10.0.0.99", (string)args[7]!);   // after = 本次保存的新 IP
        }
        finally
        {
            AuditLog.ResetForTest();
        }
    }

    /// <summary>
    /// 回归：CopySettings 必须拷贝 DataMode/RunMode/CollectorHubUrl，否则保存后改动被静默丢弃（审查修复 2026-08-13）。
    /// </summary>
    [Fact]
    public void Save_DataModeRunModeAndHubUrl_AreCopiedToAppSettings()
    {
        var vm = NewVm();
        vm.DraftSettings.DataMode = KanbanDataMode.Remote;
        vm.DraftSettings.RunMode = KanbanRunMode.Viewer;
        vm.DraftSettings.CollectorHubUrl = "http://192.168.1.50:5129/hubs/kanban";

        vm.SaveCommand.Execute(null);

        Assert.Equal(KanbanDataMode.Remote, _appSettings.DataMode);
        Assert.Equal(KanbanRunMode.Viewer, _appSettings.RunMode);
        Assert.Equal("http://192.168.1.50:5129/hubs/kanban", _appSettings.CollectorHubUrl);
    }

    // ───────────── 校验失败分支 ─────────────

    [Fact]
    public void Save_EmptyIp_NotifiesWarningAndDoesNotSave()
    {
        _appSettings.PlcConfig.IpAddress = "";
        NewVm().SaveCommand.Execute(null);

        Assert.NotEmpty(_dialog.Warning);
        Assert.Contains("IP 地址不能为空", _dialog.Warning[0]);
        Assert.Empty(_dialog.Success);
    }

    // ───────────── 测试连接：单连接限制提示 ─────────────

    private SettingsViewModel NewVmWithTestFactory(ISharedPlcDriverFactory factory)
    {
        var services = new ServiceCollection();
        services.AddSingleton(factory);
        return new SettingsViewModel(_appSettings, _conn, _dialog, _licenseGate, services.BuildServiceProvider());
    }

    [Fact]
    public async Task TestConnection_RefusedWhileAcquisitionConnected_SameEndpoint_ShowsSingleConnectionHint()
    {
        // 主采集已连接同一端点
        _appSettings.PlcConfig.IpAddress = "10.0.0.8";
        _appSettings.PlcConfig.Port = 6000;
        _conn.EnsureConnected();
        Assert.True(_conn.IsConnected);

        // 测试连接工厂返回"连接被拒绝（TCP 拒绝）"的临时驱动
        var factory = Substitute.For<ISharedPlcDriverFactory>();
        var testDriver = new FakePlcDriver { ShouldFailConnect = true, ConnectFailureKind = PlcErrorKind.ConnectionLost };
        factory.Create(Arg.Any<PlcConfig>()).Returns(testDriver);
        var vm = NewVmWithTestFactory(factory);

        await vm.TestConnectionCommand.ExecuteAsync(null);

        Assert.Equal("Error", vm.TestConnectionResultType);
        Assert.Contains("仅允许单个连接", vm.TestConnectionResult);
    }

    [Fact]
    public async Task TestConnection_RefusedWithoutAcquisition_ShowsGenericError()
    {
        // 主采集未连接：即使连接被拒绝也走通用失败文案，不提示单连接限制
        _appSettings.PlcConfig.IpAddress = "10.0.0.8";
        _appSettings.PlcConfig.Port = 6000;

        var factory = Substitute.For<ISharedPlcDriverFactory>();
        var testDriver = new FakePlcDriver { ShouldFailConnect = true, ConnectFailureKind = PlcErrorKind.ConnectionLost };
        factory.Create(Arg.Any<PlcConfig>()).Returns(testDriver);
        var vm = NewVmWithTestFactory(factory);

        await vm.TestConnectionCommand.ExecuteAsync(null);

        Assert.Equal("Error", vm.TestConnectionResultType);
        Assert.Contains("连接失败", vm.TestConnectionResult);
        Assert.DoesNotContain("仅允许单个连接", vm.TestConnectionResult);
    }

    [Fact]
    public async Task TestConnection_RefusedWhileAcquisitionConnected_DifferentEndpoint_ShowsGenericError()
    {
        // 主采集连着 A，测试连接的是 B（端点不同）：不是单连接限制，走通用文案
        _appSettings.PlcConfig.IpAddress = "10.0.0.8";
        _appSettings.PlcConfig.Port = 6000;
        _conn.EnsureConnected();

        var factory = Substitute.For<ISharedPlcDriverFactory>();
        var testDriver = new FakePlcDriver { ShouldFailConnect = true, ConnectFailureKind = PlcErrorKind.ConnectionLost };
        factory.Create(Arg.Any<PlcConfig>()).Returns(testDriver);
        var vm = NewVmWithTestFactory(factory);
        vm.DraftSettings.PlcConfig.IpAddress = "10.0.0.9"; // 草稿端点与运行实例不同

        await vm.TestConnectionCommand.ExecuteAsync(null);

        Assert.Equal("Error", vm.TestConnectionResultType);
        Assert.Contains("连接失败", vm.TestConnectionResult);
        Assert.DoesNotContain("仅允许单个连接", vm.TestConnectionResult);
    }

    [Fact]
    public async Task TestConnection_TimeoutWhileAcquisitionConnected_SameEndpoint_ShowsGenericError()
    {
        // 超时（非 ConnectionLost）不触发单连接提示
        _appSettings.PlcConfig.IpAddress = "10.0.0.8";
        _appSettings.PlcConfig.Port = 6000;
        _conn.EnsureConnected();

        var factory = Substitute.For<ISharedPlcDriverFactory>();
        var testDriver = new FakePlcDriver { ShouldFailConnect = true, ConnectFailureKind = PlcErrorKind.Timeout };
        factory.Create(Arg.Any<PlcConfig>()).Returns(testDriver);
        var vm = NewVmWithTestFactory(factory);

        await vm.TestConnectionCommand.ExecuteAsync(null);

        Assert.Equal("Error", vm.TestConnectionResultType);
        Assert.Contains("连接失败", vm.TestConnectionResult);
        Assert.DoesNotContain("仅允许单个连接", vm.TestConnectionResult);
    }

    [Fact]
    public void Save_InvalidIp_NotifiesWarning()
    {
        _appSettings.PlcConfig.IpAddress = "999.1.1.1";
        NewVm().SaveCommand.Execute(null);

        Assert.NotEmpty(_dialog.Warning);
        Assert.Contains("无效的 IP 地址", _dialog.Warning[0]);
        Assert.Empty(_dialog.Success);
    }

    [Fact]
    public void Save_PortOutOfRange_NotifiesWarning()
    {
        _appSettings.PlcConfig.Port = 0;
        NewVm().SaveCommand.Execute(null);

        Assert.NotEmpty(_dialog.Warning);
        Assert.Contains("端口号必须在 1-65535 之间", _dialog.Warning[0]);
        Assert.Empty(_dialog.Success);
    }

    [Fact]
    public void Save_PollingIntervalTooSmall_NotifiesWarning()
    {
        _appSettings.PollingIntervalMs = 10;
        NewVm().SaveCommand.Execute(null);

        Assert.NotEmpty(_dialog.Warning);
        Assert.Contains("轮询间隔不能小于 50ms", _dialog.Warning[0]);
        Assert.Empty(_dialog.Success);
    }

    [Fact]
    public void Save_HistoryIntervalTooSmall_NotifiesWarning()
    {
        _appSettings.HistoryWriteIntervalScans = 0;
        NewVm().SaveCommand.Execute(null);

        Assert.NotEmpty(_dialog.Warning);
        Assert.Contains("历史写入间隔不能小于 1", _dialog.Warning[0]);
        Assert.Empty(_dialog.Success);
    }

    // ───────────── PLC 配置变更 → 断开重连 ─────────────

    [Fact]
    public void Save_PlcConfigChanged_DisconnectsAndNotifiesSuccess()
    {
        _conn.EnsureConnected();
        Assert.True(_conn.IsConnected);
        Assert.True(_driver.IsConnected);

        var vm = NewVm(); // 先构造以捕获初始 IP
        vm.DraftSettings.PlcConfig.IpAddress = "10.0.0.9"; // 与初始 192.168.1.2 不同
        vm.SaveCommand.Execute(null);

        // Save 内 _connectionManager.Disconnect() 已调用：连接被断开
        Assert.False(_conn.IsConnected);
        Assert.False(_driver.IsConnected);
        Assert.Single(_dialog.Success);
        Assert.Contains("设置已保存", _dialog.Success[0]);
    }

    [Fact]
    public void Save_PlcConfigUnchanged_DoesNotDisconnect()
    {
        _conn.EnsureConnected();
        Assert.True(_conn.IsConnected);

        // 不修改 IP/端口，保持与构造时一致
        NewVm().SaveCommand.Execute(null);

        Assert.True(_conn.IsConnected); // 未触发断开
        Assert.Single(_dialog.Success);
    }

    // ───────────── 班次配置变更 → 确认框 ─────────────

    [Fact]
    public void Save_ShiftConfigChanged_UserClicksNo_AbortsWithoutSaving()
    {
        _dialog.ShowResult = MessageBoxResult.No;
        var vm = NewVm(); // 先构造以捕获初始班次签名
        vm.DraftSettings.Shifts[0].Name = "早班(已改)"; // 改变签名

        vm.SaveCommand.Execute(null);

        Assert.Single(_dialog.ShowCalls); // 弹出了确认框
        Assert.Contains("班次配置", _dialog.ShowCalls[0].Message);
        Assert.Empty(_dialog.Success);    // 未保存
    }

    [Fact]
    public void Save_ShiftConfigChanged_UserClicksYes_SavesSuccessfully()
    {
        _dialog.ShowResult = MessageBoxResult.Yes;
        var vm = NewVm(); // 先构造以捕获初始班次签名
        vm.DraftSettings.Shifts[0].Name = "早班(已改)";

        vm.SaveCommand.Execute(null);

        Assert.Single(_dialog.ShowCalls);
        Assert.Single(_dialog.Success);
        Assert.Contains("设置已保存", _dialog.Success[0]);
    }

    [Fact]
    public void Save_ShiftConfigUnchanged_NoConfirmDialog()
    {
        _dialog.ShowResult = MessageBoxResult.No; // 即便默认 No，也不应弹确认框
        NewVm().SaveCommand.Execute(null);

        Assert.Empty(_dialog.ShowCalls);
        Assert.Single(_dialog.Success);
    }

    // ───────────── 保存异常 → 错误通知 ─────────────

    [Fact]
    public void Save_PersistThrows_NotifiesError()
    {
        // 将 DataRoot 指向一个“已存在的文件”，使 Save 内 EnsureDirectory 抛 IO 异常
        var fileDir = Path.Combine(Path.GetTempPath(), "kanban_settingsvm_file_" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(fileDir, "");
        var prev = Environment.GetEnvironmentVariable("KANBAN_DATA_DIR");
        Environment.SetEnvironmentVariable("KANBAN_DATA_DIR", fileDir);
        try
        {
            var appSettings = new AppSettings(); // DataRoot = 该文件路径
            var conn = new PlcConnectionManager(_driver, appSettings);
            var vm = new SettingsViewModel(appSettings, conn, _dialog, _licenseGate, _services);

            vm.SaveCommand.Execute(null);

            Assert.Single(_dialog.Error);
            Assert.Contains("保存失败", _dialog.Error[0]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("KANBAN_DATA_DIR", prev);
            try { File.Delete(fileDir); } catch { /* best-effort */ }
        }
    }

    // ───────────── 班次增删 ─────────────

    [Fact]
    public void AddShift_IncreasesShiftCount()
    {
        var vm = NewVm();
        var before = vm.DraftSettings.Shifts.Count;
        vm.AddShiftCommand.Execute(null);
        Assert.Equal(before + 1, vm.DraftSettings.Shifts.Count);
        Assert.Equal(before, _appSettings.Shifts.Count);
    }

    [Fact]
    public void RemoveShift_WithMultipleShifts_RemovesOne()
    {
        // 默认至少 2 个班次
        Assert.True(_appSettings.Shifts.Count > 1);
        var vm = NewVm();
        var target = vm.DraftSettings.Shifts[0];
        var before = vm.DraftSettings.Shifts.Count;

        vm.RemoveShiftCommand.Execute(target);

        Assert.Equal(before - 1, vm.DraftSettings.Shifts.Count);
        Assert.DoesNotContain(target, vm.DraftSettings.Shifts);
    }

    [Fact]
    public void RemoveShift_WhenOnlyOne_KeepsOneAndNotifiesInfo()
    {
        _appSettings.Shifts.Clear();
        _appSettings.Shifts.Add(new ShiftConfig { Name = "唯一班次" });
        Assert.Single(_appSettings.Shifts);

        var vm = NewVm();
        vm.RemoveShiftCommand.Execute(vm.DraftSettings.Shifts[0]);

        Assert.Single(vm.DraftSettings.Shifts); // 不允许删到 0
        Assert.NotEmpty(_dialog.Info);
        Assert.Contains("至少保留一个班次", _dialog.Info[0]);
    }

    // ───────────── 看板标题 ─────────────

    [Fact]
    public void Save_AppTitle_AppliedAndPersisted()
    {
        var vm = NewVm();
        vm.DraftSettings.AppTitle = "一号车间看板";
        vm.SaveCommand.Execute(null);

        Assert.Contains("设置已保存", _dialog.Success[0]);
        Assert.Equal("一号车间看板", _appSettings.AppTitle); // 保存后写入 AppSettings（窗口标题绑定源）
    }

    [Fact]
    public void Save_EmptyAppTitle_NotifiesWarning()
    {
        var vm = NewVm();
        vm.DraftSettings.AppTitle = "   ";
        vm.SaveCommand.Execute(null);

        Assert.NotEmpty(_dialog.Warning);
        Assert.Contains("看板标题不能为空", _dialog.Warning[0]);
        Assert.Empty(_dialog.Success);
    }

    // ───────────── 界面语言 ─────────────

    [Fact]
    public void Save_LanguageChanged_AppliesToAppSettings_AndNotifiesRestartRequired()
    {
        var vm = NewVm();
        vm.DraftSettings.Language = AppLanguage.En;
        vm.SaveCommand.Execute(null);

        Assert.Contains("设置已保存", _dialog.Success[0]);
        Assert.Equal(AppLanguage.En, _appSettings.Language); // 持久化到 AppSettings（启动时应用）
        Assert.Contains("重启", _dialog.Info[0]); // 语言切换提示重启生效
    }

    [Fact]
    public void Save_LanguageUnchanged_NoRestartPrompt()
    {
        var vm = NewVm();
        vm.SaveCommand.Execute(null); // 语言保持默认中文

        Assert.Contains("设置已保存", _dialog.Success[0]);
        Assert.Empty(_dialog.Info); // 未变语言不弹重启提示
    }
}
