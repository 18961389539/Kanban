using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using MainAPP.Data;
using MainAPP.Models;
using MainAPP.Services;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// DeviceConfigIOService 纯单元测试：覆盖 RollbackToBackup / HasBackup / ExportConfig / ImportConfig 全部分支。
/// 不依赖真实 PLC / 数据库，使用 FakeDialogService 控制对话框分支 + 临时目录隔离文件 IO。
/// 因 NewService 会临时改写 KANBAN_DATA_DIR 进程环境变量，本类加入 DeviceManagerVM 集合（已禁用并行化）以避免相互干扰。
/// </summary>
[Collection("DeviceManagerVM")]
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class DeviceConfigIOServiceTests
{
    private static (DeviceConfigIOService service, DeviceRepository repo, FakeDialogService dialog, string tmp) NewService()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "kanban_io_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        Directory.CreateDirectory(Path.Combine(tmp, "Config")); // AppSettings.GetFilePath 走 Config 子目录
        Environment.SetEnvironmentVariable("KANBAN_DATA_DIR", tmp);

        var appSettings = new AppSettings();
        var repo = new DeviceRepository(appSettings);
        var dialog = new FakeDialogService();
        var service = new DeviceConfigIOService(repo, dialog);
        return (service, repo, dialog, tmp);
    }

    private static Device NewDevice(string name = "测试设备") => new()
    {
        Name = name,
        OkCountAddress = "D100",
        NgCountAddress = "D102",
        StatusCountAddress = "D104",
        ProductionResetAddress = "D106",
        TargetCycle = 600
    };

    /// <summary>
    /// 用独立 DeviceRepository 序列化设备列表为合法 JSON（与 DeviceRepository.ImportFromJson 兼容）。
    /// 不复用被测 repo，避免污染测试初始状态。
    /// </summary>
    private static string MakeValidJson(params Device[] devices)
    {
        var helper = new DeviceRepository(new AppSettings());
        foreach (var d in devices) helper.Devices.Add(d);
        var tmpFile = Path.Combine(Path.GetTempPath(), "kanban_json_" + Guid.NewGuid().ToString("N") + ".json");
        helper.ExportToFile(tmpFile);
        var json = File.ReadAllText(tmpFile);
        File.Delete(tmpFile);
        return json;
    }

    // ──────────── RollbackToBackup ────────────

    [Fact]
    public void RollbackToBackup_NoBackupFile_WarnsAndReturnsNull()
    {
        var (service, repo, dialog, tmp) = NewService();
        Assert.False(File.Exists(repo.FilePath + ".bak"));

        var result = service.RollbackToBackup();

        Assert.Null(result);
        Assert.Contains(dialog.Warning, w => w.Contains("未找到上一版本备份文件"));
        Assert.Empty(dialog.ShowCalls); // 备份不存在 → 不弹确认框
        Assert.Empty(dialog.Error);
        Assert.Empty(dialog.Success);
        Directory.Delete(tmp, true);
    }

    [Fact]
    public void RollbackToBackup_ServiceHasNoConfirmDialog_ReplacesDirectly()
    {
        // 服务层不做二次确认（密码确认在 ViewModel 层），直接读取备份并替换
        var (service, repo, dialog, tmp) = NewService();
        repo.ReplaceAll(new[] { NewDevice("当前1"), NewDevice("当前2") });
        File.WriteAllText(repo.FilePath + ".bak", MakeValidJson(NewDevice("备份1")));

        var result = service.RollbackToBackup();

        Assert.NotNull(result);
        Assert.Single(result!);
        Assert.Empty(dialog.ShowCalls); // 服务层不弹确认框
        Assert.Contains(dialog.Success, s => s.Contains("已恢复上一版本"));
        Assert.Single(repo.Devices); // 已替换为备份内容
        Assert.Equal("备份1", repo.Devices[0].Name);
        Directory.Delete(tmp, true);
    }

    [Fact]
    public void RollbackToBackup_BadJson_NotifyErrorAndReturnsNull()
    {
        var (service, repo, dialog, tmp) = NewService();
        repo.ReplaceAll(new[] { NewDevice("当前1") });
        File.WriteAllText(repo.FilePath + ".bak", "{ invalid json }");

        dialog.ShowResult = MessageBoxResult.Yes;
        var result = service.RollbackToBackup();

        Assert.Null(result);
        Assert.Contains(dialog.Error, e => e.Contains("备份文件读取或解析失败"));
        Assert.Empty(dialog.Success);
        Assert.Empty(dialog.Warning);
        Assert.Single(repo.Devices); // 解析失败不替换
        Assert.Equal("当前1", repo.Devices[0].Name);
        Directory.Delete(tmp, true);
    }

    [Fact]
    public void RollbackToBackup_EmptyDeviceList_WarnsAndReturnsNull()
    {
        var (service, repo, dialog, tmp) = NewService();
        repo.ReplaceAll(new[] { NewDevice("当前1") });
        File.WriteAllText(repo.FilePath + ".bak", "[]");

        dialog.ShowResult = MessageBoxResult.Yes;
        var result = service.RollbackToBackup();

        Assert.Null(result);
        Assert.Contains(dialog.Warning, w => w.Contains("备份文件为空或无效"));
        Assert.Empty(dialog.Error);
        Assert.Empty(dialog.Success);
        Assert.Single(repo.Devices); // 空列表不替换
        Assert.Equal("当前1", repo.Devices[0].Name);
        Directory.Delete(tmp, true);
    }

    [Fact]
    public void RollbackToBackup_ValidJson_ReplacesAndReturnsDevices()
    {
        var (service, repo, dialog, tmp) = NewService();
        repo.ReplaceAll(new[] { NewDevice("当前1") });
        File.WriteAllText(repo.FilePath + ".bak", MakeValidJson(NewDevice("备份1"), NewDevice("备份2")));

        dialog.ShowResult = MessageBoxResult.Yes;
        var result = service.RollbackToBackup();

        Assert.NotNull(result);
        Assert.Equal(2, result!.Count);
        Assert.Contains(dialog.Success, s => s.Contains("已恢复上一版本"));
        Assert.Empty(dialog.Warning);
        Assert.Empty(dialog.Error);
        // 内存中设备已被替换为备份内容
        Assert.Equal(2, repo.Devices.Count);
        Assert.Contains(repo.Devices, d => d.Name == "备份1");
        Assert.Contains(repo.Devices, d => d.Name == "备份2");
        // Runtime 同步重建
        Assert.Equal(2, repo.Runtimes.Count);
        Directory.Delete(tmp, true);
    }

    // ──────────── HasBackup ────────────

    [Fact]
    public void HasBackup_NoBakFile_ReturnsFalse()
    {
        var (service, repo, _, tmp) = NewService();
        Assert.False(File.Exists(repo.FilePath + ".bak"));
        Assert.False(service.HasBackup);
        Directory.Delete(tmp, true);
    }

    [Fact]
    public void HasBackup_BakFileExists_ReturnsTrue()
    {
        var (service, repo, _, tmp) = NewService();
        File.WriteAllText(repo.FilePath + ".bak", "[]");
        Assert.True(service.HasBackup);
        Directory.Delete(tmp, true);
    }

    // ──────────── ExportConfig ────────────

    [Fact]
    public void ExportConfig_UserCancelsSaveDialog_ReturnsFalse()
    {
        var (service, repo, dialog, tmp) = NewService();
        repo.ReplaceAll(new[] { NewDevice("设备1") });
        dialog.SaveFilePath = null; // 用户取消保存对话框

        var result = service.ExportConfig(deviceCount: 1);

        Assert.False(result);
        Assert.Empty(dialog.Success);
        Assert.Empty(dialog.Error);
        Assert.Empty(dialog.Warning);
        Directory.Delete(tmp, true);
    }

    [Fact]
    public void ExportConfig_Success_ReturnsTrueAndNotifies()
    {
        var (service, repo, dialog, tmp) = NewService();
        repo.ReplaceAll(new[] { NewDevice("设备1"), NewDevice("设备2") });
        var exportPath = Path.Combine(tmp, "export.json");
        dialog.SaveFilePath = exportPath;

        var result = service.ExportConfig(deviceCount: 2);

        Assert.True(result);
        Assert.True(File.Exists(exportPath));
        Assert.Contains(dialog.Success, s => s.Contains("已导出") && s.Contains("2"));
        Assert.Empty(dialog.Error);
        Directory.Delete(tmp, true);
    }

    [Fact]
    public void ExportConfig_Throws_NotifyErrorAndReturnsFalse()
    {
        var (service, repo, dialog, tmp) = NewService();
        repo.ReplaceAll(new[] { NewDevice("设备1") });
        // 指向不存在目录的路径 → ExportToFile 内部 File.WriteAllText 抛 DirectoryNotFoundException
        var badPath = Path.Combine(tmp, "nonexistent_subdir", "export.json");
        dialog.SaveFilePath = badPath;

        var result = service.ExportConfig(deviceCount: 1);

        Assert.False(result);
        Assert.Contains(dialog.Error, e => e.Contains("导出失败"));
        Assert.Empty(dialog.Success);
        Assert.False(File.Exists(badPath));
        Directory.Delete(tmp, true);
    }

    // ──────────── ImportConfig ────────────

    [Fact]
    public void ImportConfig_UserCancelsOpenDialog_ReturnsNull()
    {
        var (service, repo, dialog, tmp) = NewService();
        repo.ReplaceAll(new[] { NewDevice("当前1") });
        dialog.OpenFilePath = null; // 用户取消打开对话框

        var result = service.ImportConfig(currentDeviceCount: 1);

        Assert.Null(result);
        Assert.Empty(dialog.Success);
        Assert.Empty(dialog.Error);
        Assert.Empty(dialog.Warning);
        Assert.Empty(dialog.ShowCalls); // 未进入二次确认
        Assert.Single(repo.Devices); // 未替换
        Directory.Delete(tmp, true);
    }

    [Fact]
    public void ImportConfig_BadJson_NotifyErrorAndReturnsNull()
    {
        var (service, repo, dialog, tmp) = NewService();
        repo.ReplaceAll(new[] { NewDevice("当前1") });
        var importPath = Path.Combine(tmp, "bad.json");
        File.WriteAllText(importPath, "{ this is not valid json ");
        dialog.OpenFilePath = importPath;

        var result = service.ImportConfig(currentDeviceCount: 1);

        Assert.Null(result);
        Assert.Contains(dialog.Error, e => e.Contains("文件读取或解析失败"));
        Assert.Empty(dialog.Success);
        Assert.Empty(dialog.Warning);
        Assert.Empty(dialog.ShowCalls); // 解析失败不进入二次确认
        Assert.Single(repo.Devices); // 未替换
        Directory.Delete(tmp, true);
    }

    [Fact]
    public void ImportConfig_EmptyDeviceList_NotifyWarningAndReturnsNull()
    {
        var (service, repo, dialog, tmp) = NewService();
        repo.ReplaceAll(new[] { NewDevice("当前1") });
        var importPath = Path.Combine(tmp, "empty.json");
        File.WriteAllText(importPath, "[]");
        dialog.OpenFilePath = importPath;

        var result = service.ImportConfig(currentDeviceCount: 1);

        Assert.Null(result);
        Assert.Contains(dialog.Warning, w => w.Contains("没有设备数据"));
        Assert.Empty(dialog.Error);
        Assert.Empty(dialog.Success);
        Assert.Empty(dialog.ShowCalls); // 空列表不进入二次确认
        Assert.Single(repo.Devices); // 未替换
        Directory.Delete(tmp, true);
    }

    [Fact]
    public void ImportConfig_UserCancelsConfirm_ReturnsNull_NoReplaceAll()
    {
        var (service, repo, dialog, tmp) = NewService();
        repo.ReplaceAll(new[] { NewDevice("当前1") });
        var importPath = Path.Combine(tmp, "import.json");
        File.WriteAllText(importPath, MakeValidJson(NewDevice("导入1"), NewDevice("导入2")));
        dialog.OpenFilePath = importPath;
        dialog.ShowResult = MessageBoxResult.No;

        var result = service.ImportConfig(currentDeviceCount: 1);

        Assert.Null(result);
        Assert.Contains(dialog.ShowCalls, c => c.Title == "确认导入");
        Assert.Empty(dialog.Success);
        Assert.Empty(dialog.Warning);
        Assert.Empty(dialog.Error);
        Assert.Single(repo.Devices); // 未触发 ReplaceAll
        Assert.Equal("当前1", repo.Devices[0].Name);
        Directory.Delete(tmp, true);
    }

    [Fact]
    public void ImportConfig_Success_ReplacesAndReturnsDevices()
    {
        var (service, repo, dialog, tmp) = NewService();
        repo.ReplaceAll(new[] { NewDevice("当前1") });
        var importPath = Path.Combine(tmp, "import.json");
        File.WriteAllText(importPath, MakeValidJson(NewDevice("导入1"), NewDevice("导入2")));
        dialog.OpenFilePath = importPath;
        dialog.ShowResult = MessageBoxResult.Yes;

        var result = service.ImportConfig(currentDeviceCount: 1);

        Assert.NotNull(result);
        Assert.Equal(2, result!.Count);
        Assert.Contains(dialog.Success, s => s.Contains("已导入"));
        Assert.Empty(dialog.Warning);
        Assert.Empty(dialog.Error);
        Assert.Equal(2, repo.Devices.Count);
        Assert.Contains(repo.Devices, d => d.Name == "导入1");
        Assert.Contains(repo.Devices, d => d.Name == "导入2");
        Assert.Equal(2, repo.Runtimes.Count); // Runtime 同步替换
        Directory.Delete(tmp, true);
    }
}
