using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using PromptManager.Infrastructure;

namespace PromptManager.DTOs;

// Who did something (author, rater, runner) comes from the signed-in account,
// never from the form: those properties are [BindNever] so a crafted POST
// cannot set them, and the controller fills them in.

public class CreatePromptDto
{
    [Required(ErrorMessage = "Title is required.")]
    [StringLength(FieldLimits.Title, ErrorMessage = "Title cannot exceed {1} characters.")]
    public string Title { get; set; } = string.Empty;

    // Optional fields are nullable: an empty textarea binds as null, and a non-nullable
    // string would get an implicit [Required] from MVC and reject the form.
    [StringLength(FieldLimits.Description, ErrorMessage = "Description cannot exceed {1} characters.")]
    public string? Description { get; set; }

    [Required(ErrorMessage = "Prompt text is required.")]
    [StringLength(FieldLimits.PromptText, MinimumLength = FieldLimits.PromptTextMin,
        ErrorMessage = "Prompt text must be between {2} and {1} characters.")]
    public string PromptText { get; set; } = string.Empty;

    [Required(ErrorMessage = "Category is required.")]
    [AllowedListValue(AllowedList.Categories)]
    public string Category { get; set; } = string.Empty;

    [Required(ErrorMessage = "AI Tool is required.")]
    [AllowedListValue(AllowedList.AiTools)]
    public string AiTool { get; set; } = string.Empty;

    [BindNever]
    [StringLength(FieldLimits.PersonName)]
    public string Author { get; set; } = string.Empty;

    [BindNever]
    public string? OwnerId { get; set; }
}

/// <summary>Edits the prompt's metadata; the text changes only through new versions.</summary>
public class EditPromptDto
{
    [Required]
    public int Id { get; set; }

    [Required(ErrorMessage = "Title is required.")]
    [StringLength(FieldLimits.Title, ErrorMessage = "Title cannot exceed {1} characters.")]
    public string Title { get; set; } = string.Empty;

    [StringLength(FieldLimits.Description, ErrorMessage = "Description cannot exceed {1} characters.")]
    public string? Description { get; set; }

    [Required(ErrorMessage = "Category is required.")]
    [AllowedListValue(AllowedList.Categories)]
    public string Category { get; set; } = string.Empty;

    [Required(ErrorMessage = "AI Tool is required.")]
    [AllowedListValue(AllowedList.AiTools)]
    public string AiTool { get; set; } = string.Empty;
}

public class PromptListDto
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string AiTool { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public double AverageRating { get; set; }
    public int CurrentVersion { get; set; }
    public bool IsArchived { get; set; }
    public string? OwnerId { get; set; }
    public int? ProductionVersion { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>The start of the current text, for the library list.</summary>
    public string Excerpt { get; set; } = string.Empty;
}

public class PromptPageDto
{
    public List<PromptListDto> Prompts { get; set; } = [];
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }

    public int PageCount => Math.Max(1, (TotalCount + PageSize - 1) / PageSize);
}

public class PromptDetailsDto
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string CurrentText { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string AiTool { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string? OwnerId { get; set; }
    public double AverageRating { get; set; }
    public int CurrentVersion { get; set; }
    public int? ProductionVersion { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsArchived { get; set; }
    public List<PromptVersionDto> Versions { get; set; } = new();
}

public class PromptVersionDto
{
    public int Id { get; set; }
    public int VersionNumber { get; set; }
    public string PromptText { get; set; } = string.Empty;
    public string ChangeNote { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public PromptManager.Models.VersionStatus Status { get; set; }
    public DateTime? PromotedAt { get; set; }
    public string? PromotedBy { get; set; }
    public bool PromotedOverRegression { get; set; }
}

public class CreatePromptVersionDto
{
    [Required]
    public int PromptId { get; set; }

    [Required(ErrorMessage = "Prompt text is required.")]
    [StringLength(FieldLimits.PromptText, MinimumLength = FieldLimits.PromptTextMin,
        ErrorMessage = "Prompt text must be between {2} and {1} characters.")]
    public string PromptText { get; set; } = string.Empty;

    [StringLength(FieldLimits.ChangeNote, ErrorMessage = "Change note cannot exceed {1} characters.")]
    public string? ChangeNote { get; set; }

    [BindNever]
    [StringLength(FieldLimits.PersonName)]
    public string CreatedBy { get; set; } = string.Empty;

    [BindNever]
    public string? CreatedById { get; set; }
}

public class RatePromptDto
{
    [Required]
    public int PromptId { get; set; }

    [Required]
    [Range(1, 5, ErrorMessage = "Rating must be between 1 and 5.")]
    public int Rating { get; set; }

    [StringLength(FieldLimits.Comment, ErrorMessage = "Comment cannot exceed {1} characters.")]
    public string? Comment { get; set; }

    /// <summary>Display name shown next to the rating.</summary>
    [BindNever]
    [StringLength(FieldLimits.PersonName)]
    public string UserName { get; set; } = string.Empty;

    /// <summary>The account; one rating per account per prompt.</summary>
    [BindNever]
    public string UserId { get; set; } = string.Empty;
}
