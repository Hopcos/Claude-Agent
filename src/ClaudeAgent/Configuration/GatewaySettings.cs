namespace ClaudeAgent.Configuration;

/// <summary>加载并合并配置：appsettings.json 的 "Gateway" 节 &lt;- 环境变量（环境变量优先）。</summary>
public static class GatewaySettings
{
    public static GatewayOptions Load(IConfiguration config)
    {
        var opts = new GatewayOptions();
        config.GetSection("Gateway").Bind(opts);

        // 顶层环境变量
        opts.ApiKey = Env("CLAUDE_API_KEY") ?? Env("GATEWAY_API_KEY") ?? opts.ApiKey;
        opts.DefaultModel = Env("CLAUDE_DEFAULT_MODEL") ?? opts.DefaultModel;
        if (int.TryParse(Env("CLAUDE_DEFAULT_MAX_TOKENS"), out var dmt) && dmt > 0)
            opts.DefaultMaxTokens = dmt;
        if (bool.TryParse(Env("CLAUDE_WRITEBACK"), out var wb))
            opts.WriteBackToCredentialsFile = wb;

        opts.Auth ??= new AuthOptions();
        opts.Auth.Source = Env("CLAUDE_AUTH_SOURCE") ?? opts.Auth.Source;
        opts.Auth.CredentialsFile = Env("CLAUDE_CREDENTIALS_FILE") ?? opts.Auth.CredentialsFile;
        opts.Auth.ClientId = Env("CLAUDE_OAUTH_CLIENT_ID") ?? opts.Auth.ClientId;
        opts.Auth.TokenEndpoint = Env("CLAUDE_OAUTH_TOKEN_URL") ?? opts.Auth.TokenEndpoint;
        opts.Auth.RefreshToken = Env("CLAUDE_REFRESH_TOKEN") ?? opts.Auth.RefreshToken;
        opts.Auth.StateFile = Env("CLAUDE_STATE_FILE") ?? opts.Auth.StateFile;

        opts.Provider ??= new ProviderOptions();
        opts.Provider.Type = "Anthropic";
        opts.Provider.BaseUrl = Env("CLAUDE_BASE_URL") ?? opts.Provider.BaseUrl;
        opts.Provider.AnthropicVersion = Env("CLAUDE_ANTHROPIC_VERSION") ?? opts.Provider.AnthropicVersion;
        opts.Provider.AnthropicBeta = Env("CLAUDE_ANTHROPIC_BETA") ?? opts.Provider.AnthropicBeta;
        opts.Provider.UserAgent = Env("CLAUDE_USER_AGENT") ?? opts.Provider.UserAgent;
        opts.Provider.BillingSystemPrompt = Env("CLAUDE_BILLING_SYSTEM_PROMPT") ?? opts.Provider.BillingSystemPrompt;
        opts.Provider.MetadataAccountId = Env("CLAUDE_METADATA_ACCOUNT_ID") ?? opts.Provider.MetadataAccountId;
        opts.Provider.MetadataDeviceId = Env("CLAUDE_METADATA_DEVICE_ID") ?? opts.Provider.MetadataDeviceId;
        opts.Provider.MessagesQuery = Env("CLAUDE_MESSAGES_QUERY") ?? opts.Provider.MessagesQuery;
        if (int.TryParse(Env("CLAUDE_MAX_CONCURRENCY"), out var mc) && mc > 0) opts.Provider.MaxConcurrency = mc;
        if (int.TryParse(Env("CLAUDE_MAX_ATTEMPTS"), out var ma) && ma > 0) opts.Provider.MaxAttempts = ma;

        var modelsEnv = Env("CLAUDE_MODELS");
        if (!string.IsNullOrWhiteSpace(modelsEnv))
        {
            opts.Provider.Models = modelsEnv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }

        return opts;
    }

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name);
}
