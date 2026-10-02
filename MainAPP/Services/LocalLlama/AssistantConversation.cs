using System.Text;
using MainAPP.Resources;

namespace MainAPP.Services;

public enum AssistantStreamKind
{
    Answering,
    Querying,
    Text,
    Rewind,
    Notice,
}

public readonly record struct AssistantStreamEvent(AssistantStreamKind Kind, string? Text = null, int Chars = 0);

/// <summary>
/// 一次问答里的多轮工具调用，以及跨问题保留的对话。
/// 历史里留下工具结果和当时的页面上下文，下一问还能接着用。
/// </summary>
public sealed class AssistantConversation
{
    public const int MaxToolRounds = 4;

    private readonly ILocalLlamaChatClient _chat;
    private readonly IAssistantToolBroker? _tools;
    private readonly List<AssistantChatMessage> _transcript = [];

    public AssistantConversation(ILocalLlamaChatClient chat, IAssistantToolBroker? tools)
    {
        _chat = chat;
        _tools = tools;
    }

    public async Task AskAsync(
        Uri endpoint,
        string question,
        string? contextLine,
        string? selectedDeviceName,
        Action<AssistantStreamEvent> emit,
        ICollection<string> toolNames,
        CancellationToken cancellationToken)
    {
        var current = new List<AssistantChatMessage> { AssistantPrompt.UserMessage(question, contextLine) };
        var specs = _tools?.Tools ?? [];
        var overhead = AssistantPrompt.EstimateTokens(AssistantPrompt.System) + AssistantPrompt.EstimateTools(specs);
        var rounds = 0;
        var reply = new StringBuilder();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var allowTools = _tools != null && rounds < MaxToolRounds;
            AssistantPrompt.ShrinkTools(current, overhead);
            var prompt = WithSystem(AssistantPrompt.Fit([.._transcript, ..current], overhead));
            emit(new AssistantStreamEvent(AssistantStreamKind.Answering));
            IReadOnlyList<AssistantToolCall>? calls = null;
            var appended = 0;
            await foreach (var delta in _chat.StreamRoundAsync(endpoint, prompt, specs, allowTools, cancellationToken)
                .ConfigureAwait(true))
            {
                if (delta.ToolCalls is { Count: > 0 })
                {
                    calls = delta.ToolCalls;
                    if (appended > 0)
                    {
                        emit(new AssistantStreamEvent(AssistantStreamKind.Rewind, Chars: appended));
                        if (reply.Length >= appended)
                            reply.Length -= appended;
                        appended = 0;
                    }
                }
                else if (!string.IsNullOrEmpty(delta.Text))
                {
                    emit(new AssistantStreamEvent(AssistantStreamKind.Text, delta.Text));
                    reply.Append(delta.Text);
                    appended += delta.Text.Length;
                }
            }

            if (calls is not { Count: > 0 } || _tools == null || !allowTools)
                break;
            rounds++;
            emit(new AssistantStreamEvent(AssistantStreamKind.Querying));
            current.Add(new AssistantChatMessage("assistant", "", calls));
            foreach (var call in calls)
            {
                toolNames.Add(call.Name);
                var result = await Task.Run(
                    () => _tools.Execute(call.Name, call.ArgumentsJson, DateTime.Now, selectedDeviceName, question, cancellationToken),
                    cancellationToken).ConfigureAwait(true);
                current.Add(new AssistantChatMessage("tool", result, ToolCallId: call.Id, Name: call.Name));
            }
        }

        var text = reply.ToString();
        if (text.Length == 0)
            throw new InvalidOperationException("模型没有返回文字");
        if (AssistantAnswerCheck.HasNumberOutside(text, AllowedFacts(question, contextLine, current)))
            emit(new AssistantStreamEvent(AssistantStreamKind.Notice, Strings.Assistant_Ungrounded));
        current.Add(new AssistantChatMessage("assistant", text));
        _transcript.AddRange(current);
        Replace(AssistantPrompt.Fit(_transcript, overhead));
    }

    /// <summary>这一问没有正常结束时，把已经显示出来的问题和回答记进下一问的历史。</summary>
    public void Remember(string question, string? contextLine, string? answer)
    {
        _transcript.Add(AssistantPrompt.UserMessage(question, contextLine));
        if (!string.IsNullOrWhiteSpace(answer))
            _transcript.Add(new AssistantChatMessage("assistant", answer.Trim()));
        Replace(AssistantPrompt.Fit(_transcript, 0));
    }

    private void Replace(List<AssistantChatMessage> messages)
    {
        _transcript.Clear();
        _transcript.AddRange(messages);
    }

    private static List<AssistantChatMessage> WithSystem(IReadOnlyList<AssistantChatMessage> messages)
    {
        var prompt = new List<AssistantChatMessage> { new("system", AssistantPrompt.System) };
        prompt.AddRange(messages);
        return prompt;
    }

    private static IEnumerable<string> AllowedFacts(
        string question,
        string? contextLine,
        IReadOnlyList<AssistantChatMessage> current)
    {
        yield return question;
        if (!string.IsNullOrWhiteSpace(contextLine))
            yield return contextLine;
        foreach (var message in current)
        {
            if (message.Role == "tool" && !string.IsNullOrEmpty(message.Content))
                yield return message.Content;
        }
    }
}
