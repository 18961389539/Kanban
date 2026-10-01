using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace MainAPP.Services;

public interface ILocalLlamaChatClient
{
    Task<string> CompleteAsync(Uri endpoint, string system, string user, CancellationToken cancellationToken = default);
}

/// <summary>llama-server 的 OpenAI 兼容接口。这一页由用户等待回答，所以时限按一次生成来定，不套卡片洞察的两三秒。</summary>
public sealed class LocalLlamaChatClient : ILocalLlamaChatClient
{
    private readonly HttpMessageHandler? _handler;

    public LocalLlamaChatClient(HttpMessageHandler? handler = null) => _handler = handler;

    public async Task<string> CompleteAsync(Uri endpoint, string system, string user, CancellationToken cancellationToken = default)
    {
        using var client = _handler == null
            ? new HttpClient()
            : new HttpClient(_handler, disposeHandler: false);
        client.Timeout = TimeSpan.FromSeconds(90);

        var request = new ChatRequest(
        [
            new ChatMessage("system", system),
            new ChatMessage("user", user),
        ]);
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

    private sealed record ChatRequest(
        [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages)
    {
        [JsonPropertyName("temperature")]
        public double Temperature { get; init; } = 0.2;

        [JsonPropertyName("max_tokens")]
        public int MaxTokens { get; init; } = 400;
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
