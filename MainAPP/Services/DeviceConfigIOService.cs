using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using Kanban.Core.Data;
using Kanban.Core.Models;
using Kanban.Core.Services;
using MainAPP.Models;
using MainAPP.Resources;

namespace MainAPP.Services;

/// <summary>
/// 设备配置导入/导出/回滚服务：从 JSON 文件导入、导出到 JSON 文件、从 .bak 恢复上一版本。
/// 二次确认对话框（替换/恢复会丢弃当前配置）由本服务通过 IDialogService 弹出，
/// 替换设备列表的实际工作（ReplaceAll）也由本服务委托 DeviceRepository 完成。
/// 调用方（ViewModel）仅负责：执行后更新 SelectedDevice、刷新命令可用状态、置脏标记。
/// </summary>
public class DeviceConfigIOService(DeviceRepository deviceRepository, IDialogService dialog)
{
    // 文件对话框过滤器（三语资源，与 RecipeJsonIOService 同源 K695）
    private static string DeviceFileFilter => Strings.K695;

    /// <summary>
    /// 导出当前全部设备配置到用户选择的 JSON 文件（原子写入，备份上一版本）。
    /// 仅导出内存中的配置、不触发持久化或脏标记变化（导出是只读操作）。
    /// 用户取消保存对话框则不写文件。
    /// </summary>
    /// <param name="deviceCount">当前设备数量，用于成功提示文案。</param>
    /// <returns>是否导出成功（用户取消或写盘失败返回 false）。</returns>
    public bool ExportConfig(int deviceCount)
    {
        var path = dialog.ShowSaveFileDialog(Strings.M226, "devices.json", DeviceFileFilter);
        if (string.IsNullOrEmpty(path)) return false;

        try
        {
            deviceRepository.ExportToFile(path);
            dialog.NotifySuccess(string.Format(Strings.F105, deviceCount, Path.GetFileName(path)));
            return true;
        }
        catch (Exception ex)
        {
            dialog.NotifyError(string.Format(Strings.F090, ex.Message));
            return false;
        }
    }

    /// <summary>
    /// 从用户选择的 JSON 文件导入设备配置，整体替换当前内存中的设备（含运行时状态）。
    /// 导入前二次确认（替换会丢弃当前未保存的配置），导入后由调用方标记脏并提示用户保存以持久化。
    /// 文件解析失败或文件无设备数据时给出对应提示，不替换。
    /// </summary>
    /// <returns>导入的设备列表；用户取消、解析失败或文件为空时返回 null。</returns>
    public List<Device>? ImportConfig(int currentDeviceCount)
    {
        var path = dialog.ShowOpenFileDialog(Strings.M227, DeviceFileFilter);
        if (string.IsNullOrEmpty(path)) return null;

        List<Device>? imported;
        try
        {
            var json = File.ReadAllText(path);
            imported = deviceRepository.ImportFromJson(json);
        }
        catch (Exception ex)
        {
            dialog.NotifyError(string.Format(Strings.F134, ex.Message));
            return null;
        }

        if (imported == null || imported.Count == 0)
        {
            dialog.NotifyWarning(Strings.M003);
            return null;
        }

        // 二次确认：替换会丢弃当前内存中的设备配置（含未保存改动）
        var confirm = dialog.Show(
            string.Format(Strings.F089, imported.Count, currentDeviceCount),
            Strings.M119, MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return null;

        deviceRepository.ReplaceAll(imported);
        dialog.NotifySuccess(string.Format(Strings.F101, imported.Count));
        return imported;
    }

    /// <summary>
    /// 恢复上一版本：复用 AppSettings.WriteFileAtomically 每次保存前留下的 devices.json.bak，
    /// 把上一次保存前的配置加载回内存（走已验证的 ReplaceAll 路径，自动重建 Runtimes/订阅）。
    /// 安全验证（密码确认）由调用方 ViewModel 在调用前完成；本方法只负责读取备份并替换，
    /// 不再做二次确认对话框。
    /// </summary>
    /// <returns>恢复的设备列表；备份不存在、解析失败时返回 null。</returns>
    public List<Device>? RollbackToBackup()
    {
        var backupPath = deviceRepository.FilePath + ".bak";
        if (!File.Exists(backupPath))
        {
            dialog.NotifyWarning(Strings.M004);
            return null;
        }

        List<Device>? restored;
        try
        {
            var json = File.ReadAllText(backupPath);
            restored = deviceRepository.ImportFromJson(json);
        }
        catch (Exception ex)
        {
            dialog.NotifyError(string.Format(Strings.F084, ex.Message));
            return null;
        }

        if (restored == null || restored.Count == 0)
        {
            dialog.NotifyWarning(Strings.M005);
            return null;
        }

        deviceRepository.ReplaceAll(restored);
        dialog.NotifySuccess(string.Format(Strings.F108, restored.Count));
        return restored;
    }

    /// <summary>是否存在可恢复的 .bak 备份文件。</summary>
    public bool HasBackup => File.Exists(deviceRepository.FilePath + ".bak");
}
