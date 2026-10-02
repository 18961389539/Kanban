namespace MainAPP.Services;

/// <summary>发送时读到的页面、设备和历史查询时间。名单、手册和历史说明不进模型。</summary>
public sealed record AssistantPromptContext(
    string PageKey,
    string PageName,
    string? DeviceName,
    DateTime? From,
    DateTime? To);

public sealed record AssistantHistoryFacts(DateTime From, DateTime To);

/// <summary>把已经发生的对话交给模型。超长时从最早的一整轮开始丢，当前问题留下。</summary>
public static class AssistantPrompt
{
    /// <summary>留给这次生成的 token。上下文总长见 <see cref="LocalLlamaHost.ContextTokens"/>。</summary>
    public const int ReservedReplyTokens = 2048;

    public const int MaxInputTokens = LocalLlamaHost.ContextTokens - ReservedReplyTokens;

    public const string System = "你是这块生产看板的问答助手。需要数据时自己调用工具。";

    /// <summary>
    /// 按字符计 token。中文、数字和 JSON 标点在这块模型上接近一字一词，英文会估多。
    /// 估多只会更早丢掉旧对话，不会把请求撑过上下文。
    /// </summary>
    public static int EstimateTokens(string? text) => text?.Length ?? 0;

    public static int Estimate(IReadOnlyList<AssistantChatMessage> messages)
        => messages.Sum(Estimate);

    public static int Estimate(AssistantChatMessage message)
    {
        var total = EstimateTokens(message.Content) + EstimateTokens(message.Name) + EstimateTokens(message.ToolCallId);
        if (message.ToolCalls == null)
            return total;
        foreach (var call in message.ToolCalls)
            total += EstimateTokens(call.Id) + EstimateTokens(call.Name) + EstimateTokens(call.ArgumentsJson);
        return total;
    }

    public static int EstimateTools(IReadOnlyList<AssistantToolSpec> tools)
    {
        var total = 0;
        foreach (var tool in tools)
        {
            total += EstimateTokens(tool.Name) + EstimateTokens(tool.Description) + 48;
            foreach (var param in tool.Parameters)
                total += EstimateTokens(param.Name) + EstimateTokens(param.Description) + 24;
        }

        return total;
    }

    public static IReadOnlyList<AssistantChatMessage> History(
        IEnumerable<(bool IsUser, string Text)> earlier,
        string question,
        string? contextLine = null,
        int overheadTokens = 0)
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

        messages.Add(UserMessage(question, contextLine));
        return Fit(messages, overheadTokens);
    }

    public static List<AssistantChatMessage> Fit(IReadOnlyList<AssistantChatMessage> messages, int overheadTokens = 0)
    {
        var kept = new List<AssistantChatMessage>(messages);
        while (kept.Count > 1 && Estimate(kept) + overheadTokens > MaxInputTokens)
        {
            if (!DropOldestTurn(kept))
                break;
        }

        while (kept.Count > 1 && kept[0].Role != "user")
            kept.RemoveAt(0);
        return kept;
    }

    /// <summary>当前这一轮的工具结果太长时，从最后一条开始截短，直到放得进上下文。</summary>
    public static void ShrinkTools(List<AssistantChatMessage> messages, int overheadTokens)
    {
        for (var guard = 0; guard < 12 && Estimate(messages) + overheadTokens > MaxInputTokens; guard++)
        {
            var index = -1;
            for (var i = messages.Count - 1; i >= 0; i--)
            {
                if (messages[i].Role == "tool" && (messages[i].Content?.Length ?? 0) > 40)
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
                return;
            var message = messages[index];
            var content = message.Content ?? "";
            var next = content.Length > 80
                ? content[..(content.Length / 2)] + "\n（已截断）"
                : "这次结果太长，没有全部带上。";
            messages[index] = message with { Content = next };
        }
    }

    public static AssistantChatMessage UserMessage(string question, string? contextLine)
    {
        var current = "问题：" + question.Trim();
        if (!string.IsNullOrWhiteSpace(contextLine))
            current = contextLine.Trim() + "\n" + current;
        return new AssistantChatMessage("user", current);
    }

    internal static string? CleanFact(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        return text.Trim();
    }

    private static bool DropOldestTurn(List<AssistantChatMessage> messages)
    {
        var lastUser = -1;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == "user")
            {
                lastUser = i;
                break;
            }
        }

        if (lastUser <= 0)
            return false;
        var end = 1;
        while (end < lastUser && messages[end].Role != "user")
            end++;
        messages.RemoveRange(0, end);
        return true;
    }
}
