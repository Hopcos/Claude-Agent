using System.Text.Json;
using ClaudeAgent.Configuration;
using ClaudeAgent.Models.OpenAi;
using ClaudeAgent.Services;

namespace ClaudeAgent.Tests.UnitTests;

public class AnthropicRequestBuilderTests
{
    private static AnthropicRequestBuilder CreateBuilder() =>
        new(new GatewayOptions { DefaultModel = "claude-haiku-4-5", DefaultMaxTokens = 4096 });

    private static ChatMessage Msg(string role, object? content, string? toolCallId = null,
        List<OpenAiToolCall>? toolCalls = null) => new()
    {
        Role = role,
        Content = content,
        ToolCallId = toolCallId,
        ToolCalls = toolCalls
    };

    [Fact]
    public void Build_SystemMessages_MergeIntoTopLevelSystem()
    {
        var builder = CreateBuilder();
        var req = builder.Build(new ChatCompletionRequest
        {
            Messages = new List<ChatMessage>
            {
                Msg("system", "You are helpful."),
                Msg("user", "hi"),
                Msg("system", "Be concise.")
            }
        });

        // System 现为 block 数组：首块是 OAuth 通道必需的计费头，其后是合并后的提示
        var sys = Assert.IsType<JsonElement>(req.System);
        Assert.Equal(2, sys.GetArrayLength());
        Assert.Equal("x-anthropic-billing-header: cc_version=2.1.301.eb5; cc_entrypoint=sdk-cli;",
            sys[0].GetProperty("text").GetString());
        Assert.Equal("You are helpful.\n\nBe concise.", sys[1].GetProperty("text").GetString());
        Assert.Single(req.Messages);
        Assert.Equal("user", req.Messages[0].Role);
        Assert.Equal("hi", req.Messages[0].Content[0].Text);
    }

    [Fact]
    public void Build_AssistantToolCalls_MapToToolUseBlocks()
    {
        var builder = CreateBuilder();
        var req = builder.Build(new ChatCompletionRequest
        {
            Messages = new List<ChatMessage>
            {
                Msg("user", "weather?"),
                Msg("assistant", null, toolCalls: new List<OpenAiToolCall>
                {
                    new()
                    {
                        Id = "call_abc",
                        Type = "function",
                        Function = new OpenAiToolCallFunction { Name = "get_weather", Arguments = "{\"city\":\"NYC\"}" }
                    }
                }),
                Msg("tool", "sunny", toolCallId: "call_abc")
            }
        });

        Assert.Equal(3, req.Messages.Count);

        var assistant = req.Messages[1];
        Assert.Equal("assistant", assistant.Role);
        var toolUse = assistant.Content.Single(b => b.Type == "tool_use");
        Assert.Equal("call_abc", toolUse.Id);
        Assert.Equal("get_weather", toolUse.Name);
        Assert.Equal("NYC", JsonDocument.Parse(toolUse.Input!.Value.GetRawText()).RootElement.GetProperty("city").GetString());

        var toolResult = req.Messages[2];
        Assert.Equal("user", toolResult.Role);
        var resultBlock = toolResult.Content.Single();
        Assert.Equal("tool_result", resultBlock.Type);
        Assert.Equal("call_abc", resultBlock.ToolUseId);
        Assert.Equal("sunny", resultBlock.ResultContent);
    }

    [Fact]
    public void Build_Tools_MapInputSchema()
    {
        var builder = CreateBuilder();
        var parameters = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { city = new { type = "string" } },
            required = new[] { "city" }
        });

        var req = builder.Build(new ChatCompletionRequest
        {
            Messages = new List<ChatMessage> { Msg("user", "weather?") },
            Tools = new List<OpenAiTool>
            {
                new()
                {
                    Type = "function",
                    Function = new OpenAiToolFunction { Name = "get_weather", Description = "Get weather", Parameters = parameters }
                }
            }
        });

        Assert.Single(req.Tools!);
        Assert.Equal("get_weather", req.Tools[0].Name);
        Assert.Equal("Get weather", req.Tools[0].Description);
        Assert.NotNull(req.Tools[0].InputSchema);
        Assert.Equal("object",
            JsonDocument.Parse(req.Tools[0].InputSchema!.Value.GetRawText()).RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public void Build_ToolChoice_MapsNoneAutoRequiredAndNamed()
    {
        var builder = CreateBuilder();

        var none = builder.Build(new ChatCompletionRequest
        {
            Messages = new List<ChatMessage> { Msg("user", "hi") },
            ToolChoice = JsonSerializer.SerializeToElement("none")
        });
        Assert.Equal("none", none.ToolChoice!.Type);

        var auto = builder.Build(new ChatCompletionRequest
        {
            Messages = new List<ChatMessage> { Msg("user", "hi") },
            ToolChoice = JsonSerializer.SerializeToElement("auto")
        });
        Assert.Equal("auto", auto.ToolChoice!.Type);

        var named = builder.Build(new ChatCompletionRequest
        {
            Messages = new List<ChatMessage> { Msg("user", "hi") },
            ToolChoice = JsonSerializer.SerializeToElement(new
            {
                type = "function",
                function = new { name = "get_weather" }
            }),
            Tools = new List<OpenAiTool>
            {
                new() { Function = new OpenAiToolFunction { Name = "get_weather" } }
            }
        });
        Assert.Equal("tool", named.ToolChoice!.Type);
        Assert.Equal("get_weather", named.ToolChoice.Name);
    }

    [Fact]
    public void Build_MaxTokens_Temperature_TopP_Stop()
    {
        var builder = CreateBuilder();
        var req = builder.Build(new ChatCompletionRequest
        {
            Messages = new List<ChatMessage> { Msg("user", "hi") },
            MaxTokens = 100,
            Temperature = 1.5, // OpenAI 允许 >1，Anthropic 上限 1
            TopP = 0.9,
            Stop = JsonSerializer.SerializeToElement(new[] { "END", "STOP" })
        });

        Assert.Equal(100, req.MaxTokens);
        // OAuth/CLI 通道不发送 temperature / top_p（5.x 模型已废弃 temperature）
        Assert.Null(req.Temperature);
        Assert.Null(req.TopP);
        Assert.Equal(new[] { "END", "STOP" }, req.StopSequences);
    }

    [Fact]
    public void Build_MissingMaxTokens_UsesDefault()
    {
        var builder = CreateBuilder();
        var req = builder.Build(new ChatCompletionRequest
        {
            Messages = new List<ChatMessage> { Msg("user", "hi") }
        });
        Assert.Equal(4096, req.MaxTokens);
    }

    [Fact]
    public void Build_DataUriImage_MapsToImageBlock()
    {
        var builder = CreateBuilder();
        var content = JsonDocument.Parse("""
            [
              { "type": "text", "text": "what is this?" },
              { "type": "image_url", "image_url": { "url": "data:image/png;base64,iVBORw0KGgo=" } }
            ]
            """).RootElement;

        var req = builder.Build(new ChatCompletionRequest
        {
            Messages = new List<ChatMessage> { Msg("user", content) }
        });

        Assert.Equal(2, req.Messages[0].Content.Count);
        Assert.Equal("text", req.Messages[0].Content[0].Type);
        Assert.Equal("image", req.Messages[0].Content[1].Type);
        Assert.Equal("image/png", req.Messages[0].Content[1].Source!.MediaType);
        Assert.Equal("iVBORw0KGgo=", req.Messages[0].Content[1].Source!.Data);
    }

    [Fact]
    public void Build_ToolMessageWithoutToolCallId_Throws()
    {
        var builder = CreateBuilder();
        Assert.Throws<ArgumentException>(() => builder.Build(new ChatCompletionRequest
        {
            Messages = new List<ChatMessage> { Msg("tool", "x") }
        }));
    }

    [Fact]
    public void Build_OnlySystemMessages_Throws()
    {
        var builder = CreateBuilder();
        Assert.Throws<ArgumentException>(() => builder.Build(new ChatCompletionRequest
        {
            Messages = new List<ChatMessage> { Msg("system", "hi") }
        }));
    }
}
