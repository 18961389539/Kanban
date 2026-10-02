namespace MainAPP.Services;

/// <summary>送给本地模型的事实。连接口令、连接串和许可证不进这里。</summary>
public sealed record AssistantPromptContext(
    string PageKey,
    string PageName,
    string? DeviceName,
    DateTime? From,
    DateTime? To,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string>? DeviceNames = null,
    string? ManualExcerpt = null);

public sealed record AssistantHistoryFacts(DateTime From, DateTime To, IReadOnlyList<string> Notes);

/// <summary>把已经显示的对话交给模型。超长时从最早的一轮开始丢，当前问题留下。</summary>
public static class AssistantPrompt
{
    /// <summary>用户消息字符上限。上下文是 16384，还要留给系统提示和最多 2048 个生成词。中文按接近一字一词来留。</summary>
    public const int MaxUserChars = 12000;

    public const string System = "你是这块生产看板的问答助手。需要数据时自己调用工具。";

        public readonly record struct AssistantPromptPiece(
        string Text,
        bool DroppedNames,
        bool DroppedManual,
        bool DroppedNotes,
        bool DroppedDetails,
        IReadOnlyList<string> KeptFacts,
        IReadOnlyList<string> KeptNotes);

    public static string User(
        AssistantPromptContext context,
        string question,
        string? previousQuestion = null,
        string? previousAnswer = null)
        => Write(context, question, previousQuestion, previousAnswer).Text;

    public static AssistantPromptPiece Write(
        AssistantPromptContext context,
        string question,
        string? previousQuestion = null,
        string? previousAnswer = null)
    {
        _ = context;
        return new AssistantPromptPiece(
            Assemble(question, FormatPrevious(previousQuestion, previousAnswer)),
            false,
            false,
            false,
            false,
            [],
            []);
    }

    public static IReadOnlyList<AssistantChatMessage> History(
        IEnumerable<(bool IsUser, string Text)> earlier,
        string question,
        string? contextLine = null)
    {
        var messages = new List<AssistantChatMessage>();
        foreach (var turn in earlier)
        {
            if (string.IsNullOrWhiteSpace(turn.Text))
                continue;
            messages.Add(new AssistantChatMessage(
                turn.IsUser ? "user" : "assistant",
                turn.IsUser ? "问题：" + turn.Text.Trim() : turn.Text.Trim()));
        }

        var current = "问题：" + question.Trim();
        if (!string.IsNullOrWhiteSpace(contextLine))
            current = contextLine.Trim() + "\n" + current;
        messages.Add(new AssistantChatMessage("user", current));
        while (messages.Count > 1 && Length(messages) > MaxUserChars)
            messages.RemoveAt(0);
        if (messages.Count > 1 && messages[0].Role != "user")
            messages.RemoveAt(0);
        return messages;
    }

    private static int Length(IReadOnlyList<AssistantChatMessage> messages)
        => messages.Sum(message => message.Content?.Length ?? 0);

    private static string? FormatPrevious(string? question, string? answer)
    {
        if (string.IsNullOrWhiteSpace(question) || string.IsNullOrWhiteSpace(answer))
            return null;
        return "上一问：" + question.Trim() + "\n上一答：" + answer.Trim();
    }

    private static string Assemble(string question, string? previous)
        => previous == null
            ? "问题：" + question.Trim()
            : previous + "\n问题：" + question.Trim();

    internal static string? CleanFact(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        return text.Trim();
    }
}
