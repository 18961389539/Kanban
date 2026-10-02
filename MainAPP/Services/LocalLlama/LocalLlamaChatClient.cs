using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MainAPP.Services;

public interface ILocalLlamaChatClient
{
    Task<string> CompleteAsync(Uri endpoint, string system, string user, CancellationToken cancellationToken = default);

    IAsyncEnumerable<string> StreamAsync(Uri endpoint, string system, string user, CancellationToken cancellationToken = default);

    IAsyncEnumerable<AssistantChatDelta> StreamRoundAsync(
        Uri endpoint,
        IReadOnlyList<AssistantChatMessage> messages,
        IReadOnlyList<AssistantToolSpec> tools,
        bool allowTools,
        CancellationToken cancellationToken = default);
}

/// <summary>llama-server 的 OpenAI 兼容接口。这一页由用户等待回答，所以时限按一次生成来定，不套卡片洞察的两三秒。</summary>
public sealed class LocalLlamaChatClient : ILocalLlamaChatClient
{
    private readonly HttpMessageHandler? _handler;

    public LocalLlamaChatClient(HttpMessageHandler? handler = null) => _handler = handler;

    public async Task<string> CompleteAsync(Uri endpoint, string system, string user, CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        var request = CreateRequest(system, user, stream: false);
        using var response = await client.PostAsJsonAsync(new Uri(endpoint, "v1/chat/completions"), request, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}");

        var body = await response.Content.ReadFromJsonAsync<ChatResponse>(cancellationToken).ConfigureAwait(false);
        var text = body?.Choices?.FirstOrDefault()?.Message?.Content?.Trim();
        if (string.IsNullOrEmpty(text))
            throw new InvalidOperationException("模型没有返回文字");
        return text;
    }

    public async IAsyncEnumerable<string> StreamAsync(
        Uri endpoint,
        string system,
        string user,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, "v1/chat/completions"))
        {
            Content = JsonContent.Create(CreateRequest(system, user, stream: true)),
        };
        using var response = await client
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line == null)
                break;
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;
            var payload = line["data:".Length..].Trim();
            if (payload == "[DONE]")
                break;
            var chunk = ReadDelta(payload);
            if (!string.IsNullOrEmpty(chunk))
                yield return chunk;
        }
    }

    public async IAsyncEnumerable<AssistantChatDelta> StreamRoundAsync(
        Uri endpoint,
        IReadOnlyList<AssistantChatMessage> messages,
        IReadOnlyList<AssistantToolSpec> tools,
        bool allowTools,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, "v1/chat/completions"))
        {
            Content = new StringContent(CreateRoundRequest(messages, tools, allowTools), Encoding.UTF8, "application/json"),
        };
        using var response = await client
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}");

        var held = new StringBuilder();
        var raw = new StringBuilder();
        var released = false;
        var calls = new Dictionary<int, MutableToolCall>();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line == null)
                break;
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;
            var payload = line["data:".Length..].Trim();
            if (payload == "[DONE]")
                break;
            if (!TryReadRound(payload, out var content, calls))
                continue;
            if (string.IsNullOrEmpty(content))
                continue;
            raw.Append(content);
            if (released)
            {
                yield return new AssistantChatDelta(content, null);
                continue;
            }

            held.Append(content);
            if (held.ToString().TrimStart().StartsWith('<'))
                continue;
            released = true;
            yield return new AssistantChatDelta(held.ToString(), null);
        }

        IReadOnlyList<AssistantToolCall> chosen = FinishCalls(calls);
        if (chosen.Count == 0)
            chosen = AssistantToolXml.ReadCalls(raw.ToString());
        if (chosen.Count > 0)
            yield return new AssistantChatDelta(null, chosen);
        else if (!released && held.Length > 0)
        {
            var text = held.ToString().Trim();
            if (text.Length > 0)
                yield return new AssistantChatDelta(text, null);
        }
    }

    private HttpClient CreateClient()
    {
        var client = _handler == null
            ? new HttpClient()
            : new HttpClient(_handler, disposeHandler: false);
        client.Timeout = TimeSpan.FromSeconds(180);
        return client;
    }

    private static string CreateRoundRequest(
        IReadOnlyList<AssistantChatMessage> messages,
        IReadOnlyList<AssistantToolSpec> tools,
        bool allowTools)
    {
        var payload = new JsonObject
        {
            ["stream"] = true,
            ["messages"] = new JsonArray(messages.Select(MessageJson).ToArray()),
        };
        if (tools.Count > 0)
        {
            payload["tools"] = ToolJson(tools);
            payload["tool_choice"] = allowTools ? "auto" : "none";
        }

        return payload.ToJsonString();
    }

    private static JsonObject MessageJson(AssistantChatMessage message)
    {
        var node = new JsonObject
        {
            ["role"] = message.Role,
            ["content"] = message.Content ?? "",
        };
        if (message.ToolCalls is { Count: > 0 })
        {
            var calls = new JsonArray();
            foreach (var call in message.ToolCalls)
            {
                calls.Add(new JsonObject
                {
                    ["id"] = call.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = call.Name,
                        ["arguments"] = call.ArgumentsJson,
                    },
                });
            }

            node["tool_calls"] = calls;
        }

        if (!string.IsNullOrEmpty(message.ToolCallId))
            node["tool_call_id"] = message.ToolCallId;
        if (!string.IsNullOrEmpty(message.Name))
            node["name"] = message.Name;
        return node;
    }

    private static JsonArray ToolJson(IReadOnlyList<AssistantToolSpec> tools)
    {
        var array = new JsonArray();
        foreach (var tool in tools)
        {
            var properties = new JsonObject();
            var required = new JsonArray();
            foreach (var param in tool.Parameters)
            {
                properties[param.Name] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = param.Description,
                };
                if (param.Required)
                    required.Add(param.Name);
            }

            array.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = properties,
                        ["required"] = required,
                    },
                },
            });
        }

        return array;
    }

    private static bool TryReadRound(string payload, out string? content, Dictionary<int, MutableToolCall> calls)
    {
        content = null;
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                return false;
            if (!choices[0].TryGetProperty("delta", out var delta))
                return false;
            if (delta.TryGetProperty("content", out var text) && text.ValueKind == JsonValueKind.String)
                content = text.GetString();
            if (!delta.TryGetProperty("tool_calls", out var toolCalls))
                return true;
            foreach (var item in toolCalls.EnumerateArray())
            {
                var index = item.TryGetProperty("index", out var indexValue) && indexValue.TryGetInt32(out var parsed)
                    ? parsed
                    : calls.Count;
                if (!calls.TryGetValue(index, out var call))
                    calls[index] = call = new MutableToolCall();
                if (item.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } found)
                    call.Id = found;
                if (!item.TryGetProperty("function", out var function))
                    continue;
                if (function.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } toolName)
                    call.Name += toolName;
                if (function.TryGetProperty("arguments", out var arguments) && arguments.GetString() is string piece)
                    call.Arguments.Append(piece);
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static List<AssistantToolCall> FinishCalls(Dictionary<int, MutableToolCall> calls)
        => calls
            .OrderBy(pair => pair.Key)
            .Select(pair => pair.Value)
            .Where(call => call.Name.Length > 0)
            .Select((call, index) => new AssistantToolCall(
                call.Id.Length == 0 ? "call_" + index : call.Id,
                call.Name,
                call.Arguments.Length == 0 ? "{}" : call.Arguments.ToString()))
            .ToList();

    private sealed class MutableToolCall
    {
        public string Id { get; set; } = "";

        public string Name { get; set; } = "";

        public StringBuilder Arguments { get; } = new();
    }

    private static ChatRequest CreateRequest(string system, string user, bool stream) => new(
    [
        new ChatMessage("system", system),
        new ChatMessage("user", user),
    ])
    {
        Stream = stream,
    };

    private static string? ReadDelta(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                return null;
            if (!choices[0].TryGetProperty("delta", out var delta))
                return null;
            return delta.TryGetProperty("content", out var content) ? content.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record ChatRequest(
        [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages)
    {
        [JsonPropertyName("stream")]
        public bool Stream { get; init; }
    }

    private sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed class ChatResponse
    {
        [JsonPropertyName("choices")]
        public List<ChatChoice>? Choices { get; set; }
    }

    private sealed class ChatChoice
    {
        [JsonPropertyName("message")]
        public ChatReply? Message { get; set; }
    }

    private sealed class ChatReply
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }
    }
}
