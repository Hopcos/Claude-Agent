using System.ClientModel;
using System.Net;
using System.Text.Json.Nodes;
using OpenAI;
using OpenAI.Chat;
using Xunit;

namespace ClaudeAgent.Tests.IntegrationTests;

/// <summary>微软官方 OpenAI .NET SDK 直连网关的端到端验证（自动选择 Haiku 模型控制额度消耗）。</summary>
public class OfficialOpenAiDotNetSdkTests
{
    private static readonly HttpClient ProbeClient = TestGateway.CreateClient(TimeSpan.FromSeconds(30));

    private static async Task<ChatClient?> CreateClientAsync()
    {
        // 探活：网关未运行则静默跳过（verify_all.ps1 会在测试前确保网关在线）
        try
        {
            var resp = await ProbeClient.GetAsync("health");
            if (resp.StatusCode != HttpStatusCode.OK)
                return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }

        // 从 /v1/models 选择 Haiku（额度消耗最小）
        var modelsJson = JsonNode.Parse(
            (await ProbeClient.GetStringAsync("models"))!) as JsonObject;
        string model = "claude-haiku-4-5";
        if (modelsJson?["data"] is JsonArray data)
        {
            foreach (var item in data)
            {
                var id = item?["id"]?.GetValue<string>();
                if (id is not null && id.Contains("haiku", StringComparison.OrdinalIgnoreCase))
                {
                    model = id;
                    break;
                }
            }
        }

        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri("http://127.0.0.1:5055/v1")
        };
        var client = new OpenAIClient(new ApiKeyCredential(TestGateway.ApiKey), options);
        return client.GetChatClient(model);
    }

    [Fact]
    public async Task OfficialSdk_CompleteChat_ReturnsResponse()
    {
        var chatClient = await CreateClientAsync();
        if (chatClient is null) return;
        ChatCompletion completion = await chatClient.CompleteChatAsync("What is the capital of France? Reply in one word.");

        Assert.NotNull(completion);
        Assert.NotEmpty(completion.Content);
        var text = string.Concat(completion.Content.Select(c => c.Text));
        Assert.Contains("Paris", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ChatFinishReason.Stop, completion.FinishReason);
    }

    [Fact]
    public async Task OfficialSdk_CompleteChatStreaming_ReceivesUpdates()
    {
        var chatClient = await CreateClientAsync();
        if (chatClient is null) return;
        var streamedChunks = 0;
        var accumulated = new System.Text.StringBuilder();

        AsyncCollectionResult<StreamingChatCompletionUpdate> updates =
            chatClient.CompleteChatStreamingAsync("Count 1, 2, 3 in order");

        await foreach (StreamingChatCompletionUpdate update in updates)
        {
            streamedChunks++;
            foreach (ChatMessageContentPart part in update.ContentUpdate)
            {
                accumulated.Append(part.Text);
            }
        }

        Assert.True(streamedChunks > 0);
        Assert.NotEmpty(accumulated.ToString());
    }

    [Fact]
    public async Task OfficialSdk_MultiTurnToolCalling_ExecutesSuccessfully()
    {
        var chatClient = await CreateClientAsync();
        if (chatClient is null) return;
        ChatTool calculateTool = ChatTool.CreateFunctionTool(
            functionName: "calculate",
            functionDescription: "Calculates mathematical expressions",
            functionParameters: BinaryData.FromString("""
            {
                "type": "object",
                "properties": {
                    "expr": { "type": "string", "description": "Expression like '123 * 456'" }
                },
                "required": ["expr"]
            }
            """)
        );

        var options = new ChatCompletionOptions
        {
            Tools = { calculateTool }
        };

        var messages = new List<ChatMessage>
        {
            new UserChatMessage("What is 123 * 456? Use the calculate tool.")
        };

        // 1. 第一轮：模型发起工具调用
        ChatCompletion firstResponse = await chatClient.CompleteChatAsync(messages, options);

        Assert.Equal(ChatFinishReason.ToolCalls, firstResponse.FinishReason);
        Assert.NotEmpty(firstResponse.ToolCalls);

        ChatToolCall toolCall = firstResponse.ToolCalls[0];
        Assert.Equal("calculate", toolCall.FunctionName);
        // 网关透传 Anthropic 原生 toolu_ ID（OpenAI 规范不要求 call_ 前缀），仅断言非空
        Assert.False(string.IsNullOrEmpty(toolCall.Id));

        // 2. 第二轮：提供工具结果
        messages.Add(new AssistantChatMessage(firstResponse));
        messages.Add(new ToolChatMessage(toolCall.Id, "56088"));

        ChatCompletion secondResponse = await chatClient.CompleteChatAsync(messages, options);

        Assert.NotNull(secondResponse);
        var finalText = string.Concat(secondResponse.Content.Select(c => c.Text));
        Assert.Contains("56", finalText);
        Assert.Contains("088", finalText);
    }
}
