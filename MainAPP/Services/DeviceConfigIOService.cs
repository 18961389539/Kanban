using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Models;
using MainAPP.Resources;

namespace MainAPP.Services;

/// <summary>
/// 设备配置导入/导出/回滚服务：从 JSON 文件导入、导出到 JSON 文件、从 .bak 恢复上一版本。
/// 二次确认对话框（替换/恢复会丢弃当前配置）由本服务通过 IDialogService 弹出，
/// 替换设备列表的实际工作（ReplaceAll）也由本服务委托 DeviceRepository 完成。
/// 调用方（ViewModel）仅负责：执行后更新 SelectedDevice、刷新命令可用状态、置脏标记。
/// </summary>
public class DeviceConfigIOService(
    DeviceRepository deviceRepository,
    IDialogService dialog,
    IRemoteDeviceConfigurationStore? remoteStore = null)
{
    private int _remoteBackupAvailable;

    /// <summary>Remote 备份状态发生变化时通知设备管理 VM 更新回滚命令。</summary>
    public event EventHandler? BackupAvailabilityChanged;

    // 文件对话框过滤器（本地化资源，与 RecipeJsonIOService 同源 K695）
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
    /// 文件解析失败时给出提示，不替换；合法的空数组表示删除全部设备并允许用户确认。
    /// </summary>
    /// <returns>导入的设备列表；用户取消、解析失败或取消确认时返回 null。</returns>
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

        if (imported == null)
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
    /// 恢复上一版本：Local 模式复用本地 devices.json.bak；Remote 模式委托 Collector 读取其自有备份，
    /// 并把 Collector 返回的权威配置加载回内存（走已验证的 ReplaceAll 路径，自动重建 Runtimes/订阅）。
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

        if (restored == null)
        {
            dialog.NotifyWarning(Strings.M005);
            return null;
        }

        deviceRepository.ReplaceAll(restored);
        dialog.NotifySuccess(string.Format(Strings.F108, restored.Count));
        return restored;
    }

    /// <summary>异步恢复上一版本，Remote 模式不读取 MainAPP 本地备份。</summary>
    public async Task<List<Device>?> RollbackToBackupAsync()
    {
        if (remoteStore?.IsEnabled == true)
        {
            IReadOnlyList<Device>? restored;
            try
            {
            restored = await remoteStore.RollbackDevicesAsync();
            }
            catch (Exception ex)
            {
                dialog.NotifyError(string.Format(Strings.F084, ex.Message));
                return null;
            }

            if (restored == null)
            {
                SetRemoteBackupAvailability(false);
                dialog.NotifyWarning(Strings.M004);
                return null;
            }

            var restoredList = restored.ToList();
            deviceRepository.ReplaceAll(restoredList);
            SetRemoteBackupAvailability(true);
            dialog.NotifySuccess(string.Format(Strings.F108, restoredList.Count));
            return restoredList;
        }

        return RollbackToBackup();
    }

    /// <summary>
    /// 是否存在可恢复备份。Remote 模式只在 Collector 返回真实存在后才为 true；
    /// 查询尚未完成或查询失败时保持 false，避免按钮显示为可用但点击必然失败。
    /// </summary>
    public bool HasBackup => IsRemote
        ? Volatile.Read(ref _remoteBackupAvailable) != 0
        : File.Exists(deviceRepository.FilePath + ".bak");

    /// <summary>刷新 Collector 侧设备备份状态；Remote 回滚按钮由状态变化事件驱动刷新。</summary>
    public async Task<bool> RefreshRemoteBackupAvailabilityAsync()
    {
        if (!IsRemote || remoteStore == null)
        {
            SetRemoteBackupAvailability(false);
            return false;
        }

        try
        {
            var available = await remoteStore.HasDeviceBackupAsync();
            SetRemoteBackupAvailability(available);
            return available;
        }
        catch
        {
            SetRemoteBackupAvailability(false);
            return false;
        }
    }

    private void SetRemoteBackupAvailability(bool available)
    {
        var value = available ? 1 : 0;
        if (Interlocked.Exchange(ref _remoteBackupAvailable, value) == value) return;
        BackupAvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>是否已切换到 Collector 负责回滚的 Remote 模式。</summary>
    public bool IsRemote => remoteStore?.IsEnabled == true;
}
