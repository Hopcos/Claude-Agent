using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeAgent.Models.OpenAi;

// ============ 请求侧（OpenAI Chat Completions -> 网关） ============

public sealed class ChatCompletionRequest
{
    [JsonPropertyName("model")] public string? Model { get; set; }

    [JsonPropertyName("messages")] public List<ChatMessage> Messages { get; set; } = new();

    [JsonPropertyName("temperature")] public double? Temperature { get; set; }

    [JsonPropertyName("top_p")] public double? TopP { get; set; }

    [JsonPropertyName("max_tokens")] public int? MaxTokens { get; set; }

    [JsonPropertyName("max_completion_tokens")] public int? MaxCompletionTokens { get; set; }

    [JsonPropertyName("stop")] public object? Stop { get; set; }

    [JsonPropertyName("stream")] public bool? Stream { get; set; }

    [JsonPropertyName("tools")] public List<OpenAiTool>? Tools { get; set; }

    [JsonPropertyName("tool_choice")] public object? ToolChoice { get; set; }
}

public sealed class ChatMessage
{
    [JsonPropertyName("role")] public string Role { get; set; } = "user";

    [JsonPropertyName("content")] public object? Content { get; set; }

    /// <summary>仅 "tool" 角色消息使用：对应的工具调用 id。</summary>
    [JsonPropertyName("tool_call_id")] public string? ToolCallId { get; set; }

    /// <summary>仅 assistant 消息：模型发起的工具调用。</summary>
    [JsonPropertyName("tool_calls")] public List<OpenAiToolCall>? ToolCalls { get; set; }

    /// <summary>便捷读取文本内容（string 或多部件数组中的 text 部件）。</summary>
    [JsonIgnore]
    public string? ContentString
    {
        get
        {
            if (Content is null) return null;
            if (Content is string s) return s;
            if (Content is JsonElement el)
            {
                if (el.ValueKind == JsonValueKind.String) return el.GetString();
                if (el.ValueKind == JsonValueKind.Array)
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (var item in el.EnumerateArray())
                    {
                        if (item.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                            sb.Append(t.GetString());
                    }
                    return sb.ToString();
                }
                return el.ToString();
            }
            return Content.ToString();
        }
    }
}

public sealed class OpenAiTool
{
    [JsonPropertyName("type")] public string Type { get; set; } = "function";

    [JsonPropertyName("function")] public OpenAiToolFunction Function { get; set; } = new();
}

public sealed class OpenAiToolFunction
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    [JsonPropertyName("description")] public string? Description { get; set; }

    [JsonPropertyName("parameters")] public JsonElement? Parameters { get; set; }
}

public sealed class OpenAiToolCall
{
    [JsonPropertyName("id")] public string? Id { get; set; }

    [JsonPropertyName("type")] public string Type { get; set; } = "function";

    /// <summary>流式增量块使用：本工具调用在 delta 中的序号。</summary>
    [JsonIgnore]
    public int StreamIndex { get; set; }

    [JsonPropertyName("function")] public OpenAiToolCallFunction Function { get; set; } = new();
}

public sealed class OpenAiToolCallFunction
{
    [JsonPropertyName("name")] public string? Name { get; set; }

    [JsonPropertyName("arguments")] public string? Arguments { get; set; }
}

// ============ 响应侧（网关 -> OpenAI 格式） ============

public sealed class ChatCompletionResponse
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("object")] public string Object { get; set; } = "chat.completion";
    [JsonPropertyName("created")] public long Created { get; set; }
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("choices")] public List<ChatChoice> Choices { get; set; } = new();
    [JsonPropertyName("usage")] public ChatUsage? Usage { get; set; }
}

public sealed class ChatChoice
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("message")] public ChatResponseMessage Message { get; set; } = new();
    [JsonPropertyName("finish_reason")] public string? FinishReason { get; set; }
}

public sealed class ChatResponseMessage
{
    [JsonPropertyName("role")] public string Role { get; set; } = "assistant";
    [JsonPropertyName("content")] public string? Content { get; set; }
    [JsonPropertyName("tool_calls")] public List<OpenAiToolCall>? ToolCalls { get; set; }
}

public sealed class ChatUsage
{
    [JsonPropertyName("prompt_tokens")] public int PromptTokens { get; set; }
    [JsonPropertyName("completion_tokens")] public int CompletionTokens { get; set; }
    [JsonPropertyName("total_tokens")] public int TotalTokens { get; set; }
    [JsonPropertyName("prompt_tokens_details")] public ChatUsagePromptDetails? PromptTokensDetails { get; set; }
    [JsonPropertyName("completion_tokens_details")] public ChatUsageCompletionDetails? CompletionTokensDetails { get; set; }
}

public sealed class ChatUsagePromptDetails
{
    [JsonPropertyName("cached_tokens")] public int? CachedTokens { get; set; }
}

public sealed class ChatUsageCompletionDetails
{
    [JsonPropertyName("reasoning_tokens")] public int? ReasoningTokens { get; set; }
}

// ============ 流式（SSE） ============

public sealed class ChatCompletionChunk
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("object")] public string Object { get; set; } = "chat.completion.chunk";
    [JsonPropertyName("created")] public long Created { get; set; }
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("choices")] public List<ChatChunkChoice> Choices { get; set; } = new();
    [JsonPropertyName("usage")] public ChatUsage? Usage { get; set; }
}

public sealed class ChatChunkChoice
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("delta")] public ChatChunkDelta Delta { get; set; } = new();
    [JsonPropertyName("finish_reason")] public string? FinishReason { get; set; }
}

public sealed class ChatChunkDelta
{
    [JsonPropertyName("role")] public string? Role { get; set; }
    [JsonPropertyName("content")] public string? Content { get; set; }
    [JsonPropertyName("reasoning_content")] public string? ReasoningContent { get; set; }
    [JsonPropertyName("tool_calls")] public List<OpenAiToolCall>? ToolCalls { get; set; }
}

// ============ 错误 / 模型列表 ============

public sealed class OpenAiErrorBody
{
    [JsonPropertyName("error")] public OpenAiError Error { get; set; } = new();
}

public sealed class OpenAiError
{
    [JsonPropertyName("message")] public string Message { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "api_error";
    [JsonPropertyName("code")] public string? Code { get; set; }
}

public sealed class ModelListResponse
{
    [JsonPropertyName("object")] public string Object { get; set; } = "list";
    [JsonPropertyName("data")] public List<ModelInfo> Data { get; set; } = new();
}

public sealed class ModelInfo
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("object")] public string Object { get; set; } = "model";
    [JsonPropertyName("created")] public long Created { get; set; }
    [JsonPropertyName("owned_by")] public string OwnedBy { get; set; } = "anthropic";
}
