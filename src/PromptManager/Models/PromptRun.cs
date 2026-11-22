namespace PromptManager.Models;

public enum PromptRunStatus
{
    Succeeded,
    /// <summary>The answer stopped at the token limit.</summary>
    Truncated,
    /// <summary>The model declined to answer.</summary>
    Refused,
    /// <summary>The call failed (provider error, timeout); see Error.</summary>
    Failed
}

/// <summary>
/// One execution of a specific prompt version on a specific model: exactly
/// what was sent, what came back, and what it cost. Rows are never updated.
/// </summary>
public class PromptRun
{
    public int Id { get; set; }
    public int PromptId { get; set; }
    public int VersionNumber { get; set; }

    /// <summary>Model that was requested.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Set when the provider answered with a different model (refusal fallback).</summary>
    public string? ServedByModel { get; set; }

    public string Provider { get; set; } = string.Empty;

    /// <summary>The exact text sent to the model.</summary>
    public string PromptText { get; set; } = string.Empty;

    public string Output { get; set; } = string.Empty;
    public PromptRunStatus Status { get; set; }
    public string? StopReason { get; set; }

    /// <summary>Extra detail for the user, e.g. the refusal explanation.</summary>
    public string? Note { get; set; }

    public string? Error { get; set; }

    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }

    /// <summary>Computed from the configured prices at the time of the run.</summary>
    public decimal CostUsd { get; set; }

    public long DurationMs { get; set; }
    public string RunBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Prompt? Prompt { get; set; }
}
