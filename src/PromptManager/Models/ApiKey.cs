namespace PromptManager.Models;

/// <summary>
/// A key an application uses to read prompts through the API. Only a SHA-256
/// hash of the key is stored; the key itself is shown once, when created.
/// </summary>
public class ApiKey
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>The first characters of the key, so people can tell keys apart.</summary>
    public string Prefix { get; set; } = string.Empty;

    /// <summary>Hex SHA-256 of the full key.</summary>
    public string Hash { get; set; } = string.Empty;

    public string OwnerId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }

    public bool IsActive => RevokedAt is null;
}
