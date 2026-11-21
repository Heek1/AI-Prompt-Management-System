using System.ComponentModel.DataAnnotations;
using PromptManager.Infrastructure;

namespace PromptManager.DTOs;

public class CreatePromptDto
{
    [Required(ErrorMessage = "Title is required.")]
    [StringLength(FieldLimits.Title, ErrorMessage = "Title cannot exceed {1} characters.")]
    public string Title { get; set; } = string.Empty;

    [StringLength(FieldLimits.Description, ErrorMessage = "Description cannot exceed {1} characters.")]
    // Optional fields are nullable: an empty textarea binds as null, and a non-nullable
    // string would get an implicit [Required] from MVC and reject the form.
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

    [Required(ErrorMessage = "Author is required.")]
    [StringLength(FieldLimits.PersonName, ErrorMessage = "Author cannot exceed {1} characters.")]
    public string Author { get; set; } = string.Empty;
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
}

public class PromptDetailsDto
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string CurrentText { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string AiTool { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public double AverageRating { get; set; }
    public int CurrentVersion { get; set; }
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

    [Required(ErrorMessage = "Author is required.")]
    [StringLength(FieldLimits.PersonName, ErrorMessage = "Author cannot exceed {1} characters.")]
    public string CreatedBy { get; set; } = string.Empty;
}

public class RatePromptDto
{
    [Required]
    public int PromptId { get; set; }

    [Required(ErrorMessage = "Username is required.")]
    [StringLength(FieldLimits.PersonName, ErrorMessage = "Username cannot exceed {1} characters.")]
    public string UserName { get; set; } = string.Empty;

    [Required]
    [Range(1, 5, ErrorMessage = "Rating must be between 1 and 5.")]
    public int Rating { get; set; }

    [StringLength(FieldLimits.Comment, ErrorMessage = "Comment cannot exceed {1} characters.")]
    public string? Comment { get; set; }
}

public class UsePromptDto
{
    public int PromptId { get; set; }

    [StringLength(FieldLimits.PersonName)]
    public string? UserName { get; set; }
}
