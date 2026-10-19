namespace ClaudeAgent.Services;

using ClaudeAgent.Models.Anthropic;

/// <summary>Anthropic 上游 Provider 接口。</summary>
public interface IAnthropicProvider
{
    string Name { get; }
    IReadOnlyList<string> Models { get; }

    Task<AnthropicResponse> GenerateAsync(string model, AnthropicMessageRequest request, CancellationToken ct);

    IAsyncEnumerable<AnthropicStreamEvent> GenerateStreamAsync(string model, AnthropicMessageRequest request, CancellationToken ct);

    Task<List<string>> RefreshAvailableModelsAsync(CancellationToken ct = default);
}
