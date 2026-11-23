namespace PromptManager.Infrastructure;

/// <summary>Parses the comma-separated tag field of the prompt forms.</summary>
public static class TagNames
{
    public const int MaxTags = 8;
    public const int MaxLength = 30;

    /// <summary>
    /// Distinct tags (by slug, first spelling wins) in the order given, or an
    /// error for the form. Empty input means no tags.
    /// </summary>
    public static (List<(string Name, string Slug)> Tags, string? Error) Parse(string? input)
    {
        var tags = new List<(string Name, string Slug)>();
        if (string.IsNullOrWhiteSpace(input))
            return (tags, null);

        foreach (var raw in input.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            // Collapse inner whitespace so "code   review" and "code review" are one tag.
            var name = string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (name.Length > MaxLength)
                return (tags, $"Each tag can have at most {MaxLength} characters: \"{name}\" is longer.");

            var slug = Slug(name);
            if (slug.Length == 0)
                return (tags, $"\"{name}\" needs at least one letter or digit to be a tag.");

            if (tags.All(t => t.Slug != slug))
                tags.Add((name, slug));
        }

        return tags.Count > MaxTags
            ? (tags, $"A prompt can have at most {MaxTags} tags.")
            : (tags, null);
    }

    /// <summary>
    /// Lower-case letters and digits of any script joined by dashes: unlike
    /// prompt slugs, tags are often written in Ukrainian and must keep it.
    /// </summary>
    public static string Slug(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        var lastWasDash = true;
        foreach (var c in name.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
                lastWasDash = false;
            }
            else if (!lastWasDash)
            {
                sb.Append('-');
                lastWasDash = true;
            }
        }
        return sb.ToString().Trim('-');
    }
}
