using System.Security.Cryptography;
using System.Text.Json;
using ClaudeAgent.Models.Anthropic;
using ClaudeAgent.Models.OpenAi;

namespace ClaudeAgent.Services;

/// <summary>Anthropic Messages 响应/流事件 -> OpenAI Chat Completions 映射器。</summary>
public sealed class AnthropicToOpenAiMapper
{
    // ---------- 非流式 ----------

    public ChatCompletionResponse MapResponse(AnthropicResponse from, string model)
    {
        var message = new ChatResponseMessage();
        var sb = new System.Text.StringBuilder();
        var toolCalls = new List<OpenAiToolCall>();
        var reasoningTokens = 0;

        if (from.Content is { } content && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                var type = block.TryGetProperty("type", out var t) ? t.GetString() : null;
                switch (type)
                {
                    case "text":
                        if (block.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String)
                            sb.Append(tx.GetString());
                        break;

                    case "tool_use":
                        toolCalls.Add(new OpenAiToolCall
                        {
                            Id = block.TryGetProperty("id", out var id) ? id.GetString() : null,
                            Type = "function",
                            Function = new OpenAiToolCallFunction
                            {
                                Name = block.TryGetProperty("name", out var n) ? n.GetString() : null,
                                Arguments = SerializeInput(block)
                            }
                        });
                        break;

                    case "thinking":
                        // 非流式思维链统计进 reasoning_tokens（内容不外露，与 OpenAI 规范一致）
                        if (block.TryGetProperty("thinking", out var th) && th.ValueKind == JsonValueKind.String)
                            reasoningTokens += EstimateTokens(th.GetString());
                        break;
                }
            }
        }

        message.Content = sb.Length > 0 ? sb.ToString() : null;
        if (toolCalls.Count > 0) message.ToolCalls = toolCalls;

        var finishReason = toolCalls.Count > 0
            ? "tool_calls"
            : MapStopReason(from.StopReason);

        return new ChatCompletionResponse
        {
            Id = "chatcmpl-" + RandomNumberGenerator.GetHexString(24),
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Model = model,
            Choices = new List<ChatChoice>
            {
                new()
                {
                    Index = 0,
                    Message = message,
                    FinishReason = finishReason
                }
            },
            Usage = MapUsage(from.Usage)
        };
    }

    public static ChatUsage? MapUsage(AnthropicUsage? u)
    {
        if (u is null) return null;
        var cached = u.CacheReadInputTokens ?? 0;
        return new ChatUsage
        {
            PromptTokens = u.InputTokens,
            CompletionTokens = u.OutputTokens,
            TotalTokens = u.InputTokens + u.OutputTokens,
            PromptTokensDetails = cached > 0
                ? new ChatUsagePromptDetails { CachedTokens = cached }
                : null
        };
    }

    private static int EstimateTokens(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : Math.Max(1, text.Length / 4);

    private static string SerializeInput(JsonElement toolUseBlock)
    {
        if (toolUseBlock.TryGetProperty("input", out var input))
        {
            try
            {
                return JsonSerializer.Serialize(input.Clone(), JsonOptions.Anthropic);
            }
            catch
            {
                // fall through
            }
        }
        return "{}";
    }

    // ---------- 流式 ----------

    /// <summary>
    /// 流式块状态：追踪 Anthropic content block index -> OpenAI tool_call index 的映射。
    /// 每个上游会话新建一个实例，按事件顺序调用 MapChunk。
    /// </summary>
    public sealed class StreamMapper
    {
        private readonly string _id;
        private readonly long _created;
        private readonly string _model;
        private readonly Dictionary<int, char> _blockKind = new(); // t=text, r=reasoning, c=tool
        private int _nextToolIndex;
        private string? _stopReason;
        private AnthropicUsage? _usage;
        private int _reasoningTokens;

        public StreamMapper(string id, long created, string model)
        {
            _id = id;
            _created = created;
            _model = model;
        }

        /// <summary>message_start 事件里的 input_tokens。</summary>
        public void RecordMessageStartUsage(AnthropicUsage? usage)
        {
            if (usage is not null) _usage = usage;
        }

        /// <summary>将一个上游事件映射为 0..n 个 OpenAI chunk。</summary>
        public IEnumerable<ChatCompletionChunk> MapChunk(AnthropicStreamEvent evt)
        {
            switch (evt.Type)
            {
                case "message_start":
                {
                    if (evt.Message is { } m && m.ValueKind == JsonValueKind.Object)
                    {
                        if (m.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
                        {
                            var usage = ParseUsage(u);
                            if (usage is not null) _usage = MergeUsage(_usage, usage);
                        }
                    }
                    // 首个 chunk：声明 assistant 角色
                    yield return Chunk(new ChatChunkDelta { Role = "assistant" });
                    break;
                }

                case "content_block_start":
                {
                    var idx = evt.Index ?? 0;
                    var (kind, toolMeta) = ParseBlockStart(evt.ContentBlock);
                    _blockKind[idx] = kind;
                    if (kind == 'c' && toolMeta is not null)
                    {
                        var callIndex = _nextToolIndex++;
                        yield return Chunk(new ChatChunkDelta
                        {
                            ToolCalls = new List<OpenAiToolCall>
                            {
                                new()
                                {
                                    StreamIndex = callIndex,
                                    Id = toolMeta.Value.Id,
                                    Type = "function",
                                    Function = new OpenAiToolCallFunction { Name = toolMeta.Value.Name, Arguments = "" }
                                }
                            }
                        });
                    }
                    break;
                }

                case "content_block_delta":
                {
                    var idx = evt.Index ?? 0;
                    var kind = _blockKind.TryGetValue(idx, out var k) ? k : 't';
                    var delta = evt.Delta;
                    if (delta is null || delta.Value.ValueKind != JsonValueKind.Object) break;

                    var deltaType = delta.Value.TryGetProperty("type", out var dt) ? dt.GetString() : null;

                    if (deltaType == "text_delta" && (kind == 't' || kind == 'r'))
                    {
                        var text = delta.Value.TryGetProperty("text", out var tx) ? tx.GetString() : null;
                        if (!string.IsNullOrEmpty(text))
                            yield return Chunk(new ChatChunkDelta { Content = text });
                    }
                    else if (deltaType == "thinking_delta" || (deltaType == "text_delta" && kind == 'r'))
                    {
                        var think = deltaType == "thinking_delta"
                            ? (delta.Value.TryGetProperty("thinking", out var tv) ? tv.GetString() : null)
                            : (delta.Value.TryGetProperty("text", out var tv2) ? tv2.GetString() : null);
                        if (!string.IsNullOrEmpty(think))
                        {
                            _reasoningTokens += EstimateTokens(think);
                            yield return Chunk(new ChatChunkDelta { ReasoningContent = think });
                        }
                    }
                    else if (deltaType == "input_json_delta" && kind == 'c')
                    {
                        var partial = delta.Value.TryGetProperty("partial_json", out var pj) ? pj.GetString() : null;
                        if (!string.IsNullOrEmpty(partial))
                        {
                            var callIndex = ToolIndexForBlock(idx);
                            yield return Chunk(new ChatChunkDelta
                            {
                                ToolCalls = new List<OpenAiToolCall>
                                {
                                    new()
                                    {
                                        StreamIndex = callIndex,
                                        Function = new OpenAiToolCallFunction { Arguments = partial }
                                    }
                                }
                            });
                        }
                    }
                    break;
                }

                case "content_block_stop":
                    break;

                case "message_delta":
                {
                    if (evt.Delta is { } d && d.ValueKind == JsonValueKind.Object)
                    {
                        _stopReason = d.TryGetProperty("stop_reason", out var sr) ? sr.GetString() : null;
                    }
                    if (evt.Usage is { } u && u.ValueKind == JsonValueKind.Object)
                    {
                        var usage = ParseUsage(u);
                        if (usage is not null) _usage = MergeUsage(_usage, usage);
                    }
                    if (_stopReason is not null)
                    {
                        yield return new ChatCompletionChunk
                        {
                            Id = _id,
                            Created = _created,
                            Model = _model,
                            Choices = new List<ChatChunkChoice>
                            {
                                new()
                                {
                                    Index = 0,
                                    Delta = new ChatChunkDelta(),
                                    FinishReason = MapStopReason(_stopReason)
                                }
                            }
                        };
                    }
                    break;
                }

                case "message_stop":
                    yield break;

                default:
                    break;
            }
        }

        /// <summary>流结束时的 usage（可能为 null）。</summary>
        public ChatUsage? Finalize()
        {
            var usage = _usage;
            if (usage is not null && _reasoningTokens > 0)
            {
                return new ChatUsage
                {
                    PromptTokens = usage.InputTokens,
                    CompletionTokens = usage.OutputTokens,
                    TotalTokens = usage.InputTokens + usage.OutputTokens,
                    PromptTokensDetails = (usage.CacheReadInputTokens ?? 0) > 0
                        ? new ChatUsagePromptDetails { CachedTokens = usage.CacheReadInputTokens }
                        : null,
                    CompletionTokensDetails = new ChatUsageCompletionDetails { ReasoningTokens = _reasoningTokens }
                };
            }
            return MapUsage(usage);
        }

        private ChatCompletionChunk Chunk(ChatChunkDelta delta) => new()
        {
            Id = _id,
            Created = _created,
            Model = _model,
            Choices = new List<ChatChunkChoice>
            {
                new()
                {
                    Index = 0,
                    Delta = delta
                }
            }
        };

        private int ToolIndexForBlock(int blockIndex)
        {
            // content_block_start 时已按出现顺序分配 OpenAI tool index
            return _blockKind.TryGetValue(blockIndex, out var k) && k == 'c'
                ? CountToolBlocksBefore(blockIndex)
                : 0;
        }

        private int CountToolBlocksBefore(int blockIndex)
        {
            var count = 0;
            foreach (var (idx, kind) in _blockKind.OrderBy(x => x.Key))
            {
                if (idx < blockIndex && kind == 'c') count++;
            }
            return count;
        }

        private static (char Kind, (string? Id, string? Name)? Tool) ParseBlockStart(JsonElement? block)
        {
            if (block is null || block.Value.ValueKind != JsonValueKind.Object) return ('t', null);
            var type = block.Value.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "tool_use")
            {
                return ('c', (
                    block.Value.TryGetProperty("id", out var id) ? id.GetString() : null,
                    block.Value.TryGetProperty("name", out var n) ? n.GetString() : null));
            }
            if (type == "thinking" || type == "redacted_thinking") return ('r', null);
            return ('t', null);
        }

        private static AnthropicUsage? ParseUsage(JsonElement u)
        {
            try
            {
                return JsonSerializer.Deserialize<AnthropicUsage>(u.Clone().GetRawText(), JsonOptions.Anthropic);
            }
            catch
            {
                return null;
            }
        }

        private static AnthropicUsage MergeUsage(AnthropicUsage? a, AnthropicUsage b)
        {
            var result = a is null
                ? new AnthropicUsage()
                : new AnthropicUsage
                {
                    InputTokens = a.InputTokens,
                    OutputTokens = a.OutputTokens,
                    CacheCreationInputTokens = a.CacheCreationInputTokens,
                    CacheReadInputTokens = a.CacheReadInputTokens
                };
            if (b.InputTokens > 0) result.InputTokens = b.InputTokens;
            if (b.OutputTokens > 0) result.OutputTokens = b.OutputTokens;
            if (b.CacheCreationInputTokens is { } c && c > 0) result.CacheCreationInputTokens = c;
            if (b.CacheReadInputTokens is { } r && r > 0) result.CacheReadInputTokens = r;
            return result;
        }
    }

    public static string? MapStopReason(string? reason) => reason?.ToLowerInvariant() switch
    {
        null or "" => null,
        "end_turn" or "stop_sequence" or "pause_turn" => "stop",
        "max_tokens" => "length",
        "tool_use" => "tool_calls",
        "refusal" => "content_filter",
        _ => "stop"
    };
}
