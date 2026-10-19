using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeAgent;

public static class JsonOptions
{
    /// <summary>OpenAI 规范响应序列化选项（camelCase + 忽略 null）。</summary>
    public static readonly JsonSerializerOptions OpenAi = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Anthropic 上游请求/事件序列化选项（原始字段名 + 忽略 null）。</summary>
    public static readonly JsonSerializerOptions Anthropic = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true
    };
}
