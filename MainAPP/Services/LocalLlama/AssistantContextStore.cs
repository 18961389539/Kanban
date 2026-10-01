using System.IO;
using System.Text.RegularExpressions;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Services;
using MainAPP.Models;

namespace MainAPP.Services;

/// <summary>
/// 对话页打开时记住上一页。设备和历史查询时间在发送时再读，避免把用户还没打开的页面提前建出来。
/// </summary>
public sealed class AssistantContextStore
{
    public const string PageKey = "Assistant";
    public const int MaxDeviceNames = 40;

    private readonly Func<string?> _deviceName;
    private readonly Func<IReadOnlyList<string>> _deviceNames;
    private readonly Func<string, string?> _manualExcerpt;
    private readonly object _gate = new();
    private string _pageKey = NavigationPageCatalog.Home.Key;
    private Func<AssistantHistoryFacts?>? _history;

    public AssistantContextStore(IDeviceSelectionService selection, DeviceRepository devices, AppSettings settings)
        : this(
            () => NameOf(devices, selection.SelectedDeviceId),
            () => Names(devices),
            pageKey => ManualExcerpt(settings, pageKey))
    {
    }

    internal AssistantContextStore(
        Func<string?> deviceName,
        Func<IReadOnlyList<string>>? deviceNames = null,
        Func<string, string?>? manualExcerpt = null)
    {
        _deviceName = deviceName;
        _deviceNames = deviceNames ?? (() => []);
        _manualExcerpt = manualExcerpt ?? (_ => null);
    }

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
            history?.To,
            history?.Notes ?? [],
            _deviceNames(),
            _manualExcerpt(pageKey));
    }

    private static string? NameOf(DeviceRepository devices, string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
            return null;
        return devices.Devices.FirstOrDefault(device => device.Id == deviceId)?.Name;
    }

    private static IReadOnlyList<string> Names(DeviceRepository devices)
        => devices.Devices
            .Select(device => device.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Where(name => !Regex.IsMatch(name, @"^[A-Za-z]{1,4}\d+(\.\d+)?$"))
            .Distinct(StringComparer.Ordinal)
            .Take(MaxDeviceNames)
            .ToList();

    private static string? ManualExcerpt(AppSettings settings, string pageKey)
    {
        var helpKey = PageHelpContent.HelpKeyForNavigation(pageKey);
        var path = UserManualLocator.Resolve(settings);
        if (helpKey is null || path is null || !File.Exists(path))
            return null;
        return PageHelpContent.IntroExcerpt(File.ReadAllText(path), helpKey);
    }
}
