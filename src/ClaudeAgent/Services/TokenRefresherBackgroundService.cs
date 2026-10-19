namespace ClaudeAgent.Services;

/// <summary>
/// 后台保活服务：
/// 1. 每 60 秒检查 Access Token 剩余有效期，不足时提前主动续期（refresh_token -> 新 access_token），
///    并将可能轮换的 refresh_token 原子化持久化，保证 Token 真正“自动永续”；
/// 2. 启动时动态刷新一次上游最新可用模型列表（/v1/models）。
/// </summary>
public sealed class TokenRefresherBackgroundService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private readonly ClaudeAccountService _account;
    private readonly IAnthropicProvider _provider;
    private readonly ILogger<TokenRefresherBackgroundService> _log;

    public TokenRefresherBackgroundService(
        ClaudeAccountService account,
        IAnthropicProvider provider,
        ILogger<TokenRefresherBackgroundService> log)
    {
        _account = account;
        _provider = provider;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("TokenRefresherBackgroundService started (check interval: {Interval}s)", Interval.TotalSeconds);

        // 启动时主动触发一次 Token 预热与模型发现
        try
        {
            await _account.GetTokenAsync(false, stoppingToken);
            await _provider.RefreshAvailableModelsAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Warmup token / model discovery encountered an issue, will retry in background");
        }

        using var timer = new PeriodicTimer(Interval);
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await _account.RefreshIfExpiringSoonAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Error occurred during proactive background token renewal check");
            }
        }

        _log.LogInformation("TokenRefresherBackgroundService stopped");
    }
}
