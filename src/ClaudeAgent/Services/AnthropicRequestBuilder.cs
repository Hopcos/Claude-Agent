using System.Text;
using System.Text.Json;
using ClaudeAgent.Configuration;
using ClaudeAgent.Models.Anthropic;
using ClaudeAgent.Models.OpenAi;

namespace ClaudeAgent.Services;

/// <summary>
/// OpenAI Chat Completions 请求 -> Anthropic Messages API 请求构建器：
/// 1. system 消息合并进顶层 system 参数（block 数组，首块为 OAuth 通道必需的计费头）；
/// 2. 多部件文本 / 图片（data URI 或 http(s) URL 下载转 base64）映射为 text / image 块；
/// 3. assistant 的 tool_calls 映射为 tool_use 块，"tool" 角色映射为 tool_result 块；
/// 4. tools / tool_choice / stop / max_tokens 全量对齐 Anthropic 规范；
/// 5. metadata.user_id 携带 device/account/session 标识（CLI 同款配额归属）。
/// </summary>
public sealed class AnthropicRequestBuilder
{
    /// <summary>进程内稳定的 device 标识兜底（配置未提供时生成一次）。</summary>
    private static string? _fallbackDeviceId;

    private readonly GatewayOptions _settings;

    public AnthropicRequestBuilder(GatewayOptions settings)
    {
        _settings = settings;
    }

    /// <summary>OAuth 通道配额归属设备标识：配置优先，否则进程内稳定生成。</summary>
    private string DeviceId => _settings.Provider.MetadataDeviceId is { Length: > 0 } d
        ? d
        : _fallbackDeviceId ??= Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

    public AnthropicMessageRequest Build(ChatCompletionRequest request)
    {
        var systemTexts = new List<string>();
        var messages = new List<AnthropicMessage>();

        foreach (var msg in request.Messages)
        {
            switch (msg.Role)
            {
                case "system":
                case "developer":
                    var sys = msg.ContentString;
                    if (!string.IsNullOrWhiteSpace(sys)) systemTexts.Add(sys);
                    break;

                case "user":
                    messages.Add(new AnthropicMessage
                    {
                        Role = "user",
                        Content = BuildUserContent(msg.Content)
                    });
                    break;

                case "assistant":
                    var content = new List<AnthropicContentBlock>();
                    var text = msg.ContentString;
                    if (!string.IsNullOrEmpty(text))
                    {
                        content.Add(new AnthropicContentBlock { Type = "text", Text = text });
                    }
                    foreach (var call in msg.ToolCalls ?? new List<OpenAiToolCall>())
                    {
                        var name = call.Function.Name;
                        if (string.IsNullOrEmpty(name))
                            throw new ArgumentException("assistant tool_call is missing function.name");
                        JsonElement input;
                        var args = call.Function.Arguments;
                        if (string.IsNullOrWhiteSpace(args))
                        {
                            input = JsonDocument.Parse("{}").RootElement.Clone();
                        }
                        else
                        {
                            try
                            {
                                input = JsonDocument.Parse(args).RootElement.Clone();
                            }
                            catch (JsonException)
                            {
                                throw new ArgumentException(
                                    $"tool_call '{name}' arguments are not valid JSON: {Truncate(args, 120)}");
                            }
                        }
                        content.Add(new AnthropicContentBlock
                        {
                            Type = "tool_use",
                            Id = string.IsNullOrEmpty(call.Id) ? "call_" + Guid.NewGuid().ToString("N")[..20] : call.Id,
                            Name = name,
                            Input = input
                        });
                    }
                    if (content.Count == 0)
                        throw new ArgumentException("assistant message has neither content nor tool_calls");
                    messages.Add(new AnthropicMessage { Role = "assistant", Content = content });
                    break;

                case "tool":
                    if (string.IsNullOrEmpty(msg.ToolCallId))
                        throw new ArgumentException("tool message is missing tool_call_id");
                    messages.Add(new AnthropicMessage
                    {
                        Role = "user",
                        Content = new List<AnthropicContentBlock>
                        {
                            new()
                            {
                                Type = "tool_result",
                                ToolUseId = msg.ToolCallId,
                                ResultContent = msg.ContentString ?? "",
                                IsError = false
                            }
                        }
                    });
                    break;

                default:
                    throw new ArgumentException($"Unsupported message role '{msg.Role}'");
            }
        }

        if (messages.Count == 0)
            throw new ArgumentException("messages must contain at least one non-system message");

        var req = new AnthropicMessageRequest
        {
            Model = string.IsNullOrWhiteSpace(request.Model) ? _settings.DefaultModel : request.Model.Trim(),
            MaxTokens = request.MaxTokens ?? request.MaxCompletionTokens ?? _settings.DefaultMaxTokens,
            Messages = messages
        };

        // OAuth 通道必需的计费头系统块（缺失会被 Anthropic 归入最严格限流桶 → 全部 429）
        var billingText = _settings.Provider.BillingSystemPrompt;
        var systemJsonParts = new List<string> { "{\"type\":\"text\",\"text\":" + JsonSerializer.Serialize(billingText, JsonOptions.Anthropic) + "}" };
        var merged = string.Join("\n\n", systemTexts);
        if (!string.IsNullOrWhiteSpace(merged))
            systemJsonParts.Add("{\"type\":\"text\",\"text\":" + JsonSerializer.Serialize(merged, JsonOptions.Anthropic) + "}");
        req.System = JsonDocument.Parse("[" + string.Join(",", systemJsonParts) + "]").RootElement.Clone();

        // metadata.user_id：CLI 同款 device/account/session 归属（JSON 字符串内嵌 JSON）
        var sessionId = Guid.NewGuid().ToString("N")[..26];
        var userIdInner = JsonSerializer.Serialize(new
        {
            device_id = DeviceId,
            account_uuid = _settings.Provider.MetadataAccountId,
            session_id = sessionId
        }, JsonOptions.Anthropic);
        req.Metadata = JsonDocument.Parse("{\"user_id\":" + JsonSerializer.Serialize(userIdInner, JsonOptions.Anthropic) + "}").RootElement.Clone();

        // temperature / top_p：CLI 通道不发送（5.x 模型已废弃 temperature，一律省略）
        if (request.Stop is { } stop)
        {
            req.StopSequences = ExtractStopSequences(stop);
        }
        if (request.Tools is { Count: > 0 })
        {
            req.Tools = request.Tools.Select(t => new AnthropicTool
            {
                Name = t.Function.Name,
                Description = t.Function.Description,
                InputSchema = t.Function.Parameters is { } p
                    ? JsonDocument.Parse(JsonSerializer.Serialize(p, JsonOptions.Anthropic)).RootElement
                    : JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}").RootElement
            }).ToList();
        }
        if (request.ToolChoice is { } tc)
        {
            req.ToolChoice = MapToolChoice(tc, req.Tools);
        }

        // 推理 / 思考深度配置（reasoning_effort 映射为 Anthropic thinking & output_config）
        ConfigureThinking(req, request);

        return req;
    }

    /// <summary>
    /// 将 OpenAI 格式的 reasoning_effort / 思考预算映射为 Anthropic 协议。
    /// 4.6+ / 5.x 模型采用 adaptive thinking + output_config.effort；
    /// 3.7 / 4.5 及更早支持思考的模型采用 budget-based thinking（type: "enabled", budget_tokens）。
    /// </summary>
    private void ConfigureThinking(AnthropicMessageRequest req, ChatCompletionRequest request)
    {
        var rawEffort = request.ReasoningEffort?.Trim().ToLowerInvariant();
        var model = req.Model.ToLowerInvariant();
        var hasThinkingSuffix = model.EndsWith("-thinking");

        // 若模型显式以 -thinking 结尾，且未指定 effort 或为 off，则默认开启中等思考
        if (hasThinkingSuffix && (string.IsNullOrEmpty(rawEffort) || rawEffort is "off" or "none"))
        {
            rawEffort = "medium";
        }

        // 关闭思考
        if (string.IsNullOrEmpty(rawEffort) || rawEffort is "off" or "none")
        {
            return;
        }

        // 归一化 effort（OpenAI / DSH 常见值：minimal, low, medium, high, xhigh, max）
        var effort = rawEffort switch
        {
            "minimal" or "low" => "low",
            "medium" => "medium",
            "high" => "high",
            "xhigh" or "max" => "max",
            _ => rawEffort
        };

        var isAdaptive = IsAdaptiveThinkingModel(model);

        if (isAdaptive)
        {
            req.Thinking = new AnthropicThinkingConfig
            {
                Type = "adaptive"
            };
            req.OutputConfig = new AnthropicOutputConfig
            {
                Effort = effort
            };

            // 自适应思考下确保 max_tokens 留有足够思考和回复空间
            if (req.MaxTokens < 4096)
            {
                req.MaxTokens = 4096;
            }
        }
        else
        {
            // 基于 token 预算的思考模式（Claude 3.7 / 4.5 等）
            var budget = request.ThinkingBudget ?? request.ThinkingBudgetTokens ?? effort switch
            {
                "low" => 2048,
                "medium" => 8192,
                "high" => 16384,
                "max" => 32768,
                _ => 8192
            };

            budget = Math.Max(1024, budget);

            req.Thinking = new AnthropicThinkingConfig
            {
                Type = "enabled",
                BudgetTokens = budget
            };

            // Anthropic 要求 max_tokens 必须严格大于 budget_tokens
            if (req.MaxTokens <= budget)
            {
                req.MaxTokens = budget + Math.Max(2048, _settings.DefaultMaxTokens);
            }
        }
    }

    /// <summary>判断模型是否支持自适应思考（Claude 4.6+ / 5.x / fable / opus-4-6+）。</summary>
    private static bool IsAdaptiveThinkingModel(string model)
    {
        var m = model.ToLowerInvariant();
        if (m.Contains("fable-5") || m.Contains("opus-5") || m.Contains("sonnet-5") || m.Contains("haiku-5"))
            return true;
        if (m.Contains("opus-4-6") || m.Contains("opus-4-7") || m.Contains("opus-4-8") || m.Contains("sonnet-4-6"))
            return true;
        if (m.Contains("-5-") || m.Contains("-5."))
            return true;
        return false;
    }

    /// <summary>用户消息内容：纯文本 / 多部件 text + image_url（data URI 或 URL 下载转 base64）。</summary>
    private List<AnthropicContentBlock> BuildUserContent(object? content)
    {
        var blocks = new List<AnthropicContentBlock>();

        if (content is null)
        {
            blocks.Add(new AnthropicContentBlock { Type = "text", Text = "" });
            return blocks;
        }

        if (content is string s)
        {
            blocks.Add(new AnthropicContentBlock { Type = "text", Text = s });
            return blocks;
        }

        if (content is JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.String)
            {
                blocks.Add(new AnthropicContentBlock { Type = "text", Text = el.GetString() ?? "" });
                return blocks;
            }
            if (el.ValueKind != JsonValueKind.Array)
            {
                throw new ArgumentException("Unsupported content element kind");
            }

            foreach (var item in el.EnumerateArray())
            {
                var type = item.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString() : "text";

                if (type == "text")
                {
                    var text = item.TryGetProperty("text", out var tx) ? tx.GetString() : "";
                    blocks.Add(new AnthropicContentBlock { Type = "text", Text = text ?? "" });
                }
                else if (type == "image_url")
                {
                    var url = item.TryGetProperty("image_url", out var iu) && iu.ValueKind == JsonValueKind.Object
                        ? (iu.TryGetProperty("url", out var u) ? u.GetString() : null)
                        : null;
                    if (string.IsNullOrEmpty(url))
                        throw new ArgumentException("image_url part is missing url");
                    var (mediaType, data) = ResolveImage(url!);
                    blocks.Add(new AnthropicContentBlock
                    {
                        Type = "image",
                        Source = new AnthropicImageSource { MediaType = mediaType, Data = data }
                    });
                }
                else
                {
                    throw new ArgumentException($"Unsupported content part type '{type}'");
                }
            }
            return blocks;
        }

        throw new ArgumentException("Unsupported content payload");
    }

    /// <summary>data:image/png;base64,... 直接解码；http(s):// URL 下载后转 base64。</summary>
    private (string MediaType, string Data) ResolveImage(string url)
    {
        const string prefix = "data:";
        if (url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var comma = url.IndexOf(',');
            if (comma < 0) throw new ArgumentException("Malformed data URI image");
            var header = url[prefix.Length..comma]; // image/png;base64
            var base64 = url[(comma + 1)..];
            var mediaType = header.Split(';')[0].Trim();
            if (mediaType.Length == 0) mediaType = "image/png";
            return (mediaType, base64.Trim());
        }

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Image url must be a data URI or an http(s) URL");
        }

        using var handler = new HttpClientHandler();
        handler.AllowAutoRedirect = true;
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        using var resp = http.GetAsync(url).GetAwaiter().GetResult();
        if (!resp.IsSuccessStatusCode)
            throw new ArgumentException($"Failed to download image {url}: HTTP {(int)resp.StatusCode}");
        var bytes = resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        var contentType = resp.Content.Headers.ContentType?.MediaType ?? "image/png";
        if (!contentType.StartsWith("image/")) contentType = "image/png";
        return (contentType, Convert.ToBase64String(bytes));
    }

    private static List<string> ExtractStopSequences(object stop)
    {
        const int maxSeqs = 4;
        if (stop is JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.String)
            {
                var v = el.GetString();
                return string.IsNullOrEmpty(v) ? new List<string>() : new List<string> { v };
            }
            if (el.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var item in el.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } sv)
                        list.Add(sv);
                    if (list.Count >= maxSeqs) break;
                }
                return list;
            }
            return new List<string>();
        }
        if (stop is string str)
            return str.Length == 0 ? new List<string>() : new List<string> { str };
        if (stop is IEnumerable<string> seq)
            return seq.Where(x => !string.IsNullOrEmpty(x)).Take(maxSeqs).ToList();
        return new List<string>();
    }

    private AnthropicToolChoice? MapToolChoice(object choice, List<AnthropicTool>? tools)
    {
        const string openAiNone = "none";
        string type;
        string? name = null;

        if (choice is JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.String)
            {
                type = el.GetString() ?? "auto";
            }
            else if (el.ValueKind == JsonValueKind.Object)
            {
                type = el.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString() ?? "auto" : "auto";
                if (type == "function" && el.TryGetProperty("function", out var f))
                {
                    name = f.TryGetProperty("name", out var n) ? n.GetString() : null;
                    type = "tool";
                }
            }
            else
            {
                return null;
            }
        }
        else if (choice is string s)
        {
            type = s;
        }
        else
        {
            // 兼容强类型反序列化失败的情况：按 JsonNode 处理
            var json = JsonSerializer.Serialize(choice, JsonOptions.Anthropic);
            return MapToolChoice(JsonDocument.Parse(json).RootElement, tools);
        }

        if (type == "tool" && string.IsNullOrEmpty(name))
        {
            // 有具名 tool_choice 时 tools 必须存在，否则 Anthropic 直接 400
            if (tools is not { Count: > 0 }) return null;
            return null; // 无法确定 name，回退 auto 由后续 auto 处理
        }

        return name is not null
            ? new AnthropicToolChoice { Type = "tool", Name = name }
            : new AnthropicToolChoice { Type = type switch
              {
                  openAiNone => "none",
                  "required" => "any",
                  "any" => "any",
                  _ => "auto"
              } };
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "...";
}
