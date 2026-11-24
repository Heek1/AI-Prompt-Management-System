namespace PromptManager.Models;

/// <summary>A remark on one version of a prompt (C6).</summary>
public class VersionComment
{
    public int Id { get; set; }
    public int PromptId { get; set; }
    public int VersionNumber { get; set; }
    public string Body { get; set; } = string.Empty;
    public string AuthorName { get; set; } = string.Empty;
    public string AuthorId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Prompt? Prompt { get; set; }
}

/// <summary>A user who wants to hear about new versions, promotions and comments of a prompt.</summary>
public class PromptWatch
{
    public int PromptId { get; set; }
    public string UserId { get; set; } = string.Empty;

    public Prompt? Prompt { get; set; }
}

/// <summary>An in-app notice in someone's inbox.</summary>
public class Notification
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public int PromptId { get; set; }
    public string Text { get; set; } = string.Empty;

    /// <summary>A local path in this app, e.g. /Prompts/Details/5.</summary>
    public string Url { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReadAt { get; set; }

    public Prompt? Prompt { get; set; }
}
