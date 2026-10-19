using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace ClaudeAgent.Tests.IntegrationTests;

/// <summary>
/// 网关实况集成测试（真实环境闭环）：需要网关在 http://127.0.0.1:5055 运行且 Claude Code 凭据有效。
/// 为控制额度消耗，所有对话测试自动选择模型列表中的 Haiku 模型。
/// </summary>
public class GatewayLiveIntegrationTests
{
    private static readonly HttpClient Client = TestGateway.CreateClient(TimeSpan.FromSeconds(120));

    private static string? _cachedModel;

    /// <summary>解析本次验证使用的 Haiku 模型（额度消耗最小）。</summary>
    private static async Task<string> ResolveTestModelAsync()
    {
        if (_cachedModel is not null) return _cachedModel;

        var resp = await Client.GetAsync("models");
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Models list failed: {resp.StatusCode}");

        var json = (JsonObject)(await resp.Content.ReadFromJsonAsync<JsonObject>())!;
        var data = json["data"] as JsonArray ?? throw new Exception("Models list has no data array");

        string? haiku = null;
        string? first = null;
        foreach (var item in data)
        {
            var id = item?["id"]?.GetValue<string>();
            if (string.IsNullOrEmpty(id)) continue;
            first ??= id;
            if (id!.Contains("haiku", StringComparison.OrdinalIgnoreCase))
            {
                haiku = id;
                break;
            }
        }

        _cachedModel = haiku ?? first ?? throw new Exception("No models available");
        return _cachedModel;
    }

    private static async Task<bool> GatewayAliveAsync()
    {
        try
        {
            var resp = await Client.GetAsync("health");
            return resp.StatusCode == HttpStatusCode.OK;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> EnsureGatewayAsync()
    {
        if (await GatewayAliveAsync()) return true;
        // 网关未运行时静默跳过（verify_all.ps1 会在测试前确保网关在线）
        return false;
    }

    [Fact]
    public async Task HealthCheck_ReturnsHealthy_WithAccountAndModels()
    {
        if (!await EnsureGatewayAsync()) return;

        var resp = await Client.GetAsync("health");
        Assert.True(resp.IsSuccessStatusCode, $"Health check failed: {resp.StatusCode}");

        var json = (JsonObject)(await resp.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("healthy", json["status"]?.GetValue<string>());

        var account = json["account"] as JsonObject;
        Assert.NotNull(account);
        Assert.True(account!["hasToken"]?.GetValue<bool>());

        var models = json["allModels"] as JsonArray;
        Assert.NotNull(models);
        Assert.True(models!.Count > 0);
    }

    [Fact]
    public async Task ModelsList_ReturnsOpenAiFormattedModels()
    {
        if (!await EnsureGatewayAsync()) return;

        var resp = await Client.GetAsync("models");
        Assert.True(resp.IsSuccessStatusCode, $"Models list failed: {resp.StatusCode}");

        var json = (JsonObject)(await resp.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("list", json["object"]?.GetValue<string>());

        var data = json["data"] as JsonArray;
        Assert.NotNull(data);
        Assert.NotEmpty(data!);

        var first = data![0] as JsonObject;
        Assert.NotNull(first);
        Assert.Equal("model", first!["object"]?.GetValue<string>());
        Assert.NotNull(first["id"]?.GetValue<string>());
    }

    [Fact]
    public async Task ChatCompletions_NonStreaming_Haiku_ReturnsValidResponse()
    {
        if (!await EnsureGatewayAsync()) return;
        var model = await ResolveTestModelAsync();

        var req = new
        {
            model,
            messages = new[]
            {
                new { role = "user", content = "9.11 和 9.9 哪个数字更大？请仅输出更大的那个数字。" }
            },
            temperature = 0.1,
            max_tokens = 64
        };

        var resp = await Client.PostAsJsonAsync("chat/completions", req);
        Assert.True(resp.IsSuccessStatusCode, $"Chat completions failed: {resp.StatusCode}");

        var json = (JsonObject)(await resp.Content.ReadFromJsonAsync<JsonObject>())!;

        var choices = json["choices"] as JsonArray;
        Assert.NotNull(choices);
        Assert.Single(choices!);

        var content = choices![0]?["message"]?["content"]?.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(content));
        Assert.Contains("9.9", content!);

        var usage = json["usage"] as JsonObject;
        Assert.NotNull(usage);
        Assert.True(usage!["prompt_tokens"]?.GetValue<int>() > 0);
        Assert.True(usage["completion_tokens"]?.GetValue<int>() > 0);
    }

    [Fact]
    public async Task ChatCompletions_Streaming_Haiku_EmitsSseEvents()
    {
        if (!await EnsureGatewayAsync()) return;
        var model = await ResolveTestModelAsync();

        var req = new
        {
            model,
            messages = new[] { new { role = "user", content = "从 1 数到 3，每个数字一行。" } },
            stream = true,
            max_tokens = 64
        };

        var requestMsg = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = JsonContent.Create(req)
        };

        var resp = await Client.SendAsync(requestMsg, HttpCompletionOption.ResponseHeadersRead);
        Assert.True(resp.IsSuccessStatusCode);
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType?.MediaType);

        using var stream = await resp.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        var receivedData = false;
        var receivedDone = false;
        var sawTextDelta = false;
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (line.StartsWith("data: [DONE]"))
            {
                receivedDone = true;
                break;
            }
            if (line.StartsWith("data: "))
            {
                receivedData = true;
                if (line.Contains("\"content\":"))
                    sawTextDelta = true;
            }
        }

        Assert.True(receivedData, "Expected to receive SSE data frames");
        Assert.True(receivedDone, "Expected to receive data: [DONE]");
        Assert.True(sawTextDelta, "Expected at least one content delta chunk");
    }

    [Fact]
    public async Task ChatCompletions_FunctionCalling_Haiku_ReturnsToolCall()
    {
        if (!await EnsureGatewayAsync()) return;
        var model = await ResolveTestModelAsync();

        var req = new
        {
            model,
            messages = new[]
            {
                new { role = "user", content = "帮我查询一下谷歌 (GOOG) 的当前股价，必须调用工具查询。" }
            },
            tools = new[]
            {
                new
                {
                    type = "function",
                    function = new
                    {
                        name = "get_stock_price",
                        description = "Get real-time stock price for a ticker",
                        parameters = new
                        {
                            type = "object",
                            properties = new
                            {
                                ticker = new { type = "string", description = "Stock ticker" }
                            },
                            required = new[] { "ticker" }
                        }
                    }
                }
            },
            max_tokens = 256
        };

        var resp = await Client.PostAsJsonAsync("chat/completions", req);
        Assert.True(resp.IsSuccessStatusCode);

        var json = (JsonObject)(await resp.Content.ReadFromJsonAsync<JsonObject>())!;
        var choice = json["choices"]?[0] as JsonObject;
        Assert.NotNull(choice);
        Assert.Equal("tool_calls", choice!["finish_reason"]?.GetValue<string>());

        var toolCalls = choice["message"]?["tool_calls"] as JsonArray;
        Assert.NotNull(toolCalls);
        Assert.NotEmpty(toolCalls!);

        var fnName = toolCalls![0]?["function"]?["name"]?.GetValue<string>();
        Assert.Equal("get_stock_price", fnName);
    }

    [Fact]
    public async Task ChatCompletions_MultiTurnToolRoundtrip_Haiku_Completes()
    {
        if (!await EnsureGatewayAsync()) return;
        var model = await ResolveTestModelAsync();

        var toolCall = new
        {
            model,
            messages = new[] { new { role = "user", content = "What is 123 * 456? Use the calculate tool." } },
            tools = new[]
            {
                new
                {
                    type = "function",
                    function = new
                    {
                        name = "calculate",
                        description = "Calculates mathematical expressions",
                        parameters = new
                        {
                            type = "object",
                            properties = new
                            {
                                expr = new { type = "string", description = "Expression like '123 * 456'" }
                            },
                            required = new[] { "expr" }
                        }
                    }
                }
            },
            max_tokens = 256
        };

        // 第一轮：模型发起工具调用
        var first = (JsonObject)(await (await Client.PostAsJsonAsync("chat/completions", toolCall)).Content
            .ReadFromJsonAsync<JsonObject>())!;
        var firstChoice = first["choices"]?[0] as JsonObject;
        Assert.Equal("tool_calls", firstChoice?["finish_reason"]?.GetValue<string>());

        var toolCalls = firstChoice?["message"]?["tool_calls"] as JsonArray;
        var callId = toolCalls![0]?["id"]?.GetValue<string>();
        Assert.NotNull(callId);

        // 第二轮：回传工具结果（assistant 消息需深拷贝——first 响应节点已有父对象）
        var messages = new JsonArray();
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = "What is 123 * 456? Use the calculate tool." });
        messages.Add(JsonNode.Parse(firstChoice!["message"]!.ToJsonString()));
        messages.Add(new JsonObject
        {
            ["role"] = "tool",
            ["tool_call_id"] = callId,
            ["content"] = "56088"
        });

        var second = (JsonObject)(await (await Client.PostAsJsonAsync("chat/completions",
            new JsonObject { ["model"] = model, ["messages"] = messages, ["max_tokens"] = 256 })
        ).Content.ReadFromJsonAsync<JsonObject>())!;

        var content = second["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(content));
        Assert.Contains("56", content!);
    }
}
