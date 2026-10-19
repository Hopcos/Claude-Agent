using System.Security.Cryptography;
using ClaudeAgent.Configuration;

namespace ClaudeAgent.Services;

/// <summary>模型路由注册表：默认模型解析 + 别名映射（gpt-4o 等旧标识自动路由到可用 Claude 模型）。</summary>
public sealed class ProviderRegistry
{
    private readonly IAnthropicProvider _provider;
    private readonly GatewayOptions _settings;

    public ProviderRegistry(IAnthropicProvider provider, GatewayOptions settings)
    {
        _provider = provider;
        _settings = settings;
    }

    public IAnthropicProvider Provider => _provider;

    public IReadOnlyList<string> AllModels => _provider.Models;

    public string DefaultModel
    {
        get
        {
            var configured = _settings.DefaultModel;
            var models = _provider.Models;
            if (models.Count == 0) return configured;

            if (models.Contains(configured, StringComparer.OrdinalIgnoreCase))
                return configured;

            // 优先选择发现的 Haiku 模型（消耗额度最小，作为缺省最安全）
            var haiku = models.FirstOrDefault(m =>
                m.StartsWith("claude-haiku", StringComparison.OrdinalIgnoreCase));
            if (haiku is not null) return haiku;

            return models[0];
        }
    }

    /// <summary>解析请求模型：精确匹配 -> 短别名 -> 家族就近匹配 -> 默认模型。</summary>
    public (string Model, IAnthropicProvider Provider) Resolve(string? requested)
    {
        var target = string.IsNullOrWhiteSpace(requested) ? DefaultModel : requested.Trim();
        var models = _provider.Models;

        if (models.Contains(target, StringComparer.OrdinalIgnoreCase))
        {
            var found = models.First(m =>
                m.Equals(target, StringComparison.OrdinalIgnoreCase));
            return (found, _provider);
        }

        var lowered = target.ToLowerInvariant();

        // 常见旧标识 / 第三方客户端硬编码别名 -> 归一到默认模型
        var normalized = lowered switch
        {
            "gpt-4o" or "gpt-4" or "gpt-4.1" or "o3" or "o4-mini" => "haiku",
            "gpt-3.5-turbo" => "haiku",
            _ when lowered.StartsWith("gpt-") || lowered.StartsWith("o1") || lowered.StartsWith("o3") || lowered.StartsWith("o4") => "haiku",
            _ => null
        };

        // 家族就近匹配：请求 haiku 找已发现 haiku；请求 sonnet 找已发现 sonnet；以此类推
        string? familyKey = normalized ?? (lowered.Contains("haiku") ? "haiku"
            : lowered.Contains("sonnet") ? "sonnet"
            : lowered.Contains("opus") ? "opus" : null);

        if (familyKey is not null)
        {
            var exact = FindFamily(models, familyKey, target);
            if (exact is not null) return (exact, _provider);
        }

        var fallback = DefaultModel;
        if (!models.Contains(fallback, StringComparer.OrdinalIgnoreCase) && models.Count > 0)
            fallback = models[0];

        if (fallback.Equals(target, StringComparison.OrdinalIgnoreCase) || models.Count == 0)
        {
            // 未发现任何模型时允许透传原始标识，由上游给出明确错误
            return (target, _provider);
        }
        return (fallback, _provider);
    }

    /// <summary>在同一模型家族内挑选与请求版本最接近的已发现模型。</summary>
    private static string? FindFamily(IReadOnlyList<string> models, string family, string requested)
    {
        var candidates = models.Where(m => m.StartsWith($"claude-{family}", StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.Length)
            .ToList();
        if (candidates.Count == 0) return null;

        // 请求形如 claude-sonnet-4-5-20250929 时优先匹配含同版本号的候选
        var requestedParts = requested.Split('-').ToArray();
        var versionIndex = Array.IndexOf(requestedParts, family) + 1;
        var version = versionIndex > 0 && versionIndex < requestedParts.Length ? requestedParts[versionIndex] : null;

        if (version is not null)
        {
            var matching = candidates.FirstOrDefault(m =>
            {
                var parts = m.Split('-');
                return parts.Length > versionIndex && parts[versionIndex].Equals(version, StringComparison.OrdinalIgnoreCase);
            });
            if (matching is not null) return matching;
        }
        return candidates[0];
    }

    public static string NewChatId() => "chatcmpl-" + RandomNumberGenerator.GetHexString(24);
}
