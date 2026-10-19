using System.Net.Http.Headers;

namespace ClaudeAgent.Tests;

/// <summary>
/// 网关集成测试共用的连接信息。网关若配置了鉴权（appsettings Gateway:ApiKey 或 CLAUDE_API_KEY /
/// GATEWAY_API_KEY 环境变量），测试需携带相同的 Bearer key；未配置则留空（任意请求可访问）。
/// </summary>
public static class TestGateway
{
    public const string BaseUrl = "http://127.0.0.1:5055/v1/";

    /// <summary>网关期望的 API key（取环境变量，缺省空串 = 网关未鉴权）。</summary>
    public static string ApiKey =>
        Environment.GetEnvironmentVariable("CLAUDE_API_KEY")
        ?? Environment.GetEnvironmentVariable("GATEWAY_API_KEY")
        ?? string.Empty;

    /// <summary>构造已带 Bearer 鉴权头的 HttpClient。</summary>
    public static HttpClient CreateClient(TimeSpan? timeout = null)
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri(BaseUrl),
            Timeout = timeout ?? TimeSpan.FromSeconds(120)
        };
        if (!string.IsNullOrEmpty(ApiKey))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
        return client;
    }
}
