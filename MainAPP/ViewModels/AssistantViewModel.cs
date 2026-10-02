using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanban.Collector.Core.Services;
using MainAPP.Resources;
using MainAPP.Services;

namespace MainAPP.ViewModels;

public sealed partial class AssistantTurn : ObservableObject
{
    public AssistantTurn(bool isUser, string text)
    {
        IsUser = isUser;
        Text = text;
    }

    public bool IsUser { get; }

    [ObservableProperty]
    private string _text = "";

    [ObservableProperty]
    private string _notice = "";

    public bool HasNotice => Notice.Length > 0;

    partial void OnNoticeChanged(string value) => OnPropertyChanged(nameof(HasNotice));
}

/// <summary>AI问答页。发送时才准备模型；启动程序本身不下载、不拉起 llama-server。</summary>
public partial class AssistantViewModel : ObservableObject
{
    private readonly ILocalLlamaHost _host;
    private readonly AssistantContextStore _context;
    private readonly AssistantConversation _conversation;
    private readonly IAssistantQuestionTally? _questions;
    private CancellationTokenSource? _sendCancellation;

    public AssistantViewModel(
        ILocalLlamaHost host,
        ILocalLlamaChatClient chat,
        AssistantContextStore context,
        IAssistantQuestionTally? questions = null,
        IAssistantToolBroker? tools = null)
    {
        _host = host;
        _context = context;
        _conversation = new AssistantConversation(chat, tools);
        _questions = questions;
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

    [RelayCommand(CanExecute = nameof(CanAskPreset))]
    private Task LagAsync() => SendPresetAsync(Strings.Assistant_LagQuestion);

    [RelayCommand(CanExecute = nameof(CanAskPreset))]
    private Task YesterdayAsync() => SendPresetAsync(Strings.Assistant_YesterdayQuestion);

    [RelayCommand(CanExecute = nameof(CanAskPreset))]
    private Task OrderBehindAsync() => SendPresetAsync(Strings.Assistant_OrderBehindQuestion);

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _sendCancellation?.Cancel();

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var question = Draft.Trim();
        Draft = "";
        _questions?.Record(question);
        Messages.Add(new AssistantTurn(true, question));
        var context = _context.Capture();
        ContextLine = Describe(context);
        IsBusy = true;
        Status = Strings.Assistant_Preparing;
        AssistantTurn? reply = null;
        var toolsUsed = new List<string>();
        var committed = false;
        var cancellation = new CancellationTokenSource();
        _sendCancellation = cancellation;
        try
        {
            var endpoint = await _host.EnsureStartedAsync(cancellation.Token).ConfigureAwait(true);
            await _conversation.AskAsync(
                endpoint,
                question,
                ContextLine,
                context.DeviceName,
                step => reply = Apply(reply, step),
                toolsUsed,
                cancellation.Token).ConfigureAwait(true);
            Status = "";
            committed = true;
            Audit(context, toolsUsed, succeeded: true);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (reply == null || reply.Text.Length == 0)
                DropEmptyReply(reply);
            Status = Strings.Assistant_Stopped;
            Audit(context, toolsUsed, succeeded: false);
        }
        catch (FileNotFoundException)
        {
            DropEmptyReply(reply);
            Status = Strings.Assistant_NoServer;
            Audit(context, toolsUsed, succeeded: false);
        }
        catch (Exception ex)
        {
            DropEmptyReply(reply);
            Status = string.Format(Strings.Assistant_Failed, ex.Message);
            Audit(context, toolsUsed, succeeded: false);
        }
        finally
        {
            if (!committed)
                _conversation.Remember(question, ContextLine, reply?.Text);
            if (ReferenceEquals(_sendCancellation, cancellation))
                _sendCancellation = null;
            cancellation.Dispose();
            IsBusy = false;
        }
    }

    private AssistantTurn? Apply(AssistantTurn? reply, AssistantStreamEvent step)
    {
        switch (step.Kind)
        {
            case AssistantStreamKind.Answering:
                Status = Strings.Assistant_Answering;
                break;
            case AssistantStreamKind.Querying:
                Status = Strings.Assistant_Querying;
                break;
            case AssistantStreamKind.Text:
                reply ??= new AssistantTurn(false, "");
                if (!Messages.Contains(reply))
                    Messages.Add(reply);
                reply.Text += step.Text;
                break;
            case AssistantStreamKind.Rewind:
                if (reply != null && step.Chars > 0 && reply.Text.Length >= step.Chars)
                {
                    reply.Text = reply.Text[..^step.Chars];
                    if (reply.Text.Length == 0)
                    {
                        Messages.Remove(reply);
                        reply = null;
                    }
                }
                break;
            case AssistantStreamKind.Notice:
                if (reply != null)
                    reply.Notice = step.Text ?? "";
                break;
        }

        return reply;
    }

    private async Task SendPresetAsync(string question)
    {
        Draft = question;
        await SendAsync();
    }

    private bool CanSend() => !IsBusy && !string.IsNullOrWhiteSpace(Draft);

    private bool CanStop() => IsBusy;

    private bool CanAskPreset() => !IsBusy;

    partial void OnDraftChanged(string value) => SendCommand.NotifyCanExecuteChanged();

    public bool CanType => !IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanType));
        SendCommand.NotifyCanExecuteChanged();
        CompareCommand.NotifyCanExecuteChanged();
        HandoverCommand.NotifyCanExecuteChanged();
        LagCommand.NotifyCanExecuteChanged();
        YesterdayCommand.NotifyCanExecuteChanged();
        OrderBehindCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    private void DropEmptyReply(AssistantTurn? reply)
    {
        if (reply != null && reply.Text.Length == 0)
            Messages.Remove(reply);
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

    private static void Audit(AssistantPromptContext context, IReadOnlyList<string> tools, bool succeeded)
    {
        var detail = $"device={context.DeviceName ?? ""}; from={context.From:yyyy-MM-dd HH:mm}; to={context.To:yyyy-MM-dd HH:mm}; tools={string.Join(",", tools)}";
        AuditLog.Record("Assistant.Ask", "Page", context.PageKey, succeeded, detail);
    }
}
