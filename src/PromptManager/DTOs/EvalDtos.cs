using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using PromptManager.Evals;
using PromptManager.Models;

namespace PromptManager.DTOs;

public class EvalCaseDto
{
    public int Id { get; set; }

    [Required]
    public int PromptId { get; set; }

    [Required(ErrorMessage = "Give the case a name.")]
    [StringLength(100)]
    public string Name { get; set; } = string.Empty;

    public Dictionary<string, string?> Variables { get; set; } = [];

    [StringLength(4000)]
    [Display(Name = "Must contain (one per line)")]
    public string? MustContain { get; set; }

    [StringLength(4000)]
    [Display(Name = "Must not contain (one per line)")]
    public string? MustNotContain { get; set; }

    [StringLength(1000)]
    [Display(Name = "Must match regular expression")]
    public string? Regex { get; set; }

    [StringLength(20_000)]
    [Display(Name = "Must be JSON matching this schema")]
    public string? JsonSchema { get; set; }

    [StringLength(4000)]
    [Display(Name = "Judge rubric")]
    public string? JudgeRubric { get; set; }

    public int CheckCount =>
        EvalChecks.Lines(MustContain).Count() + EvalChecks.Lines(MustNotContain).Count()
        + (string.IsNullOrWhiteSpace(Regex) ? 0 : 1)
        + (string.IsNullOrWhiteSpace(JsonSchema) ? 0 : 1)
        + (string.IsNullOrWhiteSpace(JudgeRubric) ? 0 : 1);
}

public class StartEvalDto
{
    [Required]
    public int PromptId { get; set; }

    public int? VersionNumber { get; set; }

    [Required(ErrorMessage = "Choose a model.")]
    [StringLength(100)]
    public string Model { get; set; } = string.Empty;

    [StringLength(100)]
    public string? JudgeModel { get; set; }

    [BindNever] public string RunBy { get; set; } = string.Empty;
    [BindNever] public string? RunByUserId { get; set; }
    [BindNever] public bool MayUsePaidModels { get; set; }
}

public class EvalRunListItemDto
{
    public int Id { get; set; }
    public int VersionNumber { get; set; }
    public string Model { get; set; } = string.Empty;
    public string? JudgeModel { get; set; }
    public EvalRunStatus Status { get; set; }
    public int TotalCases { get; set; }
    public int CompletedCases { get; set; }
    public int PassedCases { get; set; }
    public decimal CostUsd { get; set; }
    public string RunBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }

    public double? PassRate => CompletedCases == 0 ? null : (double)PassedCases / CompletedCases;
}

public class EvalCaseResultDto
{
    public string CaseName { get; set; } = string.Empty;
    public int? PromptRunId { get; set; }
    public bool Passed { get; set; }
    public List<CheckResult> Checks { get; set; } = [];
}

public class EvalRunDetailsDto : EvalRunListItemDto
{
    public int PromptId { get; set; }
    public string PromptTitle { get; set; } = string.Empty;
    public string? Error { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public List<EvalCaseResultDto> Results { get; set; } = [];

    /// <summary>The latest completed eval of an earlier version on the same model, for comparison.</summary>
    public EvalRunListItemDto? Previous { get; set; }

    public bool IsFinished => Status is EvalRunStatus.Completed or EvalRunStatus.Failed;

    /// <summary>True when this run passes fewer cases (by rate) than the previous version did.</summary>
    public bool IsRegression =>
        Status == EvalRunStatus.Completed && Previous?.PassRate is { } before && PassRate is { } now && now < before;
}
