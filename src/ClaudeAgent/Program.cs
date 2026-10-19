using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeAgent;
using ClaudeAgent.Configuration;
using ClaudeAgent.Models.Anthropic;
using ClaudeAgent.Models.OpenAi;
using ClaudeAgent.Services;

var builder = WebApplication.CreateBuilder(args);

// 日志配置：单行时间戳
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff ";
    o.SingleLine = true;
});

// JSON Options
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

var settings = GatewaySettings.Load(builder.Configuration);

builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(settings.Auth);
builder.Services.AddSingleton(settings.Provider);

// 注册核心服务
builder.Services.AddSingleton(sp =>
    new ClaudeAccountService(
        sp.GetRequiredService<AuthOptions>(),
        settings.WriteBackToCredentialsFile,
        sp.GetRequiredService<ILogger<ClaudeAccountService>>()));
builder.Services.AddSingleton<AnthropicRequestBuilder>();
builder.Services.AddSingleton<AnthropicToOpenAiMapper>();

builder.Services.AddSingleton<IAnthropicProvider>(sp =>
    new AnthropicProvider(
        sp.GetRequiredService<ProviderOptions>(),
        sp.GetRequiredService<ClaudeAccountService>(),
        sp.GetRequiredService<ILogger<AnthropicProvider>>()));

builder.Services.AddSingleton<ProviderRegistry>();
builder.Services.AddHostedService<TokenRefresherBackgroundService>();

var app = builder.Build();

var json = JsonOptions.OpenAi;
var logger = app.Logger;
var accountService = app.Services.GetRequiredService<ClaudeAccountService>();
var builderSvc = app.Services.GetRequiredService<AnthropicRequestBuilder>();
var mapper = app.Services.GetRequiredService<AnthropicToOpenAiMapper>();
var registry = app.Services.GetRequiredService<ProviderRegistry>();

// 可选：网关 API Key 鉴权（配置 CLAUDE_API_KEY / GATEWAY_API_KEY 后生效）
if (!string.IsNullOrEmpty(settings.ApiKey))
{
    app.Use(async (ctx, next) =>
    {
        if (ctx.Request.Path.StartsWithSegments("/v1"))
        {
            var auth = ctx.Request.Headers.Authorization.ToString();
            if (!string.Equals(auth, "Bearer " + settings.ApiKey, StringComparison.Ordinal))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsJsonAsync(
                    new OpenAiErrorBody
                    {
                        Error = new OpenAiError
                        {
                            Message = "Invalid or missing gateway API key.",
                            Type = "invalid_request_error",
                            Code = "invalid_api_key"
                        }
                    },
                    json);
                return;
            }
        }
        await next(ctx);
    });
}

// —— 健康检查与状态端点 ——
app.MapGet("/", () => Results.Json(new
{
    service = "ClaudeAgent OpenAI Compatible Gateway",
    version = "1.0.0",
    account = accountService.GetStatus(),
    defaultModel = registry.DefaultModel,
    modelsCount = registry.AllModels.Count
}, json));

app.MapGet("/healthz", () => Results.Text("ok"));

app.MapGet("/v1/health", () => Results.Json(new
{
    status = "healthy",
    account = accountService.GetStatus(),
    defaultModel = registry.DefaultModel,
    allModels = registry.AllModels
}, json));

// —— OpenAI 模型列表端点 ——
app.MapGet("/v1/models", () => Results.Json(
    new ModelListResponse
    {
        Data = registry.AllModels
            .Select(m => new ModelInfo { Id = m, Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds() })
            .ToList()
    },
    json));

// —— OpenAI Chat Completions 核心端点 ——
app.MapPost("/v1/chat/completions", async (HttpContext ctx, ChatCompletionRequest? request) =>
{
    AnthropicMessageRequest? anthropicReq = null;
    try
    {
        if (request is null)
        {
            return ErrorJson(400, "Request body is required and must be valid JSON.",
                "invalid_request_error", "invalid_request");
        }

        var (model, provider) = registry.Resolve(request.Model);
        anthropicReq = builderSvc.Build(request);

        var lastRole = request.Messages.Count > 0 ? request.Messages[^1].Role : "(none)";
        logger.LogInformation(
            "chat/completions model={Model} stream={Stream} messages={Count} lastRole={LastRole}",
            model, request.Stream == true, request.Messages.Count, lastRole);

        // 流式响应处理
        if (request.Stream == true)
        {
            var chunkId = ProviderRegistry.NewChatId();
            var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var ct = ctx.RequestAborted;

            return Results.Stream(
                async (Stream body) =>
                {
                    await using var writer = new StreamWriter(body, leaveOpen: true);
                    var wroteAny = false;
                    try
                    {
                        var streamMapper = new AnthropicToOpenAiMapper.StreamMapper(chunkId, created, model);
                        await foreach (var evt in provider.GenerateStreamAsync(model, anthropicReq!, ct))
                        {
                            var mapped = streamMapper.MapChunk(evt);
                            foreach (var chunk in mapped)
                            {
                                var payload = JsonSerializer.Serialize(chunk, json);
                                await writer.WriteAsync("data: " + payload + "\n\n");
                                await writer.FlushAsync(ct);
                                wroteAny = true;
                            }
                        }

                        // 收尾用量块与 [DONE]
                        var usage = streamMapper.Finalize();
                        if (usage is not null)
                        {
                            var usageChunk = JsonSerializer.Serialize(
                                new ChatCompletionChunk
                                {
                                    Id = chunkId,
                                    Created = created,
                                    Model = model,
                                    Choices = new List<ChatChunkChoice>(),
                                    Usage = usage
                                }, json);
                            await writer.WriteAsync("data: " + usageChunk + "\n\n");
                        }
                        await writer.WriteAsync("data: [DONE]\n\n");
                        await writer.FlushAsync(ct);
                    }
                    catch (UpstreamException ex)
                    {
                        if (!wroteAny)
                        {
                            ctx.Response.StatusCode = (int)ex.StatusCode;
                            await writer.WriteAsync(JsonSerializer.Serialize(
                                new OpenAiErrorBody
                                {
                                    Error = new OpenAiError
                                    {
                                        Message = ex.Message,
                                        Type = "upstream_error",
                                        Code = "upstream_error"
                                    }
                                }, json));
                        }
                        else
                        {
                            await writer.WriteAsync("data: " + JsonSerializer.Serialize(
                                new OpenAiErrorBody
                                {
                                    Error = new OpenAiError
                                    {
                                        Message = ex.Message,
                                        Type = "upstream_error",
                                        Code = "upstream_error"
                                    }
                                }, json) + "\n\n");
                            await writer.WriteAsync("data: [DONE]\n\n");
                        }
                        await writer.FlushAsync(ct);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Error during streaming");
                        await writer.WriteAsync("data: " + JsonSerializer.Serialize(
                            new OpenAiErrorBody
                            {
                                Error = new OpenAiError
                                {
                                    Message = "Internal error: " + ex.Message,
                                    Type = "api_error",
                                    Code = "internal_error"
                                }
                            }, json) + "\n\n");
                        await writer.WriteAsync("data: [DONE]\n\n");
                        await writer.FlushAsync(ct);
                    }
                },
                "text/event-stream");
        }

        // 非流式响应处理
        var resp = await provider.GenerateAsync(model, anthropicReq, ctx.RequestAborted);
        return Results.Json(mapper.MapResponse(resp, model), json);
    }
    catch (ModelNotFoundException ex)
    {
        return ErrorJson(404,
            $"The model '{ex.Model}' does not exist. Supported models: {string.Join(", ", registry.AllModels)}",
            "invalid_request_error", "model_not_found");
    }
    catch (ArgumentException ex)
    {
        return ErrorJson(400, ex.Message, "invalid_request_error", "invalid_request");
    }
    catch (TokenRefreshException ex)
    {
        logger.LogError(ex, "Token refresh failed");
        return ErrorJson(502, ex.Message, "api_error", "token_refresh_failed");
    }
    catch (UpstreamException ex)
    {
        logger.LogWarning("Upstream error: {Message}", ex.Message);
        return ErrorJson((int)ex.StatusCode, ex.Message, "upstream_error", "upstream_error");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Unhandled error in completions");
        return ErrorJson(500, "Internal error: " + ex.Message, "api_error", "internal_error");
    }
});

IResult ErrorJson(int status, string message, string type, string code) =>
    Results.Json(
        new OpenAiErrorBody { Error = new OpenAiError { Message = message, Type = type, Code = code } },
        json,
        statusCode: status);

app.Run();
