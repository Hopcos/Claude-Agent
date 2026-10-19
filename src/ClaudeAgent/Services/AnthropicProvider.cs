using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeAgent.Configuration;
using ClaudeAgent.Models.Anthropic;

namespace ClaudeAgent.Services;

/// <summary>
/// Anthropic 第一方 OAuth Provider：
/// 直接代理本机已登录 Claude Code 账号，将请求转发至 https://api.anthropic.com/v1/messages，
/// 携带 OAuth Bearer token、anthropic-beta: oauth-2025-04-20 与 claude-cli User-Agent。
/// 支持流式 SSE 与非流式 JSON，内置 401 自愈重试、429/5xx 退避重试与并发闸门。
/// </summary>
public sealed class AnthropicProvider : IAnthropicProvider
{
    private readonly ProviderOptions _opts;
    private readonly ClaudeAccountService _account;
    private readonly HttpClient _http;
    private readonly ILogger<AnthropicProvider> _log;
    private readonly SemaphoreSlim _requestGate;
    private List<string> _models;

    public AnthropicProvider(
        ProviderOptions opts,
        ClaudeAccountService account,
        ILogger<AnthropicProvider> log)
    {
        _opts = opts;
        _account = account;
        _log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(600) };
        var conc = Math.Max(1, opts.MaxConcurrency);
        _requestGate = new SemaphoreSlim(conc, conc);

        _models = opts.Models.Count > 0
            ? new List<string>(opts.Models)
            : new List<string>
            {
                "claude-haiku-4-5",
                "claude-sonnet-4-5",
                "claude-opus-4-5",
                "claude-opus-4-1",
                "claude-sonnet-4-0",
                "claude-3-7-sonnet",
                "claude-3-5-haiku"
            };

        _log.LogInformation("AnthropicProvider initialized. BaseUrl={Url}, Concurrency={Conc}", BaseUrl, conc);
    }

    public string Name => "Anthropic";
    public IReadOnlyList<string> Models => _models;

    private string BaseUrl => _opts.BaseUrl.TrimEnd('/');

    /// <summary>CLI 同款 messages 端点（含 ?beta=true 查询参数）。</summary>
    private string MessagesUrl()
    {
        var q = _opts.MessagesQuery;
        return string.IsNullOrWhiteSpace(q) ? $"{BaseUrl}/v1/messages" : $"{BaseUrl}/v1/messages?{q.TrimStart('?')}";
    }

    private int MaxAttempts => _opts.MaxAttempts > 0 ? _opts.MaxAttempts : 4;

    private void SetOAuthHeaders(HttpRequestMessage msg, string token)
    {
        msg.Headers.Authorization = new("Bearer", token);
        msg.Headers.TryAddWithoutValidation("anthropic-version", _opts.AnthropicVersion);
        msg.Headers.TryAddWithoutValidation("anthropic-beta", _opts.AnthropicBeta);
        msg.Headers.TryAddWithoutValidation("User-Agent", _opts.UserAgent);
    }

    // ---------- 非流式 ----------

    public async Task<AnthropicResponse> GenerateAsync(
        string model, AnthropicMessageRequest request, CancellationToken ct)
    {
        request.Model = model;
        request.Stream = false;
        var json = JsonSerializer.Serialize(request, JsonOptions.Anthropic);
        var url = MessagesUrl();

        await _requestGate.WaitAsync(ct);
        try
        {
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                var token = await _account.GetTokenAsync(false, ct);

                using var msg = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
                };
                SetOAuthHeaders(msg, token.Value);

                try
                {
                    using var resp = await _http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct);

                    if (resp.StatusCode == HttpStatusCode.Unauthorized && attempt == 1)
                    {
                        _log.LogWarning("Anthropic upstream returned 401; forcing token refresh and retrying once");
                        await _account.ForceRefreshAsync(ct);
                        continue;
                    }

                    if ((int)resp.StatusCode == 429 || (int)resp.StatusCode >= 500)
                    {
                        var errBody = await resp.Content.ReadAsStringAsync(ct);
                        resp.Dispose();
                        if (attempt < MaxAttempts)
                        {
                            var delay = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempt), 15));
                            _log.LogWarning("Anthropic {Code}; retry {Attempt}/{Max} after {Delay}s: {Body}",
                                (int)resp.StatusCode, attempt, MaxAttempts, delay.TotalSeconds, Truncate(errBody, 200));
                            await Task.Delay(delay, ct);
                            continue;
                        }
                        throw new UpstreamException(resp.StatusCode, errBody);
                    }

                    if (!resp.IsSuccessStatusCode)
                    {
                        var errBody = await resp.Content.ReadAsStringAsync(ct);
                        resp.Dispose();
                        throw new UpstreamException(resp.StatusCode, errBody);
                    }

                    var body = await resp.Content.ReadAsStringAsync(ct);
                    return JsonSerializer.Deserialize<AnthropicResponse>(body, JsonOptions.Anthropic)
                        ?? throw new UpstreamException(HttpStatusCode.BadGateway, "Failed to parse upstream response");
                }
                catch (Exception ex) when (ex is not UpstreamException && ex is not TokenRefreshException
                                           && attempt < MaxAttempts && IsTransient(ex))
                {
                    _log.LogWarning(ex, "Transient connection error during send; retry {Attempt}/{Max}",
                        attempt, MaxAttempts);
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempt), 15)), ct);
                    continue;
                }
            }

            throw new UpstreamException(HttpStatusCode.BadGateway, "Anthropic upstream failed after retries.");
        }
        finally
        {
            _requestGate.Release();
        }
    }

    // ---------- 流式 ----------

    public async IAsyncEnumerable<AnthropicStreamEvent> GenerateStreamAsync(
        string model, AnthropicMessageRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        request.Model = model;
        request.Stream = true;
        var json = JsonSerializer.Serialize(request, JsonOptions.Anthropic);
        var url = MessagesUrl();

        await _requestGate.WaitAsync(ct);
        try
        {
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                var token = await _account.GetTokenAsync(false, ct);

                using var msg = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
                };
                SetOAuthHeaders(msg, token.Value);

                HttpResponseMessage? resp = null;
                Stream? stream = null;
                StreamReader? reader = null;

                try
                {
                    resp = await _http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct);

                    if (resp.StatusCode == HttpStatusCode.Unauthorized && attempt == 1)
                    {
                        _log.LogWarning("Anthropic upstream returned 401 during stream setup; forcing refresh and retrying");
                        await _account.ForceRefreshAsync(ct);
                        resp.Dispose();
                        continue;
                    }

                    if ((int)resp.StatusCode == 429 || (int)resp.StatusCode >= 500)
                    {
                        var errBody = await resp.Content.ReadAsStringAsync(ct);
                        resp.Dispose();
                        if (attempt < MaxAttempts)
                        {
                            var delay = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempt), 15));
                            _log.LogWarning("Anthropic {Code}; retry {Attempt}/{Max} after {Delay}s: {Body}",
                                (int)resp.StatusCode, attempt, MaxAttempts, delay.TotalSeconds, Truncate(errBody, 200));
                            await Task.Delay(delay, ct);
                            continue;
                        }
                        throw new UpstreamException(resp.StatusCode, errBody);
                    }

                    if (!resp.IsSuccessStatusCode)
                    {
                        var errBody = await resp.Content.ReadAsStringAsync(ct);
                        resp.Dispose();
                        throw new UpstreamException(resp.StatusCode, errBody);
                    }

                    stream = await resp.Content.ReadAsStreamAsync(ct);
                    reader = new StreamReader(stream);
                }
                catch (Exception ex) when (ex is not UpstreamException && ex is not TokenRefreshException
                                           && attempt < MaxAttempts && IsTransient(ex))
                {
                    _log.LogWarning(ex, "Transient connection error during stream send; retry {Attempt}/{Max}",
                        attempt, MaxAttempts);
                    resp?.Dispose();
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempt), 15)), ct);
                    continue;
                }

                if (reader is null) continue;

                try
                {
                    // Anthropic SSE 帧结构：
                    //   event: <type>\n
                    //   data: {json}\n
                    //   \n
                    string? eventName = null;
                    while (!ct.IsCancellationRequested)
                    {
                        var line = await reader.ReadLineAsync(ct);
                        if (line is null) break;

                        var trimmed = line.Trim();
                        if (trimmed.Length == 0)
                        {
                            eventName = null;
                            continue;
                        }
                        if (trimmed.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
                        {
                            eventName = trimmed["event:".Length..].Trim();
                            continue;
                        }
                        if (!trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var data = trimmed[5..].Trim();
                        if (string.IsNullOrEmpty(data) || data == "[DONE]") continue;

                        AnthropicStreamEvent? evt = null;
                        try
                        {
                            evt = JsonSerializer.Deserialize<AnthropicStreamEvent>(data, JsonOptions.Anthropic);
                        }
                        catch (Exception ex)
                        {
                            _log.LogWarning(ex, "Failed to parse SSE data chunk: {Data}", Truncate(data, 120));
                        }

                        if (evt is not null)
                        {
                            if (evt.Type == "error" || evt.Error is not null)
                            {
                                var message = evt.Error?.Message ?? "Upstream stream error event";
                                throw new UpstreamException(HttpStatusCode.BadGateway, message);
                            }
                            yield return evt;
                        }
                        eventName = null;
                    }
                    yield break; // 正常读取完毕
                }
                finally
                {
                    reader.Dispose();
                    if (stream != null) await stream.DisposeAsync();
                    resp?.Dispose();
                }
            }

            throw new UpstreamException(HttpStatusCode.BadGateway, "Anthropic upstream failed after retries.");
        }
        finally
        {
            _requestGate.Release();
        }
    }

    // ---------- 动态模型发现 ----------

    /// <summary>调用上游 GET /v1/models 获取账号名下全部可用模型。</summary>
    public async Task<List<string>> RefreshAvailableModelsAsync(CancellationToken ct = default)
    {
        var found = new List<string>();
        try
        {
            var token = await _account.GetTokenAsync(false, ct);

            // Anthropic /v1/models 分页：follow has_more + next（data_cursor / offset 视端点而定）
            string? requestUri = $"{BaseUrl}/v1/models?limit=100";
            for (var page = 0; page < 10 && requestUri is not null; page++)
            {
                using var msg = new HttpRequestMessage(HttpMethod.Get, requestUri);
                SetOAuthHeaders(msg, token.Value);

                using var resp = await _http.SendAsync(msg, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    var errBody = await resp.Content.ReadAsStringAsync(ct);
                    // 部分部署对 limit=100 返回 400：退回默认 limit 重试一次
                    if (resp.StatusCode == HttpStatusCode.BadRequest && page == 0)
                    {
                        requestUri = $"{BaseUrl}/v1/models";
                        continue;
                    }
                    throw new UpstreamException(resp.StatusCode, errBody);
                }

                var body = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in data.EnumerateArray())
                    {
                        if (item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                        {
                            var mid = id.GetString();
                            if (!string.IsNullOrEmpty(mid)) found.Add(mid!);
                        }
                    }
                }

                requestUri = null;
                if (root.TryGetProperty("has_more", out var more) && more.ValueKind == JsonValueKind.True)
                {
                    // 尝试官方 next-page 指针（不同网关字段名不一，逐一探测）
                    if (root.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.String)
                        requestUri = $"{BaseUrl}/v1/models?limit=100&next={Uri.EscapeDataString(next.GetString() ?? "")}";
                    else if (root.TryGetProperty("last_id", out var last) && last.ValueKind == JsonValueKind.String)
                        requestUri = $"{BaseUrl}/v1/models?limit=100&last_id={Uri.EscapeDataString(last.GetString() ?? "")}";
                    else if (root.TryGetProperty("next_page", out var np) && np.ValueKind == JsonValueKind.String)
                        requestUri = np.GetString();
                }
            }

            if (found.Count > 0)
            {
                // 常见简写别名并入（若上游只暴露带日期戳的完整 ID，则补齐无日期的短 ID 提高兼容性）
                var aliases = new List<string>();
                foreach (var m in found)
                {
                    var short1 = ShortModelAlias(m);
                    if (short1 is not null && !found.Contains(short1) && !aliases.Contains(short1))
                        aliases.Add(short1);
                }
                found.AddRange(aliases);
                found = found.Distinct().ToList();

                _models = found;
                _log.LogInformation("Discovered {Count} active models from Anthropic upstream", found.Count);
                return found;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to fetch available models from upstream; using defaults");
        }
        return _models;
    }

    /// <summary>claude-sonnet-4-5-20250929 -> claude-sonnet-4-5 等短别名。</summary>
    private static string? ShortModelAlias(string modelId)
    {
        if (modelId.StartsWith("claude-") && modelId.Count(c => c == '-') >= 3)
        {
            // claude-<family>-<ver>-<datestamp>
            var parts = modelId.Split('-');
            var last = parts[^1];
            if (last.Length >= 8 && last.All(char.IsDigit))
            {
                return string.Join('-', parts[..^1]);
            }
        }
        return null;
    }

    private static bool IsTransient(Exception ex) => ex is HttpRequestException or IOException or TimeoutException;

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Trim();
        return s.Length <= max ? s : s[..max] + "...";
    }
}
