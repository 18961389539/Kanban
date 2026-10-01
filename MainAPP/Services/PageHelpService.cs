using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanban.Collector.Core.Services;
using MainAPP.Resources;

namespace MainAPP.Services;

/// <summary>侧栏里的一节。点标题展开这一节，并收起其他节。</summary>
public sealed partial class PageHelpSectionItem : ObservableObject
{
    private readonly Action<PageHelpSectionItem> _collapseOthers;

    public PageHelpSectionItem(PageHelpSection section, Action<PageHelpSectionItem> collapseOthers, bool expanded)
    {
        _collapseOthers = collapseOthers;
        Title = section.Title;
        Blocks = section.Blocks;
        _isExpanded = expanded;
        ToggleCommand = new RelayCommand(Toggle);
    }

    public string Title { get; }

    public IReadOnlyList<PageHelpBlock> Blocks { get; }

    public IRelayCommand ToggleCommand { get; }

    [ObservableProperty]
    private bool _isExpanded;

    private void Toggle()
    {
        if (IsExpanded)
        {
            IsExpanded = false;
            return;
        }

        _collapseOthers(this);
        IsExpanded = true;
    }
}

/// <summary>把手册里的一页说明填进侧栏集合。</summary>
public static class PageHelpLoader
{
    public static string? Apply(
        string markdown,
        string key,
        ObservableCollection<PageHelpBlock> intro,
        ObservableCollection<PageHelpSectionItem> sections)
    {
        intro.Clear();
        sections.Clear();
        var document = PageHelpContent.ExtractDocument(markdown, key);
        if (document.Intro.Count == 0 && document.Sections.Count == 0)
            return Strings.Ux_PageHelpMissing;

        foreach (var block in document.Intro)
            intro.Add(block);

        var items = new List<PageHelpSectionItem>();
        void CollapseOthers(PageHelpSectionItem current)
        {
            foreach (var item in items)
            {
                if (!ReferenceEquals(item, current))
                    item.IsExpanded = false;
            }
        }

        for (var i = 0; i < document.Sections.Count; i++)
        {
            var item = new PageHelpSectionItem(document.Sections[i], CollapseOthers, expanded: i == 0);
            items.Add(item);
            sections.Add(item);
        }

        return null;
    }
}

/// <summary>
/// 页标题旁的问号：在页面右侧展开当前页说明，不盖住页面。
/// 正文从使用手册的 page-help 标记读取。
/// </summary>
public partial class PageHelpService : ObservableObject
{
    private readonly AppSettings _appSettings;

    public ObservableCollection<PageHelpBlock> Intro { get; } = new();

    public ObservableCollection<PageHelpSectionItem> Sections { get; } = new();

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
        Intro.Clear();
        Sections.Clear();
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

        Error = PageHelpLoader.Apply(markdown, key, Intro, Sections);
        IsOpen = true;
    }

    public string? ChapterAnchorFor(string? helpKey)
    {
        if (string.IsNullOrEmpty(helpKey))
            return null;

        var path = UserManualLocator.Resolve(_appSettings);
        if (path is null)
            return null;

        try
        {
            return PageHelpContent.ChapterAnchor(File.ReadAllText(path), helpKey);
        }
        catch (IOException)
        {
            return null;
        }
    }

    [RelayCommand]
    public void Close() => IsOpen = false;
}
