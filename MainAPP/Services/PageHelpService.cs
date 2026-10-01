using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanban.Collector.Core.Services;
using MainAPP.Resources;

namespace MainAPP.Services;

/// <summary>
/// 页标题旁的问号：在页面右侧展开当前页的短说明，不盖住页面。
/// 正文从使用手册的 page-help 标记读取。
/// </summary>
public partial class PageHelpService : ObservableObject
{
    private readonly AppSettings _appSettings;

    public ObservableCollection<PageHelpBlock> Blocks { get; } = new();

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _activeKey = "";

    [ObservableProperty]
    private string? _error;

    public PageHelpService(AppSettings appSettings)
    {
        _appSettings = appSettings;
    }

    public void Toggle(string? key)
    {
        if (string.IsNullOrEmpty(key))
            return;
        if (IsOpen && ActiveKey == key)
            Close();
        else
            Show(key);
    }

    public void Show(string key)
    {
        ActiveKey = key;
        Blocks.Clear();
        Error = null;

        var path = UserManualLocator.Resolve(_appSettings);
        if (path is null)
        {
            Error = string.Format(Strings.Ux_HelpManualMissing, UserManualLocator.ExpectedFileName(_appSettings));
            IsOpen = true;
            return;
        }

        string markdown;
        try
        {
            markdown = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            IsOpen = true;
            return;
        }

        var blocks = PageHelpContent.Extract(markdown, key);
        if (blocks.Count == 0)
        {
            Error = Strings.Ux_PageHelpMissing;
            IsOpen = true;
            return;
        }

        foreach (var block in blocks)
            Blocks.Add(block);
        IsOpen = true;
    }

    [RelayCommand]
    public void Close() => IsOpen = false;
}
