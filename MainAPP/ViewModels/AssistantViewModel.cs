using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanban.Collector.Core.Services;
using MainAPP.Resources;
using MainAPP.Services;

namespace MainAPP.ViewModels;

public sealed class AssistantTurn
{
    public AssistantTurn(bool isUser, string text)
    {
        IsUser = isUser;
        Text = text;
    }

    public bool IsUser { get; }
    public string Text { get; }
}

/// <summary>AI问答页。发送时才准备模型；启动程序本身不下载、不拉起 llama-server。</summary>
public partial class AssistantViewModel : ObservableObject
{
    public const int MaxQuestionLength = 800;

    private readonly ILocalLlamaHost _host;
    private readonly ILocalLlamaChatClient _chat;
    private readonly AssistantContextStore _context;

    public AssistantViewModel(ILocalLlamaHost host, ILocalLlamaChatClient chat, AssistantContextStore context)
    {
        _host = host;
        _chat = chat;
        _context = context;
        RefreshContext();
    }

    public ObservableCollection<AssistantTurn> Messages { get; } = new();

    [ObservableProperty]
    private string _draft = "";

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private string _contextLine = "";

    [ObservableProperty]
    private bool _isBusy;

    public void RefreshContext() => ContextLine = Describe(_context.Capture());

    [RelayCommand(CanExecute = nameof(CanAskPreset))]
    private Task CompareAsync() => SendPresetAsync(Strings.Assistant_CompareQuestion);

    [RelayCommand(CanExecute = nameof(CanAskPreset))]
    private Task HandoverAsync() => SendPresetAsync(Strings.Assistant_HandoverQuestion);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var question = Draft.Trim();
        if (question.Length > MaxQuestionLength)
        {
            Status = Strings.Assistant_TooLong;
            return;
        }

        Draft = "";
        Messages.Add(new AssistantTurn(true, question));
        var context = _context.Capture();
        ContextLine = Describe(context);
        IsBusy = true;
        Status = Strings.Assistant_Preparing;
        try
        {
            var endpoint = await _host.EnsureStartedAsync().ConfigureAwait(true);
            Status = Strings.Assistant_Answering;
            var reply = await _chat.CompleteAsync(endpoint, AssistantPrompt.System, AssistantPrompt.User(context, question))
                .ConfigureAwait(true);
            Messages.Add(new AssistantTurn(false, reply));
            Status = "";
            Audit(context, succeeded: true);
        }
        catch (FileNotFoundException)
        {
            Status = Strings.Assistant_NoServer;
            Audit(context, succeeded: false);
        }
        catch (Exception ex)
        {
            Status = string.Format(Strings.Assistant_Failed, ex.Message);
            Audit(context, succeeded: false);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SendPresetAsync(string question)
    {
        Draft = question;
        await SendAsync();
    }

    private bool CanSend() => !IsBusy && !string.IsNullOrWhiteSpace(Draft);

    private bool CanAskPreset() => !IsBusy;

    partial void OnDraftChanged(string value) => SendCommand.NotifyCanExecuteChanged();

    public bool CanType => !IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanType));
        SendCommand.NotifyCanExecuteChanged();
        CompareCommand.NotifyCanExecuteChanged();
        HandoverCommand.NotifyCanExecuteChanged();
    }

    private static string Describe(AssistantPromptContext context)
    {
        var page = string.Format(Strings.Assistant_ContextPage, context.PageName);
        var device = string.IsNullOrWhiteSpace(context.DeviceName)
            ? Strings.Assistant_ContextDeviceNone
            : string.Format(Strings.Assistant_ContextDevice, context.DeviceName);
        var range = context.From is DateTime from && context.To is DateTime to
            ? string.Format(Strings.Assistant_ContextRange, from.ToString("yyyy-MM-dd HH:mm"), to.ToString("yyyy-MM-dd HH:mm"))
            : Strings.Assistant_ContextRangeNone;
        return $"{page}    {device}    {range}";
    }

    private static void Audit(AssistantPromptContext context, bool succeeded)
    {
        var detail = $"device={context.DeviceName ?? ""}; from={context.From:yyyy-MM-dd HH:mm}; to={context.To:yyyy-MM-dd HH:mm}";
        AuditLog.Record("Assistant.Ask", "Page", context.PageKey, succeeded, detail);
    }
}
