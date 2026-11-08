using System.ComponentModel.DataAnnotations;

namespace AI_Prompt_Management_System.DTOs;

public class CreatePromptDto
{
    [Required(ErrorMessage = "Title is required.")]
    [StringLength(200, ErrorMessage = "Title cannot exceed 200 characters.")]
    public string Title { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    [Required(ErrorMessage = "Prompt text is required.")]
    [MinLength(20, ErrorMessage = "Prompt text must be at least 20 characters.")]
    public string PromptText { get; set; } = string.Empty;

    [Required(ErrorMessage = "Category is required.")]
    public string Category { get; set; } = string.Empty;

    [Required(ErrorMessage = "AI Tool is required.")]
    public string AiTool { get; set; } = string.Empty;

    [Required(ErrorMessage = "Author is required.")]
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
    [MinLength(20, ErrorMessage = "Prompt text must be at least 20 characters.")]
    public string PromptText { get; set; } = string.Empty;

    public string ChangeNote { get; set; } = string.Empty;

    [Required(ErrorMessage = "Author is required.")]
    public string CreatedBy { get; set; } = string.Empty;
}

public class RatePromptDto
{
    [Required]
    public int PromptId { get; set; }

    [Required(ErrorMessage = "Username is required.")]
    public string UserName { get; set; } = string.Empty;

    [Required]
    [Range(1, 5, ErrorMessage = "Rating must be between 1 and 5.")]
    public int Rating { get; set; }

    public string Comment { get; set; } = string.Empty;
}

public class UsePromptDto
{
    public int PromptId { get; set; }
    public string UserName { get; set; } = string.Empty;
}
