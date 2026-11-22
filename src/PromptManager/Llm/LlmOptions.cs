namespace PromptManager.Llm;

/// <summary>Bound from the "Llm" configuration section.</summary>
public sealed class LlmOptions
{
    public const string Section = "Llm";

    /// <summary>Model preselected in the Run form.</summary>
    public string DefaultModel { get; set; } = "claude-opus-5-5";

    /// <summary>Upper bound for a single run; the request is cancelled after this.</summary>
    public int TimeoutSeconds { get; set; } = 180;

    public List<LlmModelOptions> Models { get; set; } = [];

    /// <summary>
    /// Who may run models that call a paid provider: "Admins" (default, so a
    /// public demo cannot spend the owner's API credit) or "Everyone".
    /// The offline echo model is always open to signed-in users.
    /// </summary>
    public string PaidModelsAccess { get; set; } = PaidAccess.Admins;
}

public static class PaidAccess
{
    public const string Admins = "Admins";
    public const string Everyone = "Everyone";
}

public sealed class LlmModelOptions
{
    /// <summary>Model id sent to the provider, e.g. "claude-opus-5-5".</summary>
    public string Id { get; set; } = "";

    public string DisplayName { get; set; } = "";

    /// <summary>Which <see cref="ILlmClient"/> serves this model, e.g. "Anthropic" or "Echo".</summary>
    public string Provider { get; set; } = "";

    /// <summary>USD per million input tokens. Prices change: keep these in sync with the provider's price list.</summary>
    public decimal InputPricePerMillion { get; set; }

    /// <summary>USD per million output tokens.</summary>
    public decimal OutputPricePerMillion { get; set; }

    /// <summary>True for every provider except the offline echo model.</summary>
    public bool IsPaid => !string.Equals(Provider, EchoLlmClient.ProviderName, StringComparison.OrdinalIgnoreCase);

    public decimal CostOf(long inputTokens, long outputTokens) =>
        (inputTokens * InputPricePerMillion + outputTokens * OutputPricePerMillion) / 1_000_000m;
}

/// <summary>Bound from the "Anthropic" configuration section. The key comes from user secrets or ANTHROPIC_API_KEY.</summary>
public sealed class AnthropicOptions
{
    public const string Section = "Anthropic";

    public string? ApiKey { get; set; }

    /// <summary>
    /// Reasoning effort: low, medium, high, xhigh or max. Set explicitly because the
    /// default differs between models (medium on Claude Opus 5.5).
    /// </summary>
    public string Effort { get; set; } = "high";

    /// <summary>
    /// Let the API re-serve a refused request with a fallback model
    /// (server-side fallbacks beta). Turn off to see raw refusals.
    /// </summary>
    public bool UseRefusalFallbacks { get; set; } = true;

    /// <summary>SDK retries for 408/409/429/5xx and connection errors.</summary>
    public int MaxRetries { get; set; } = 2;
}
