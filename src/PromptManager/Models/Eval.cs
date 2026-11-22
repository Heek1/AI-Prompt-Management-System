namespace PromptManager.Models;

/// <summary>
/// One test input for a prompt with the expectations its answer must meet.
/// Every non-empty expectation is a check; a case passes when all of its checks pass.
/// </summary>
public class EvalCase
{
    public int Id { get; set; }
    public int PromptId { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>Template variable values, as a JSON object.</summary>
    public string VariablesJson { get; set; } = "{}";

    /// <summary>Texts the answer must contain, one per line (case-insensitive).</summary>
    public string MustContain { get; set; } = string.Empty;

    /// <summary>Texts the answer must not contain, one per line (case-insensitive).</summary>
    public string MustNotContain { get; set; } = string.Empty;

    /// <summary>A .NET regular expression the answer must match.</summary>
    public string? Regex { get; set; }

    /// <summary>A JSON Schema the answer (parsed as JSON) must satisfy.</summary>
    public string? JsonSchema { get; set; }

    /// <summary>A rubric for a judge model, which answers pass or fail with a reason.</summary>
    public string? JudgeRubric { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Prompt? Prompt { get; set; }
}

public enum EvalRunStatus
{
    Queued,
    Running,
    Completed,
    /// <summary>The run itself broke (not a case failing); see Error.</summary>
    Failed
}

/// <summary>One version on one model against all cases of a prompt, executed in the background.</summary>
public class EvalRun
{
    public int Id { get; set; }
    public int PromptId { get; set; }
    public int VersionNumber { get; set; }
    public string Model { get; set; } = string.Empty;

    /// <summary>Model that grades rubric checks; null when no case has a rubric.</summary>
    public string? JudgeModel { get; set; }

    public EvalRunStatus Status { get; set; } = EvalRunStatus.Queued;
    public int TotalCases { get; set; }
    public int CompletedCases { get; set; }
    public int PassedCases { get; set; }

    /// <summary>Answers plus judge calls.</summary>
    public decimal CostUsd { get; set; }

    public string? Error { get; set; }
    public string RunBy { get; set; } = string.Empty;
    public string? RunByUserId { get; set; }

    /// <summary>Whether the requester may use paid models; checked again by the runner for every call.</summary>
    public bool MayUsePaidModels { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }

    public Prompt? Prompt { get; set; }
    public ICollection<EvalResult> Results { get; set; } = new List<EvalResult>();

    public double? PassRate => CompletedCases == 0 ? null : (double)PassedCases / CompletedCases;
}

public class EvalResult
{
    public int Id { get; set; }
    public int EvalRunId { get; set; }

    /// <summary>The case may be edited or deleted later; its name is kept here.</summary>
    public int? EvalCaseId { get; set; }
    public string CaseName { get; set; } = string.Empty;

    /// <summary>The run that produced the answer; null if it could not start.</summary>
    public int? PromptRunId { get; set; }

    public bool Passed { get; set; }

    /// <summary>JSON list of {name, passed, detail}.</summary>
    public string ChecksJson { get; set; } = "[]";

    public EvalRun? EvalRun { get; set; }
}
