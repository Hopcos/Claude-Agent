using System.Text.Json;

namespace ClaudeAgent.Configuration;

/// <summary>顶层网关配置（appsettings.json 的 "Gateway" 节 + 环境变量合并，环境变量优先）。</summary>
public sealed class GatewayOptions
{
    /// <summary>可选：调用方访问网关所需的 Bearer Key。为空则不鉴权。</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>请求未指定 model 时使用的默认模型。</summary>
    public string DefaultModel { get; set; } = "claude-haiku-4-5";

    /// <summary>上游 max_tokens 缺省值（Anthropic API 为必填项）。</summary>
    public int DefaultMaxTokens { get; set; } = 4096;

    /// <summary>刷新获得新 token 后是否写回 Claude Code 的凭据文件（保持 CLI 与网关共用同一账号状态）。</summary>
    public bool WriteBackToCredentialsFile { get; set; } = true;

    public AuthOptions Auth { get; set; } = new();

    public ProviderOptions Provider { get; set; } = new();
}

public sealed class AuthOptions
{
    /// <summary>凭据来源模式：Auto（默认，凭据文件优先）、File、Env。</summary>
    public string Source { get; set; } = "Auto";

    /// <summary>Claude Code 凭据文件路径（默认 ~/.claude/.credentials.json）。</summary>
    public string? CredentialsFile { get; set; }

    /// <summary>Claude Code OAuth 公开 client_id（逆向自官方 CLI，可用环境变量覆盖）。</summary>
    public string? ClientId { get; set; }

    /// <summary>OAuth token 端点（默认 https://platform.claude.com/v1/oauth/token）。</summary>
    public string? TokenEndpoint { get; set; }

    /// <summary>网关自身 token 状态文件路径（Docker/无头部署时指向可写卷；缺省 ~/.claude/claude-agent-state.json）。</summary>
    public string? StateFile { get; set; }

    /// <summary>显式指定 refresh token（用于 Linux / Docker 无头部署，优先级最高）。</summary>
    public string? RefreshToken { get; set; }
}

public sealed class ProviderOptions
{
    public string Type { get; set; } = "Anthropic";

    /// <summary>上游 API 基础地址（默认 https://api.anthropic.com）。</summary>
    public string BaseUrl { get; set; } = "https://api.anthropic.com";

    public string AnthropicVersion { get; set; } = "2023-06-01";

    /// <summary>OAuth 通道必需的 beta 头（oauth + claude-code 标识，CLI 同款）。</summary>
    public string AnthropicBeta { get; set; } = "oauth-2025-04-20,claude-code-20250219";

    public string UserAgent { get; set; } = "claude-cli/2.1.301 (external, sdk-cli)";

    /// <summary>
    /// OAuth 通道计费头（CLI 会把它作为 system 提示首块发送；缺失会被 Anthropic
    /// 归入最严格的限流桶 → 请求全部 429。实测必需）。
    /// </summary>
    public string BillingSystemPrompt { get; set; } =
        "x-anthropic-billing-header: cc_version=2.1.301.eb5; cc_entrypoint=sdk-cli;";

    /// <summary>metadata.user_id 的 account_uuid（OAuth 配额归属账号）。</summary>
    public string MetadataAccountId { get; set; } = "xxx";

    /// <summary>metadata.user_id 的 device_id（空则进程内自动生成一次；默认取本机 CLI 实测设备标识）。</summary>
    public string? MetadataDeviceId { get; set; } = "638354c6f2a200fa9a368430c7b820f49f3d9448abb041d4b0580482eac7a74c";

    /// <summary>上游 /v1/messages 查询参数（CLI 同款 "?beta=true"）。</summary>
    public string MessagesQuery { get; set; } = "beta=true";

    /// <summary>最大并发请求数。</summary>
    public int MaxConcurrency { get; set; } = 4;

    /// <summary>429/5xx/瞬态错误重试次数。</summary>
    public int MaxAttempts { get; set; } = 4;

    /// <summary>发现失败时的兜底模型列表。</summary>
    public List<string> Models { get; set; } = new();
}
