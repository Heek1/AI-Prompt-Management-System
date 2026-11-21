using System.ComponentModel.DataAnnotations;
using PromptManager.Infrastructure;
using PromptManager.Models;

namespace PromptManager.DTOs;

public class RunPromptDto
{
    [Required]
    public int PromptId { get; set; }

    /// <summary>Version to run; the current version when empty.</summary>
    public int? VersionNumber { get; set; }

    [Required(ErrorMessage = "Choose a model.")]
    [StringLength(100)]
    public string Model { get; set; } = string.Empty;

    [StringLength(FieldLimits.PersonName)]
    public string? RunBy { get; set; }

    /// <summary>Values for {{name}} placeholders, bound from form fields "Variables[name]".</summary>
    public Dictionary<string, string?> Variables { get; set; } = [];

    /// <summary>When set, the values are also saved as an input set under this name.</summary>
    [StringLength(FieldLimits.InputSetName)]
    public string? SaveInputSetAs { get; set; }
}

public class InputSetDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public Dictionary<string, string> Values { get; set; } = [];
    public DateTime UpdatedAt { get; set; }
}

public class RunListItemDto
{
    public int Id { get; set; }
    public int PromptId { get; set; }
    public string PromptTitle { get; set; } = string.Empty;
    public int VersionNumber { get; set; }
    public string Model { get; set; } = string.Empty;
    public PromptRunStatus Status { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public decimal CostUsd { get; set; }
    public long DurationMs { get; set; }
    public string RunBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public class RunDetailsDto : RunListItemDto
{
    public Dictionary<string, string> Variables { get; set; } = [];
    public string? ServedByModel { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string PromptText { get; set; } = string.Empty;
    public string Output { get; set; } = string.Empty;
    public string? StopReason { get; set; }
    public string? Note { get; set; }
    public string? Error { get; set; }
}

public class RunPageDto
{
    public List<RunListItemDto> Runs { get; set; } = [];
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public int? PromptId { get; set; }
    public string? PromptTitle { get; set; }

    public int PageCount => Math.Max(1, (TotalCount + PageSize - 1) / PageSize);
}
