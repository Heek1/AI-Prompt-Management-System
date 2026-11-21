namespace PromptManager.Llm;

/// <summary>A single-turn completion request: one prompt in, one answer out.</summary>
public sealed record LlmRequest(string Model, string Prompt, int MaxTokens = 16_000);

public enum LlmOutcome
{
    Completed,
    /// <summary>The answer was cut off at MaxTokens.</summary>
    Truncated,
    /// <summary>The provider declined to answer (for Claude: stop_reason "refusal").</summary>
    Refused
}

public sealed record LlmResponse(
    string Text,
    long InputTokens,
    long OutputTokens,
    LlmOutcome Outcome,
    string? StopReason,
    string? Note = null,
    /// <summary>The model that actually answered, when the provider substituted one (fallbacks).</summary>
    string? ServedByModel = null);

/// <summary>
/// One implementation per provider. Clients only translate to and from the
/// provider's API; timing, cost and persistence live in <see cref="PromptRunner"/>.
/// </summary>
public interface ILlmClient
{
    /// <summary>Matches <see cref="LlmModelOptions.Provider"/>.</summary>
    string Provider { get; }

    /// <summary>False when the client cannot be used, e.g. no API key is configured.</summary>
    bool IsConfigured { get; }

    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct);
}

/// <summary>Thrown for provider failures the user should see, with a message safe to store and display.</summary>
public sealed class LlmException(string message, Exception? inner = null) : Exception(message, inner);
