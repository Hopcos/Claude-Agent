using System.Net;
using System.Text.Json;
using ClaudeAgent.Configuration;
using ClaudeAgent.Services;
using Microsoft.Extensions.Logging;

namespace ClaudeAgent.Tests.UnitTests;

public class ClaudeAccountServiceTests
{
    private static ILogger<T> SilentLogger<T>() where T : class =>
        new LoggerFactory().CreateLogger<T>();

    private static readonly string TempDir = Path.Combine(Path.GetTempPath(), "claude-agent-tests", Guid.NewGuid().ToString("N"));

    private static string WriteCredentialsFile(string accessToken, string refreshToken, long expiresAtMs)
    {
        Directory.CreateDirectory(TempDir);
        var path = Path.Combine(TempDir, ".credentials.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["claudeAiOauth"] = new Dictionary<string, object>
            {
                ["accessToken"] = accessToken,
                ["refreshToken"] = refreshToken,
                ["expiresAt"] = expiresAtMs,
                ["refreshTokenExpiresAt"] = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeMilliseconds(),
                ["scopes"] = "user:profile user:sessions:claude_code user:inference",
                ["subscriptionType"] = "enterprise"
            }
        }));
        return path;
    }

    private sealed class FakeOAuthHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        private readonly List<string> _requests = new();
        private int _postCount;

        public FakeOAuthHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        public IReadOnlyList<string> Requests => _requests;
        public int PostCount => _postCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            _requests.Add(request.Method + " " + request.RequestUri);
            if (request.Method == HttpMethod.Post) _postCount++;
            var response = _responder(request);
            return Task.FromResult(response);
        }
    }

    private static HttpResponseMessage OAuthOk(string accessToken, string? rotatedRefreshToken = null, int expiresIn = 3600)
    {
        var payload = string.IsNullOrEmpty(rotatedRefreshToken)
            ? $$"""{"access_token":"{{accessToken}}","expires_in":{{expiresIn}},"scope":"user:profile"}"""
            : $$"""{"access_token":"{{accessToken}}","refresh_token":"{{rotatedRefreshToken}}","expires_in":{{expiresIn}},"scope":"user:profile"}""";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json")
        };
    }

    [Fact]
    public async Task Refresh_Success_NoRotation_PersistsState()
    {
        var path = WriteCredentialsFile("sk-ant-oat01-old", "sk-ant-ort01-r0",
            DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeMilliseconds()); // 已过期的 access token
        var auth = new AuthOptions { CredentialsFile = path, TokenEndpoint = "https://fake.local/oauth/token" };

        var handler = new FakeOAuthHandler(_ => OAuthOk("sk-ant-oat01-new"));
        var svc = new ClaudeAccountService(auth, writeBack: true, SilentLogger<ClaudeAccountService>(), handler);

        var token = await svc.GetTokenAsync(ct: CancellationToken.None);

        Assert.Equal("sk-ant-oat01-new", token.Value);
        Assert.True(token.ExpiresUtc > DateTimeOffset.UtcNow);
        Assert.Equal(1, handler.PostCount); // 单飞 + 有效期内不再刷新

        // 写回凭据文件（无轮换：refresh token 保持原样，access token 更新）
        var updated = JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("claudeAiOauth");
        Assert.Equal("sk-ant-oat01-new", updated.GetProperty("accessToken").GetString());
        Assert.Equal("sk-ant-ort01-r0", updated.GetProperty("refreshToken").GetString());

        // 网关状态文件与凭据文件同目录
        var statePath = Path.Combine(TempDir, ClaudeAccountService.StateFileName);
        Assert.True(File.Exists(statePath));
        var state = JsonDocument.Parse(File.ReadAllText(statePath)).RootElement;
        Assert.Equal("sk-ant-oat01-new", state.GetProperty("access_token").GetString());
        Assert.Equal("sk-ant-ort01-r0", state.GetProperty("refresh_token").GetString());
    }

    [Fact]
    public async Task Refresh_WithRotation_PersistsNewRefreshToken()
    {
        var path = WriteCredentialsFile("sk-ant-oat01-old", "sk-ant-ort01-r0",
            DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeMilliseconds());
        var auth = new AuthOptions { CredentialsFile = path, TokenEndpoint = "https://fake.local/oauth/token" };

        var handler = new FakeOAuthHandler(_ => OAuthOk("sk-ant-oat01-new", rotatedRefreshToken: "sk-ant-ort01-r1"));
        var svc = new ClaudeAccountService(auth, writeBack: true, SilentLogger<ClaudeAccountService>(), handler);

        await svc.GetTokenAsync(ct: CancellationToken.None);

        var updated = JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("claudeAiOauth");
        Assert.Equal("sk-ant-ort01-r1", updated.GetProperty("refreshToken").GetString());
        Assert.Equal("sk-ant-oat01-new", updated.GetProperty("accessToken").GetString());
    }

    [Fact]
    public async Task Refresh_InvalidGrant_ThrowsTokenRefreshException()
    {
        var path = WriteCredentialsFile("sk-ant-oat01-old", "sk-ant-ort01-dead",
            DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeMilliseconds());
        var auth = new AuthOptions { CredentialsFile = path, TokenEndpoint = "https://fake.local/oauth/token" };

        var handler = new FakeOAuthHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":\"invalid_grant\",\"error_description\":\"Refresh token not found or invalid\"}")
        });
        var svc = new ClaudeAccountService(auth, writeBack: false, SilentLogger<ClaudeAccountService>(), handler);

        await Assert.ThrowsAsync<TokenRefreshException>(() => svc.GetTokenAsync(ct: CancellationToken.None));
    }

    [Fact]
    public async Task GetToken_SingleFlight_ConcurrentCallersTriggerOneRefresh()
    {
        var path = WriteCredentialsFile("sk-ant-oat01-old", "sk-ant-ort01-r0",
            DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeMilliseconds());
        var auth = new AuthOptions { CredentialsFile = path, TokenEndpoint = "https://fake.local/oauth/token" };

        var handler = new FakeOAuthHandler(_ => OAuthOk("sk-ant-oat01-new"));
        var svc = new ClaudeAccountService(auth, writeBack: false, SilentLogger<ClaudeAccountService>(), handler);

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => svc.GetTokenAsync(ct: CancellationToken.None)));

        Assert.All(results, t => Assert.Equal("sk-ant-oat01-new", t.Value));
        Assert.Equal(1, handler.PostCount); // 并发只应触发一次上游刷新
    }

    [Fact]
    public async Task ValidInitialAccessToken_UsedWithoutRefresh()
    {
        // 凭据文件里 access token 仍有效：不应触发上游刷新
        var path = WriteCredentialsFile("sk-ant-oat01-live", "sk-ant-ort01-r0",
            DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds());
        var auth = new AuthOptions { CredentialsFile = path, TokenEndpoint = "https://fake.local/oauth/token" };

        var handler = new FakeOAuthHandler(_ => throw new InvalidOperationException("should not refresh"));
        var svc = new ClaudeAccountService(auth, writeBack: false, SilentLogger<ClaudeAccountService>(), handler);

        var token = await svc.GetTokenAsync(ct: CancellationToken.None);
        Assert.Equal("sk-ant-oat01-live", token.Value);
        Assert.Equal(0, handler.PostCount);
    }

    [Fact]
    public void ParseCredentialsFile_ClaudeAiOauthSchema()
    {
        var path = WriteCredentialsFile("sk-ant-oat01-a", "sk-ant-ort01-b",
            DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds());
        var parsed = ClaudeAccountService.ParseCredentialsFile(path);

        Assert.NotNull(parsed);
        Assert.Equal("sk-ant-ort01-b", parsed!.RefreshToken);
        Assert.Equal("sk-ant-oat01-a", parsed.AccessToken);
        Assert.True(parsed.IsClaudeAiOauthSchema);
        Assert.True(parsed.ExpiresAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public void ParseCredentialsFile_FlatSchema()
    {
        Directory.CreateDirectory(TempDir);
        var path = Path.Combine(TempDir, "flat.json");
        File.WriteAllText(path, """{"access_token":"sk-ant-oat01-f","refresh_token":"sk-ant-ort01-f","expires_at":0}""");
        var parsed = ClaudeAccountService.ParseCredentialsFile(path);

        Assert.NotNull(parsed);
        Assert.Equal("sk-ant-ort01-f", parsed!.RefreshToken);
        Assert.False(parsed.IsClaudeAiOauthSchema);
    }

    [Fact]
    public void ParseCredentialsFile_MissingFile_ReturnsNull()
    {
        Assert.Null(ClaudeAccountService.ParseCredentialsFile(Path.Combine(TempDir, "nonexistent.json")));
    }
}
