using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeAgent.Models.Anthropic;

// ============ 请求侧（网关 -> Anthropic Messages API） ============

public sealed class AnthropicMessageRequest
{
    [JsonPropertyName("model")] public string Model { get; set; } = "";

    /// <summary>Anthropic API 必填：单次响应的最大输出 token 数。</summary>
    [JsonPropertyName("max_tokens")] public int MaxTokens { get; set; } = 4096;

    [JsonPropertyName("messages")] public List<AnthropicMessage> Messages { get; set; } = new();

    /// <summary>顶层系统提示：string 或 content block 数组（网关输出 block 数组，首块为计费头）。</summary>
    [JsonPropertyName("system")] public object? System { get; set; }

    /// <summary>CLI 同款配额归属元数据：user_id 为含 device_id/account_uuid/session_id 的 JSON 字符串。</summary>
    [JsonPropertyName("metadata")] public object? Metadata { get; set; }

    [JsonPropertyName("tools")] public List<AnthropicTool>? Tools { get; set; }

    [JsonPropertyName("tool_choice")] public AnthropicToolChoice? ToolChoice { get; set; }

    [JsonPropertyName("temperature")] public double? Temperature { get; set; }

    [JsonPropertyName("top_p")] public double? TopP { get; set; }

    [JsonPropertyName("stop_sequences")] public List<string>? StopSequences { get; set; }

    [JsonPropertyName("stream")] public bool? Stream { get; set; }

    /// <summary>Anthropic 思考配置（adaptive / enabled / disabled）。</summary>
    [JsonPropertyName("thinking")] public AnthropicThinkingConfig? Thinking { get; set; }

    /// <summary>Anthropic 输出配置（含 effort: low / medium / high / max 等）。</summary>
    [JsonPropertyName("output_config")] public AnthropicOutputConfig? OutputConfig { get; set; }
}

public sealed class AnthropicThinkingConfig
{
    [JsonPropertyName("type")] public string Type { get; set; } = "adaptive";

    [JsonPropertyName("budget_tokens")] public int? BudgetTokens { get; set; }

    [JsonPropertyName("display")] public string? Display { get; set; }
}

public sealed class AnthropicOutputConfig
{
    [JsonPropertyName("effort")] public string? Effort { get; set; }
}

public sealed class AnthropicMessage
{
    /// <summary>"user" 或 "assistant"（tool_result 归并为 user 消息）。</summary>
    [JsonPropertyName("role")] public string Role { get; set; } = "user";

    [JsonPropertyName("content")] public List<AnthropicContentBlock> Content { get; set; } = new();
}

/// <summary>多态内容块（text / image / tool_use / tool_result），按 type 序列化。</summary>
public sealed class AnthropicContentBlock
{
    [JsonPropertyName("type")] public string Type { get; set; } = "text";

    // ---- text ----
    [JsonPropertyName("text")] public string? Text { get; set; }

    // ---- image ----
    [JsonPropertyName("source")] public AnthropicImageSource? Source { get; set; }

    // ---- tool_use ----
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }

    /// <summary>tool_use 的调用参数（JSON 对象）。</summary>
    [JsonPropertyName("input")] public JsonElement? Input { get; set; }

    // ---- tool_result ----
    [JsonPropertyName("tool_use_id")] public string? ToolUseId { get; set; }

    /// <summary>tool_result 的内容（string 或 block 数组；网关输出 string）。</summary>
    [JsonPropertyName("content")] public string? ResultContent { get; set; }

    [JsonPropertyName("is_error")] public bool? IsError { get; set; }
}

public sealed class AnthropicImageSource
{
    [JsonPropertyName("type")] public string Type { get; set; } = "base64";
    [JsonPropertyName("media_type")] public string MediaType { get; set; } = "image/png";
    [JsonPropertyName("data")] public string Data { get; set; } = "";
}

public sealed class AnthropicTool
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string? Description { get; set; }

    /// <summary>JSON Schema（OpenAI function.parameters 原样透传）。</summary>
    [JsonPropertyName("input_schema")] public JsonElement? InputSchema { get; set; }
}

public sealed class AnthropicToolChoice
{
    /// <summary>"auto" | "any" | "none" | "tool"</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = "auto";
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("disable_parallel_tool_use")] public bool? DisableParallelToolUse { get; set; }
}

// ============ 响应侧（非流式 JSON，宽松解析） ============

public sealed class AnthropicResponse
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("role")] public string? Role { get; set; }
    [JsonPropertyName("stop_reason")] public string? StopReason { get; set; }
    [JsonPropertyName("stop_sequence")] public string? StopSequence { get; set; }

    /// <summary>content block 数组（text / tool_use / thinking 等），由 Mapper 解析。</summary>
    [JsonPropertyName("content")] public JsonElement? Content { get; set; }

    [JsonPropertyName("usage")] public AnthropicUsage? Usage { get; set; }

    [JsonPropertyName("error")] public AnthropicApiError? Error { get; set; }
}

public sealed class AnthropicUsage
{
    [JsonPropertyName("input_tokens")] public int InputTokens { get; set; }
    [JsonPropertyName("output_tokens")] public int OutputTokens { get; set; }
    [JsonPropertyName("cache_creation_input_tokens")] public int? CacheCreationInputTokens { get; set; }
    [JsonPropertyName("cache_read_input_tokens")] public int? CacheReadInputTokens { get; set; }
}

public sealed class AnthropicApiError
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
}

// ============ 流式事件（SSE data 载荷，宽松解析） ============

/// <summary>
/// Anthropic SSE 事件：message_start / content_block_start / content_block_delta /
/// content_block_stop / message_delta / message_stop / ping / error。
/// 各事件的专有字段统一以 JsonElement 形式保留，由 Mapper 动态读取。
/// </summary>
public sealed class AnthropicStreamEvent
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("index")] public int? Index { get; set; }

    [JsonPropertyName("content_block")] public JsonElement? ContentBlock { get; set; }
    [JsonPropertyName("delta")] public JsonElement? Delta { get; set; }
    [JsonPropertyName("message")] public JsonElement? Message { get; set; }
    [JsonPropertyName("usage")] public JsonElement? Usage { get; set; }
    [JsonPropertyName("error")] public AnthropicApiError? Error { get; set; }
}
