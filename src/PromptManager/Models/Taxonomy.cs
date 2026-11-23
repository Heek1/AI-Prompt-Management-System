namespace PromptManager.Models;

public enum LookupKind
{
    Category,
    Tool
}

/// <summary>
/// One entry of an admin-managed list (categories, tools). Prompts store the
/// name itself, so removing an entry does not change prompts that use it;
/// it only stops new prompts from picking it.
/// </summary>
public class LookupValue
{
    public int Id { get; set; }
    public LookupKind Kind { get; set; }
    public string Name { get; set; } = string.Empty;
}

/// <summary>A free-form label; all prompts with one tag form a collection.</summary>
public class Tag
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>Lower-case, URL-safe form of the name; unique, so "API" and "api" are one tag.</summary>
    public string Slug { get; set; } = string.Empty;

    public ICollection<PromptTag> PromptTags { get; set; } = new List<PromptTag>();
}

public class PromptTag
{
    public int PromptId { get; set; }
    public int TagId { get; set; }

    public Prompt? Prompt { get; set; }
    public Tag? Tag { get; set; }
}
