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
    public const int MaxToolRounds = 4;
    private readonly ILocalLlamaHost _host;
    private readonly ILocalLlamaChatClient _chat;
    private readonly AssistantContextStore _context;
    private readonly IAssistantQuestionTally? _questions;
    private readonly IAssistantToolBroker? _tools;
    private CancellationTokenSource? _sendCancellation;

    public AssistantViewModel(
        ILocalLlamaHost host,
        ILocalLlamaChatClient chat,
        AssistantContextStore context,
        IAssistantQuestionTally? questions = null,
        IAssistantToolBroker? tools = null)
    {
        _host = host;
        _chat = chat;
        _context = context;
        _questions = questions;
        _tools = tools;
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
        var earlier = Messages.Select(turn => (turn.IsUser, turn.Text)).ToList();
        Messages.Add(new AssistantTurn(true, question));
        var context = _context.Capture();
        ContextLine = Describe(context);
        IsBusy = true;
        Status = Strings.Assistant_Preparing;
        AssistantTurn? reply = null;
        var cancellation = new CancellationTokenSource();
        _sendCancellation = cancellation;
        try
        {
            var endpoint = await _host.EnsureStartedAsync(cancellation.Token).ConfigureAwait(true);
            Status = Strings.Assistant_Answering;
            var messages = new List<AssistantChatMessage> { new("system", AssistantPrompt.System) };
            messages.AddRange(AssistantPrompt.History(earlier, question, ContextLine));
            var specs = _tools?.Tools ?? [];
            var rounds = 0;
            while (true)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var allowTools = _tools != null && rounds < MaxToolRounds;
                if (!allowTools && rounds > 0)
                    messages.Add(new AssistantChatMessage("user", "请根据上面的工具结果直接回答，不要再调用函数。"));
                IReadOnlyList<AssistantToolCall>? calls = null;
                var appended = 0;
                await foreach (var delta in _chat.StreamRoundAsync(
                        endpoint,
                        messages,
                        specs,
                        allowTools,
                        cancellation.Token)
                    .ConfigureAwait(true))
                {
                    if (delta.ToolCalls is { Count: > 0 })
                    {
                        calls = delta.ToolCalls;
                        if (reply != null && appended > 0 && reply.Text.Length >= appended)
                        {
                            reply.Text = reply.Text[..^appended];
                            appended = 0;
                            if (reply.Text.Length == 0)
                            {
                                Messages.Remove(reply);
                                reply = null;
                            }
                        }
                    }
                    else if (!string.IsNullOrEmpty(delta.Text))
                    {
                        reply ??= new AssistantTurn(false, "");
                        if (!Messages.Contains(reply))
                            Messages.Add(reply);
                        reply.Text += delta.Text;
                        appended += delta.Text.Length;
                    }
                }

                if (calls is not { Count: > 0 } || _tools == null || !allowTools)
                    break;
                rounds++;
                Status = Strings.Assistant_Querying;
                messages.Add(new AssistantChatMessage("assistant", "", calls));
                foreach (var call in calls)
                {
                    var result = await Task.Run(
                        () => _tools.Execute(call.Name, call.ArgumentsJson, DateTime.Now, context.DeviceName, question, cancellation.Token),
                        cancellation.Token).ConfigureAwait(true);
                    messages.Add(new AssistantChatMessage("tool", result, ToolCallId: call.Id, Name: call.Name));
                }

                Status = Strings.Assistant_Answering;
            }

            if (reply == null || reply.Text.Length == 0)
                throw new InvalidOperationException("模型没有返回文字");
            Status = "";
            Audit(context, succeeded: true);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (reply == null || reply.Text.Length == 0)
                DropEmptyReply(reply);
            Status = Strings.Assistant_Stopped;
            Audit(context, succeeded: false);
        }
        catch (FileNotFoundException)
        {
            DropEmptyReply(reply);
            Status = Strings.Assistant_NoServer;
            Audit(context, succeeded: false);
        }
        catch (Exception ex)
        {
            DropEmptyReply(reply);
            Status = string.Format(Strings.Assistant_Failed, ex.Message);
            Audit(context, succeeded: false);
        }
        finally
        {
            if (ReferenceEquals(_sendCancellation, cancellation))
                _sendCancellation = null;
            cancellation.Dispose();
            IsBusy = false;
        }
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

    private static void Audit(AssistantPromptContext context, bool succeeded)
    {
        var detail = $"device={context.DeviceName ?? ""}; from={context.From:yyyy-MM-dd HH:mm}; to={context.To:yyyy-MM-dd HH:mm}";
        AuditLog.Record("Assistant.Ask", "Page", context.PageKey, succeeded, detail);
    }
}
