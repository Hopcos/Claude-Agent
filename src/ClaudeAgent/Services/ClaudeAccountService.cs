using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeAgent.Configuration;

namespace ClaudeAgent.Services;

public readonly record struct AccessToken(string Value, DateTimeOffset ExpiresUtc, int TtlSeconds)
{
    public static readonly AccessToken Empty = new("", DateTimeOffset.MinValue, 0);

    public bool IsValidAt(DateTimeOffset now, int marginSeconds = 60) =>
        !string.IsNullOrEmpty(Value) && ExpiresUtc - now > TimeSpan.FromSeconds(marginSeconds);
}

public sealed class AccountStatus
{
    public string Account { get; set; } = "unknown";
    public string Source { get; set; } = "unknown";
    public bool HasToken { get; set; }
    public DateTimeOffset? ExpiresUtc { get; set; }
    public int? RemainingSeconds => ExpiresUtc.HasValue
        ? (int)Math.Max(0, (ExpiresUtc.Value - DateTimeOffset.UtcNow).TotalSeconds)
        : null;
    public string? Scopes { get; set; }
    public string? Subscription { get; set; }
}

/// <summary>
/// Claude Code 账号与凭据管理服务：
/// 1. 自动探测本机已登录 Claude Code 的 OAuth 凭据（~/.claude/.credentials.json，兼容 claudeAiOauth 与扁平两种结构）；
/// 2. 具备多级降级兜底：显式环境变量 -> 凭据文件 -> 网关自身状态文件；
/// 3. 管理 OAuth 2.0 双 Token 永续生命周期：使用官方 CLI 公开 client_id，凭 refresh_token 向
///    https://platform.claude.com/v1/oauth/token 静默换取新 access_token；
/// 4. 刷新结果原子化写回凭据文件与网关状态文件（refresh token 若发生轮换则同步持久化，避免旧 token 作废导致断供）；
/// 5. 支持懒刷新、主动保活（后台每分钟检查）与 401 反应式刷新重试。
/// </summary>
public sealed class ClaudeAccountService
{
    /// <summary>Claude Code 官方 CLI 的 OAuth 公开 client_id（逆向自 claude.exe 2.1.301）。</summary>
    public const string DefaultClaudeClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    public const string DefaultTokenEndpoint = "https://platform.claude.com/v1/oauth/token";
    public const string DefaultCredentialsPath = ".credentials.json";
    public const string StateFileName = "claude-agent-state.json";

    private readonly HttpClient _http;
    private readonly AuthOptions _auth;
    private readonly bool _writeBack;
    private readonly ILogger<ClaudeAccountService> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _credsGate = new(1, 1);

    private ResolvedCredentials? _creds;
    private AccessToken _current = AccessToken.Empty;

    public ClaudeAccountService(AuthOptions auth, bool writeBack, ILogger<ClaudeAccountService> log)
        : this(auth, writeBack, log, null)
    {
    }

    /// <summary>测试构造：允许注入伪造的 HttpMessageHandler 以拦截 OAuth 端点。</summary>
    internal ClaudeAccountService(AuthOptions auth, bool writeBack, ILogger<ClaudeAccountService> log,
        HttpMessageHandler? handler)
    {
        _auth = auth;
        _writeBack = writeBack;
        _log = log;
        _http = handler is null
            ? new HttpClient { Timeout = TimeSpan.FromSeconds(30) }
            : new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    internal sealed record ResolvedCredentials(
        string ClientId,
        string TokenEndpoint,
        string RefreshToken,
        string AccessToken,
        DateTimeOffset ExpiresAt,
        string Scopes,
        string Source,
        string? FilePath,
        bool IsClaudeAiOauthSchema);

    public AccountStatus GetStatus()
    {
        return new AccountStatus
        {
            Account = _creds?.FilePath is not null ? "claude-code" : "env-configured",
            Source = _creds?.Source ?? _auth.Source,
            HasToken = !string.IsNullOrEmpty(_current.Value),
            ExpiresUtc = _current.ExpiresUtc == DateTimeOffset.MinValue ? null : _current.ExpiresUtc,
            Scopes = _creds?.Scopes
        };
    }

    /// <summary>获取有效的 Access Token；force=true 时强制触发刷新。</summary>
    public async Task<AccessToken> GetTokenAsync(bool force = false, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (!force && _current.IsValidAt(now))
        {
            return _current;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (!force && _current.IsValidAt(DateTimeOffset.UtcNow))
            {
                return _current;
            }

            _current = await RefreshCoreAsync(ct);
            return _current;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<AccessToken> ForceRefreshAsync(CancellationToken ct = default) => GetTokenAsync(true, ct);

    /// <summary>供后台保活服务定期检查：剩余有效期不足阈值时提前续期。</summary>
    public Task RefreshIfExpiringSoonAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var t = _current;
        var threshold = TimeSpan.FromSeconds(Math.Max(t.TtlSeconds / 10, 300)); // 剩余不足 10% 或少于 5 分钟
        if (!t.IsValidAt(now, 0) || t.ExpiresUtc - now < threshold)
        {
            _log.LogInformation("Token approaching expiry (expires in {Secs:F0}s); initiating proactive background renewal",
                (t.ExpiresUtc - now).TotalSeconds);
            return GetTokenAsync(force: true, ct);
        }
        return Task.CompletedTask;
    }

    private async Task<AccessToken> RefreshCoreAsync(CancellationToken ct)
    {
        var creds = await GetCredentialsAsync(ct);

        // 首次加载且凭据源自带未过期的 access token 时直接复用（避免不必要的刷新）
        if (_current == AccessToken.Empty
            && !string.IsNullOrEmpty(creds.AccessToken)
            && creds.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(60))
        {
            var ttl = (int)Math.Max(60, (creds.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds);
            _log.LogInformation("Using active access token from {Source} (valid until {Exp:O})",
                creds.Source, creds.ExpiresAt);
            return new AccessToken(creds.AccessToken, creds.ExpiresAt, ttl);
        }

        var tokenEndpoint = string.IsNullOrWhiteSpace(creds.TokenEndpoint) ? DefaultTokenEndpoint : creds.TokenEndpoint;
        using var body = new StringContent(
            JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = creds.RefreshToken,
                ["client_id"] = creds.ClientId,
                ["scope"] = creds.Scopes
            }, JsonOptions.Anthropic),
            Encoding.UTF8,
            "application/json");

        using var resp = await _http.PostAsync(tokenEndpoint, body, ct);
        var respBody = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            var hint = respBody.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase)
                ? "Claude Code refresh token 已失效或被吊销，请在 Claude Code 中重新登录（claude auth login）"
                : "请检查 Claude Code 凭据配置";
            throw new TokenRefreshException($"Token refresh failed ({(int)resp.StatusCode}): {hint}. {Truncate(respBody, 300)}");
        }

        using var doc = JsonDocument.Parse(respBody);
        var root = doc.RootElement;
        var access = root.TryGetProperty("access_token", out var at) ? at.GetString() : null;
        if (string.IsNullOrEmpty(access))
        {
            throw new TokenRefreshException("OAuth token endpoint returned no access_token");
        }

        var expiresIn = root.TryGetProperty("expires_in", out var ei) && ei.TryGetInt32(out var e)
            ? e : 3600;
        expiresIn = Math.Max(expiresIn, 60);

        var newRefresh = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        var scope = root.TryGetProperty("scope", out var sc) ? sc.GetString() : creds.Scopes;
        if (string.IsNullOrWhiteSpace(scope)) scope = creds.Scopes;

        var ttlSeconds = expiresIn;
        // 预留 60s 安全保护期
        var expiresUtc = DateTimeOffset.UtcNow.AddSeconds(ttlSeconds - 60);

        // refresh token 轮换持久化（Anthropic 可能返回新的 refresh_token）
        if (!string.IsNullOrEmpty(newRefresh) && newRefresh != creds.RefreshToken)
        {
            creds = creds with { RefreshToken = newRefresh };
            _log.LogWarning("Anthropic rotated the refresh token; persisting the new one to keep the lifecycle perpetual");
        }

        creds = creds with { AccessToken = access, Scopes = scope };
        PersistCredentials(creds, DateTimeOffset.UtcNow.AddSeconds(expiresIn));

        _log.LogInformation("Claude Code token refreshed successfully (valid ~{Ttl}s, until {Exp:yyyy-MM-dd HH:mm:ss})",
            ttlSeconds, expiresUtc.ToLocalTime());

        return new AccessToken(access, expiresUtc, ttlSeconds);
    }

    /// <summary>将最新凭据状态原子化写回凭据文件（保持 Claude Code 可读结构）与网关状态文件。</summary>
    private void PersistCredentials(ResolvedCredentials creds, DateTimeOffset accessTokenExpiryUtc)
    {
        try
        {
            if (_writeBack && creds.FilePath is { Length: > 0 } filePath)
            {
                var abs = ExpandPath(filePath);
                var dir = Path.GetDirectoryName(abs);
                if (!string.IsNullOrEmpty(dir) && File.Exists(abs))
                {
                    var json = File.ReadAllText(abs);
                    JsonObject obj;
                    try
                    {
                        obj = JsonSerializer.Deserialize<JsonObject>(json, JsonOptions.Anthropic) ?? new JsonObject();
                    }
                    catch (JsonException)
                    {
                        obj = new JsonObject();
                    }

                    if (obj["claudeAiOauth"] is { } oauth)
                    {
                        oauth["accessToken"] = creds.AccessToken;
                        oauth["refreshToken"] = creds.RefreshToken;
                        oauth["expiresAt"] = accessTokenExpiryUtc.ToUnixTimeMilliseconds();
                        if (oauth["refreshTokenExpiresAt"] is null)
                        {
                            oauth["refreshTokenExpiresAt"] =
                                DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeMilliseconds();
                        }
                    }
                    else
                    {
                        obj["accessToken"] = creds.AccessToken;
                        obj["refreshToken"] = creds.RefreshToken;
                        obj["expiresAt"] = accessTokenExpiryUtc.ToUnixTimeMilliseconds();
                    }

                    // 原子写入：临时文件 + File.Replace（保留 .bak 备份）
                    var tmp = Path.Combine(dir!, $"~{Path.GetFileName(abs)}.tmp{Guid.NewGuid():N}");
                    var indent = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
                    File.WriteAllText(tmp, obj.ToJsonString(indent));
                    try
                    {
                        if (File.Exists(abs + ".bak")) File.Delete(abs + ".bak");
                        File.Replace(tmp, abs, abs + ".bak");
                    }
                    catch (PlatformNotSupportedException)
                    {
                        File.Delete(tmp);
                        File.WriteAllText(abs, obj.ToJsonString(indent));
                    }
                    _log.LogInformation("Refreshed credentials written back to {Path} (atomic replace)", abs);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to write back refreshed credentials; gateway state file will still be updated");
        }

        try
        {
            // 网关自身状态文件：默认与凭据文件同目录（~/.claude/），可用 CLAUDE_STATE_FILE 覆盖。
            // Docker/无头部署时指向可写卷，即使凭据文件只读也能永续。
            var statePath = string.IsNullOrWhiteSpace(_auth.StateFile)
                ? (creds.FilePath is { Length: > 0 } fp
                    ? Path.Combine(Path.GetDirectoryName(ExpandPath(fp)) ?? UserHomeDirectory(), StateFileName)
                    : Path.Combine(UserHomeDirectory(), ".claude", StateFileName))
                : ExpandPath(_auth.StateFile!);
            var state = new JsonObject
            {
                ["access_token"] = creds.AccessToken,
                ["refresh_token"] = creds.RefreshToken,
                ["client_id"] = creds.ClientId,
                ["token_endpoint"] = creds.TokenEndpoint,
                ["scopes"] = creds.Scopes,
                ["updated_utc"] = DateTimeOffset.UtcNow.ToString("O")
            };
            var indent2 = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(statePath, state.ToJsonString(indent2));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to persist gateway token state file");
        }
    }

    private async ValueTask<ResolvedCredentials> GetCredentialsAsync(CancellationToken ct)
    {
        if (_creds is not null) return _creds;

        await _credsGate.WaitAsync(ct);
        try
        {
            if (_creds is not null) return _creds;
            _creds = ResolveCredentials();
            return _creds;
        }
        finally
        {
            _credsGate.Release();
        }
    }

    private ResolvedCredentials ResolveCredentials()
    {
        var clientId = string.IsNullOrWhiteSpace(_auth.ClientId) ? DefaultClaudeClientId : _auth.ClientId;
        var tokenEndpoint = string.IsNullOrWhiteSpace(_auth.TokenEndpoint) ? DefaultTokenEndpoint : _auth.TokenEndpoint;

        // 1. 显式环境变量优先（Linux / Docker 无头部署）
        if (!string.IsNullOrWhiteSpace(_auth.RefreshToken))
        {
            _log.LogInformation("Loaded Claude Code refresh credentials from environment variables");
            var envScopes = Environment.GetEnvironmentVariable("CLAUDE_OAUTH_SCOPES")
                ?? "user:profile user:sessions:claude_code user:inference";
            return new ResolvedCredentials(clientId, tokenEndpoint, _auth.RefreshToken, "",
                DateTimeOffset.MinValue, envScopes, "Environment", null, false);
        }

        // 2. 凭据文件（默认 ~/.claude/.credentials.json）
        var filePath = string.IsNullOrWhiteSpace(_auth.CredentialsFile)
            ? Path.Combine(UserHomeDirectory(), ".claude", DefaultCredentialsPath)
            : ExpandPath(_auth.CredentialsFile!);

        if (File.Exists(filePath))
        {
            var parsed = ParseCredentialsFile(filePath);
            if (parsed is not null)
            {
                _log.LogInformation("Discovered Claude Code account from {Path} ({Kind})",
                    filePath, parsed.IsClaudeAiOauthSchema ? "claudeAiOauth" : "flat");
                return parsed with { ClientId = clientId, TokenEndpoint = tokenEndpoint };
            }
            _log.LogWarning("Credentials file exists at {Path} but contains no OAuth tokens", filePath);
        }
        else
        {
            _log.LogWarning("Credentials file not found at {Path}", filePath);
        }

        // 3. 网关自身状态文件兜底（此前刷新成功过的 token 状态）
        var statePath = string.IsNullOrWhiteSpace(_auth.StateFile)
            ? Path.Combine(UserHomeDirectory(), ".claude", StateFileName)
            : ExpandPath(_auth.StateFile!);
        if (File.Exists(statePath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(statePath));
                var root = doc.RootElement;
                var rt = root.TryGetProperty("refresh_token", out var r) ? r.GetString() : null;
                if (!string.IsNullOrEmpty(rt))
                {
                    _log.LogInformation("Loaded credentials from gateway state file {Path}", statePath);
                    return new ResolvedCredentials(
                        clientId, tokenEndpoint, rt, "", DateTimeOffset.MinValue,
                        root.TryGetProperty("scopes", out var s) ? s.GetString() ?? "" : "",
                        "Gateway state file", null, false);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to parse gateway state file {Path}", statePath);
            }
        }

        throw new TokenRefreshException(
            "未找到有效的 Claude Code 登录凭据！请确保本机已登录 Claude Code（~/.claude/.credentials.json 存在且含 claudeAiOauth），" +
            "或设置环境变量 CLAUDE_REFRESH_TOKEN。若此前已登录但 token 被吊销，请运行 claude auth login 重新登录。");
    }

    internal static ResolvedCredentials? ParseCredentialsFile(string filePath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(filePath));
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("claudeAiOauth", out var oauth)
                && oauth.ValueKind == JsonValueKind.Object)
            {
                var rt = oauth.TryGetProperty("refreshToken", out var r) ? r.GetString() : null;
                var at = oauth.TryGetProperty("accessToken", out var a) ? a.GetString() : null;
                long expiresAtMs = 0;
                if (oauth.TryGetProperty("expiresAt", out var e) && e.ValueKind == JsonValueKind.Number)
                {
                    expiresAtMs = e.TryGetInt64(out var v) ? v : 0;
                }
                var expiresAt = expiresAtMs > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(expiresAtMs)
                    : DateTimeOffset.MinValue;

                if (string.IsNullOrEmpty(rt)) return null;

                var scopes = oauth.TryGetProperty("scopes", out var sc)
                    ? (sc.ValueKind == JsonValueKind.String ? sc.GetString()
                        : sc.ValueKind == JsonValueKind.Array
                            ? string.Join(" ", sc.EnumerateArray().Select(x => x.GetString() ?? "")) : null)
                    : null;
                scopes = string.IsNullOrWhiteSpace(scopes)
                    ? "user:profile user:sessions:claude_code user:inference" : scopes;

                return new ResolvedCredentials(
                    DefaultClaudeClientId, DefaultTokenEndpoint, rt!, at ?? "",
                    expiresAt, scopes!, "Claude Code credentials file", filePath, true);
            }

            // 扁平结构（gateway-login secureStorage 导出等）：accessToken / refresh_token
            var rt2 = root.TryGetProperty("refresh_token", out var rr) ? rr.GetString() : null;
            var at2 = root.TryGetProperty("access_token", out var aa) ? aa.GetString() : null;
            long expMs = 0;
            if (root.TryGetProperty("expires_at", out var e2) && e2.ValueKind == JsonValueKind.Number)
            {
                expMs = e2.TryGetInt64(out var v2) ? v2 : 0;
            }
            if (root.TryGetProperty("expiresAt", out var e3) && e3.ValueKind == JsonValueKind.Number && expMs == 0)
            {
                expMs = e3.TryGetInt64(out var v3) ? v3 : 0;
            }
            if (string.IsNullOrEmpty(rt2)) return null;
            var exp = expMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(expMs) : DateTimeOffset.MinValue;
            return new ResolvedCredentials(
                DefaultClaudeClientId, DefaultTokenEndpoint, rt2!, at2 ?? "", exp,
                "user:profile user:sessions:claude_code user:inference",
                "Flat credentials file", filePath, false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static string ExpandPath(string path)
    {
        path = path.Trim();
        if (path.StartsWith('~'))
        {
            var rest = path[1..].TrimStart('/', '\\');
            return Path.Combine(UserHomeDirectory(), rest);
        }
        if (path.StartsWith("%USERPROFILE%"))
        {
            var up = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(up, path["%USERPROFILE%".Length..].TrimStart('/', '\\'));
        }
        return Path.GetFullPath(path);
    }

    internal static string UserHomeDirectory() =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "...";
}
