namespace PromptManager.Models;

public class Prompt
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string CurrentText { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string AiTool { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;

    /// <summary>
    /// Id of the user who created the prompt; null for prompts created before
    /// sign-in existed, which only admins can manage. Not a foreign key, so
    /// deleting a user keeps their prompts.
    /// </summary>
    public string? OwnerId { get; set; }

    public int CurrentVersion { get; set; } = 1;
    public double AverageRating { get; set; } = 0;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public bool IsArchived { get; set; } = false;

    // Optimistic concurrency token. A plain column (rather than PostgreSQL's
    // xmin) that the service replaces on every change to the prompt row.
    public string ConcurrencyStamp { get; set; } = Guid.NewGuid().ToString();

    public ICollection<PromptVersion> Versions { get; set; } = new List<PromptVersion>();
    public ICollection<PromptRating> Ratings { get; set; } = new List<PromptRating>();
}

public class PromptVersion
{
    public int Id { get; set; }
    public int PromptId { get; set; }
    public int VersionNumber { get; set; }
    public string PromptText { get; set; } = string.Empty;
    public string ChangeNote { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public string? CreatedById { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Prompt? Prompt { get; set; }
}

public class PromptRating
{
    public int Id { get; set; }
    public int PromptId { get; set; }
    /// <summary>Display name at the time of rating.</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>The account that rated; one rating per account per prompt.</summary>
    public string UserId { get; set; } = string.Empty;

    public int Rating { get; set; }
    public string Comment { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Prompt? Prompt { get; set; }
}
