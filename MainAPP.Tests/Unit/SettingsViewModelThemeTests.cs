using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using LicenseManager.Services;
using Kanban.Collector.Core.Models;
using MainAPP.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using MainAPP.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// SettingsViewModel 主题相关逻辑与 Save 命令补充测试。
/// 重点覆盖：
/// - IsDarkTheme 持久化（通过 SettingsViewModel.AppSettings 暴露）
/// - IsDarkTheme 切换时触发 PropertyChanged 通知
/// - Save 命令对空班次配置的校验（ShiftValidator 返回 "至少配置一个班次"）
///
/// 与 <see cref="SettingsViewModelTests"/> 共用同一 xUnit Collection（SettingsVM），
/// 因 KANBAN_DATA_DIR 为进程级环境变量，DisableParallelization 避免与同类测试互相干扰。
/// </summary>
[Collection("SettingsVM")]
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class SettingsViewModelThemeTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppSettings _appSettings;
    private readonly FakePlcDriver _driver = new();
    private readonly PlcConnectionManager _conn;
    private readonly FakeDialogService _dialog = new();
    private readonly LicenseGate _licenseGate;
    private readonly IServiceProvider _services;

    public SettingsViewModelThemeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "kanban_settingsvm_theme_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        Environment.SetEnvironmentVariable("KANBAN_DATA_DIR", _tempDir);
        _appSettings = new AppSettings();
        _conn = new PlcConnectionManager(_driver, _appSettings);

        // 授权管理：用临时目录 + 内存版 RegistryBackup，避免污染测试机器
        var licenseStore = new LicenseStore(_tempDir);
        var registryBackup = new TrialRegistryBackupStub();
        var trialTracker = new TrialTracker(licenseStore, registryBackup);
        var attemptTracker = new ActivationAttemptTracker(_tempDir);
        _licenseGate = new LicenseGate(licenseStore, trialTracker, attemptTracker);
        _licenseGate.CheckStatus();
        _services = new ServiceCollection().BuildServiceProvider();
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { /* best-effort */ }
        Environment.SetEnvironmentVariable("KANBAN_DATA_DIR", null);
    }

    private SettingsViewModel NewVm() => new(_appSettings, _conn, _dialog, _licenseGate, _services);

    // ───────────── 主题持久化 ─────────────

    [Fact]
    public void IsDarkTheme_SaveAndLoad_RoundTrips()
    {
        // 默认为 false
        Assert.False(_appSettings.IsDarkTheme);

        // 通过 SettingsViewModel.DraftSettings 修改为 true，执行 Save 命令持久化
        var vm = NewVm();
        vm.DraftSettings.IsDarkTheme = true;
        vm.SaveCommand.Execute(null);

        // Save 应成功（默认班次配置通过校验）
        Assert.NotEmpty(_dialog.Success);
        Assert.Contains("设置已保存", _dialog.Success[0]);
        Assert.True(File.Exists(_appSettings.SettingsFilePath));

        // 重新构造 AppSettings 加载，应还原 IsDarkTheme = true
        var loaded = new AppSettings();
        loaded.Load();

        Assert.True(loaded.IsDarkTheme);

        // 再切换回 false 并保存，验证双向往返
        _appSettings.IsDarkTheme = false;
        NewVm().SaveCommand.Execute(null);

        var loaded2 = new AppSettings();
        loaded2.Load();
        Assert.False(loaded2.IsDarkTheme);
    }

    [Fact]
    public void IsDarkTheme_Toggle_RaisesPropertyChanged()
    {
        var vm = NewVm();
        var changedProps = new List<string?>();
        ((INotifyPropertyChanged)vm.AppSettings).PropertyChanged += (_, e) => changedProps.Add(e.PropertyName);

        // 初始为 false，切换到 true 应触发 PropertyChanged
        vm.AppSettings.IsDarkTheme = true;
        Assert.Contains(nameof(AppSettings.IsDarkTheme), changedProps);
        Assert.True(vm.AppSettings.IsDarkTheme);

        // 切换回 false 应再次触发
        var countBefore = changedProps.Count;
        vm.AppSettings.IsDarkTheme = false;
        Assert.Equal(countBefore + 1, changedProps.Count(name => name == nameof(AppSettings.IsDarkTheme)));
        Assert.False(vm.AppSettings.IsDarkTheme);
    }

    // ───────────── Save 命令校验失败路径（空班次） ─────────────

    [Fact]
    public void Save_WithEmptyShifts_ShowsWarning()
    {
        // 清空班次，使 ShiftValidator.Validate 返回 "至少配置一个班次"
        _appSettings.Shifts.Clear();
        Assert.Empty(_appSettings.Shifts);

        NewVm().SaveCommand.Execute(null);

        // 应 NotifyWarning，不弹确认框，不通知成功
        Assert.NotEmpty(_dialog.Warning);
        Assert.Contains("至少配置一个班次", _dialog.Warning[0]);
        Assert.Empty(_dialog.ShowCalls);
        Assert.Empty(_dialog.Success);
        // 文件未被写入
        Assert.False(File.Exists(_appSettings.SettingsFilePath));
    }
}
