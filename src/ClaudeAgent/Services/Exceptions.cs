using System.Net;

namespace ClaudeAgent.Services;

/// <summary>上游返回了非成功状态码（附原始错误体）。</summary>
public sealed class UpstreamException : Exception
{
    public HttpStatusCode StatusCode { get; }

    public UpstreamException(HttpStatusCode statusCode, string message)
        : base($"Anthropic upstream returned {(int)statusCode}: {message}")
    {
        StatusCode = statusCode;
    }
}

/// <summary>OAuth 刷新失败（凭据失效或端点不可达）。</summary>
public sealed class TokenRefreshException : Exception
{
    public TokenRefreshException(string message) : base(message)
    {
    }
}

/// <summary>请求的模型不存在。</summary>
public sealed class ModelNotFoundException : Exception
{
    public string Model { get; }

    public ModelNotFoundException(string model) : base($"Model '{model}' not found")
    {
        Model = model;
    }
}
