namespace PromptManager.Models;

/// <summary>
/// A named set of values for a prompt's template variables, saved so the same
/// test input can be run again (on another version or model).
/// </summary>
public class PromptInputSet
{
    public int Id { get; set; }
    public int PromptId { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>JSON object of variable name to value.</summary>
    public string ValuesJson { get; set; } = "{}";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Prompt? Prompt { get; set; }
}
