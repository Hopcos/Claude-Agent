using System.Text.Json;
using ClaudeAgent.Models.Anthropic;
using ClaudeAgent.Services;

namespace ClaudeAgent.Tests.UnitTests;

public class AnthropicToOpenAiMapperTests
{
    private static AnthropicResponse ParseResponse(string json) =>
        JsonSerializer.Deserialize<AnthropicResponse>(json, ClaudeAgent.JsonOptions.Anthropic)!;

    [Fact]
    public void MapResponse_TextOnly()
    {
        var mapper = new AnthropicToOpenAiMapper();
        var resp = ParseResponse("""
            {
              "id": "msg_1",
              "model": "claude-haiku-4-5",
              "role": "assistant",
              "stop_reason": "end_turn",
              "content": [ { "type": "text", "text": "Hello world" } ],
              "usage": { "input_tokens": 10, "output_tokens": 5 }
            }
            """);

        var mapped = mapper.MapResponse(resp, "claude-haiku-4-5");

        Assert.Equal("Hello world", mapped.Choices[0].Message.Content);
        Assert.Equal("stop", mapped.Choices[0].FinishReason);
        Assert.Null(mapped.Choices[0].Message.ToolCalls);
        Assert.Equal(10, mapped.Usage!.PromptTokens);
        Assert.Equal(5, mapped.Usage.CompletionTokens);
        Assert.Equal(15, mapped.Usage.TotalTokens);
        Assert.StartsWith("chatcmpl-", mapped.Id);
    }

    [Fact]
    public void MapResponse_ToolUse_ProducesToolCallsAndFinishReason()
    {
        var mapper = new AnthropicToOpenAiMapper();
        var resp = ParseResponse("""
            {
              "id": "msg_2",
              "stop_reason": "tool_use",
              "content": [
                { "type": "text", "text": "Let me check." },
                { "type": "tool_use", "id": "toolu_1", "name": "get_weather", "input": { "city": "NYC" } }
              ],
              "usage": { "input_tokens": 20, "output_tokens": 30 }
            }
            """);

        var mapped = mapper.MapResponse(resp, "claude-haiku-4-5");

        Assert.Equal("tool_calls", mapped.Choices[0].FinishReason);
        Assert.Equal("Let me check.", mapped.Choices[0].Message.Content);
        var call = mapped.Choices[0].Message.ToolCalls!.Single();
        Assert.Equal("toolu_1", call.Id);
        Assert.Equal("get_weather", call.Function.Name);
        Assert.Equal("NYC", JsonDocument.Parse(call.Function.Arguments!).RootElement.GetProperty("city").GetString());
    }

    [Fact]
    public void MapResponse_CacheReadTokens_PopulatePromptDetails()
    {
        var mapper = new AnthropicToOpenAiMapper();
        var resp = ParseResponse("""
            {
              "stop_reason": "end_turn",
              "content": [ { "type": "text", "text": "ok" } ],
              "usage": { "input_tokens": 100, "output_tokens": 10, "cache_read_input_tokens": 80 }
            }
            """);

        var mapped = mapper.MapResponse(resp, "m");
        Assert.Equal(80, mapped.Usage!.PromptTokensDetails!.CachedTokens);
    }

    [Theory]
    [InlineData("end_turn", "stop")]
    [InlineData("stop_sequence", "stop")]
    [InlineData("max_tokens", "length")]
    [InlineData("tool_use", "tool_calls")]
    [InlineData("refusal", "content_filter")]
    public void MapStopReason_MapsAnthropicToOpenAi(string stopReason, string expected)
    {
        Assert.Equal(expected, AnthropicToOpenAiMapper.MapStopReason(stopReason));
    }

    [Fact]
    public void StreamMapper_TextDeltas_EmitContentChunks()
    {
        var mapper = new AnthropicToOpenAiMapper();
        var stream = new AnthropicToOpenAiMapper.StreamMapper("chatcmpl-x", 1, "m");

        var events = new[]
        {
            new AnthropicStreamEvent
            {
                Type = "message_start",
                Message = JsonDocument.Parse("""{"usage":{"input_tokens":12}}""").RootElement
            },
            new AnthropicStreamEvent
            {
                Type = "content_block_start",
                Index = 0,
                ContentBlock = JsonDocument.Parse("""{"type":"text","text":""}""").RootElement
            },
            new AnthropicStreamEvent
            {
                Type = "content_block_delta",
                Index = 0,
                Delta = JsonDocument.Parse("""{"type":"text_delta","text":"Hel"}""").RootElement
            },
            new AnthropicStreamEvent
            {
                Type = "content_block_delta",
                Index = 0,
                Delta = JsonDocument.Parse("""{"type":"text_delta","text":"lo"}""").RootElement
            },
            new AnthropicStreamEvent
            {
                Type = "message_delta",
                Delta = JsonDocument.Parse("""{"stop_reason":"end_turn"}""").RootElement,
                Usage = JsonDocument.Parse("""{"output_tokens":7}""").RootElement
            },
            new AnthropicStreamEvent { Type = "message_stop" }
        };

        var chunks = events.SelectMany(stream.MapChunk).ToList();

        Assert.Equal("assistant", chunks[0].Choices[0].Delta.Role);
        Assert.Equal("Hel", chunks[1].Choices[0].Delta.Content);
        Assert.Equal("lo", chunks[2].Choices[0].Delta.Content);
        Assert.Equal("stop", chunks[^1].Choices[0].FinishReason);

        var usage = stream.Finalize();
        Assert.NotNull(usage);
        Assert.Equal(12, usage.PromptTokens);
        Assert.Equal(7, usage.CompletionTokens);
        Assert.Equal(19, usage.TotalTokens);
    }

    [Fact]
    public void StreamMapper_ToolUse_EmitsIndexedToolCallDeltas()
    {
        var mapper = new AnthropicToOpenAiMapper();
        var stream = new AnthropicToOpenAiMapper.StreamMapper("chatcmpl-x", 1, "m");

        var events = new[]
        {
            new AnthropicStreamEvent { Type = "message_start" },
            new AnthropicStreamEvent
            {
                Type = "content_block_start",
                Index = 1,
                ContentBlock = JsonDocument.Parse("""{"type":"tool_use","id":"toolu_9","name":"get_weather"}""").RootElement
            },
            new AnthropicStreamEvent
            {
                Type = "content_block_delta",
                Index = 1,
                Delta = JsonDocument.Parse("""{"type":"input_json_delta","partial_json":"{\"ci"}""").RootElement
            },
            new AnthropicStreamEvent
            {
                Type = "content_block_delta",
                Index = 1,
                Delta = JsonDocument.Parse("""{"type":"input_json_delta","partial_json":"ty\":\"NYC\"}"}""").RootElement
            },
            new AnthropicStreamEvent
            {
                Type = "message_delta",
                Delta = JsonDocument.Parse("""{"stop_reason":"tool_use"}""").RootElement
            }
        };

        var chunks = events.SelectMany(stream.MapChunk).ToList();

        var startChunk = chunks.Single(c => c.Choices[0].Delta.ToolCalls is { Count: 1 }
            && c.Choices[0].Delta.ToolCalls[0].Function.Name == "get_weather");
        Assert.Equal(0, startChunk.Choices[0].Delta.ToolCalls[0].StreamIndex);
        Assert.Equal("toolu_9", startChunk.Choices[0].Delta.ToolCalls[0].Id);

        // 参数增量按同一 index 追加
        var argChunks = chunks.Where(c => c.Choices[0].Delta.ToolCalls is { Count: 1 }
            && !string.IsNullOrEmpty(c.Choices[0].Delta.ToolCalls[0].Function.Arguments)).ToList();
        Assert.Equal(2, argChunks.Count);
        Assert.All(argChunks, c => Assert.Equal(0, c.Choices[0].Delta.ToolCalls![0].StreamIndex));
        Assert.Equal("tool_calls", chunks[^1].Choices[0].FinishReason);
    }

    [Fact]
    public void StreamMapper_ThinkingDelta_EmitsReasoningContent()
    {
        var stream = new AnthropicToOpenAiMapper.StreamMapper("chatcmpl-x", 1, "m");
        var events = new[]
        {
            new AnthropicStreamEvent
            {
                Type = "message_start",
                Message = JsonDocument.Parse("""{"usage":{"input_tokens":5}}""").RootElement
            },
            new AnthropicStreamEvent
            {
                Type = "content_block_start",
                Index = 0,
                ContentBlock = JsonDocument.Parse("""{"type":"thinking","thinking":""}""").RootElement
            },
            new AnthropicStreamEvent
            {
                Type = "content_block_delta",
                Index = 0,
                Delta = JsonDocument.Parse("""{"type":"thinking_delta","thinking":"pondering"}""").RootElement
            },
            new AnthropicStreamEvent
            {
                Type = "message_delta",
                Delta = JsonDocument.Parse("""{"stop_reason":"end_turn"}""").RootElement,
                Usage = JsonDocument.Parse("""{"output_tokens":3}""").RootElement
            }
        };

        var chunks = events.SelectMany(stream.MapChunk).ToList();
        Assert.Contains(chunks, c => c.Choices[0].Delta.ReasoningContent == "pondering");

        var usage = stream.Finalize();
        Assert.NotNull(usage!.CompletionTokensDetails);
        Assert.True(usage.CompletionTokensDetails.ReasoningTokens > 0);
    }

    [Fact]
    public void StreamMapper_TextThenTool_KeepsDistinctToolIndexes()
    {
        var stream = new AnthropicToOpenAiMapper.StreamMapper("chatcmpl-x", 1, "m");
        var events = new[]
        {
            new AnthropicStreamEvent { Type = "message_start" },
            new AnthropicStreamEvent
            {
                Type = "content_block_start",
                Index = 0,
                ContentBlock = JsonDocument.Parse("""{"type":"text","text":""}""").RootElement
            },
            new AnthropicStreamEvent
            {
                Type = "content_block_start",
                Index = 1,
                ContentBlock = JsonDocument.Parse("""{"type":"tool_use","id":"toolu_a","name":"a"}""").RootElement
            },
            new AnthropicStreamEvent
            {
                Type = "content_block_start",
                Index = 2,
                ContentBlock = JsonDocument.Parse("""{"type":"tool_use","id":"toolu_b","name":"b"}""").RootElement
            }
        };

        var chunks = events.SelectMany(stream.MapChunk).ToList();
        var toolStarts = chunks.Where(c => c.Choices[0].Delta.ToolCalls is { Count: 1 }
            && c.Choices[0].Delta.ToolCalls[0].Function.Name is not null).ToList();

        Assert.Equal(2, toolStarts.Count);
        Assert.Equal(0, toolStarts[0].Choices[0].Delta.ToolCalls![0].StreamIndex);
        Assert.Equal(1, toolStarts[1].Choices[0].Delta.ToolCalls![0].StreamIndex);
    }
}
