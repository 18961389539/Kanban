using System.IO;
using System.Text.RegularExpressions;
using Kanban.Collector.Core.Services;
using Kanban.Collector.Core.Data;
using MainAPP.Models;

namespace MainAPP.Services;

/// <summary>
/// 对话页打开时记住上一页。设备和历史查询时间在发送时再读，避免把用户还没打开的页面提前建出来。
/// </summary>
public sealed class AssistantContextStore
{
    public const string PageKey = "Assistant";

    private readonly Func<string?> _deviceName;
    private readonly object _gate = new();
    private string _pageKey = NavigationPageCatalog.Home.Key;
    private Func<AssistantHistoryFacts?>? _history;

    public AssistantContextStore(IDeviceSelectionService selection, DeviceRepository devices)
        : this(() => NameOf(devices, selection.SelectedDeviceId))
    {
    }

    internal AssistantContextStore(Func<string?> deviceName) => _deviceName = deviceName;

    public void NotePage(string? pageKey)
    {
        if (string.IsNullOrEmpty(pageKey) || pageKey == PageKey)
            return;
        lock (_gate)
            _pageKey = pageKey;
    }

    public void BindHistory(Func<AssistantHistoryFacts?> reader) => _history = reader;

    public AssistantPromptContext Capture()
    {
        string pageKey;
        lock (_gate)
            pageKey = _pageKey;
        var page = NavigationPageCatalog.All.FirstOrDefault(item => item.Key == pageKey);
        var history = _history?.Invoke();
        return new AssistantPromptContext(
            pageKey,
            page?.NavItem.AccessibleName ?? pageKey,
            _deviceName(),
            history?.From,
            history?.To);
    }

    private static string? NameOf(DeviceRepository devices, string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
            return null;
        return devices.Devices.FirstOrDefault(device => device.Id == deviceId)?.Name;
    }
}
