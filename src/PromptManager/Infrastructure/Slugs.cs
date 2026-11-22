using System.Globalization;
using System.Text;

namespace PromptManager.Infrastructure;

public static class Slugs
{
    public const int MaxLength = 80;

    /// <summary>
    /// "Review a diff (v2)!" -> "review-a-diff-v2". Accents are dropped, other
    /// non-ASCII letters (e.g. Cyrillic) are removed; an empty result becomes "prompt".
    /// </summary>
    public static string From(string title)
    {
        var normalized = title.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        var lastWasDash = true;
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9' or >= 'A' and <= 'Z')
            {
                sb.Append(char.ToLowerInvariant(c));
                lastWasDash = false;
            }
            else if (!lastWasDash)
            {
                sb.Append('-');
                lastWasDash = true;
            }
        }

        var slug = sb.ToString().Trim('-');
        if (slug.Length > MaxLength) slug = slug[..MaxLength].Trim('-');
        return slug.Length == 0 ? "prompt" : slug;
    }

    /// <summary>The slug, or the slug with -2, -3, ... appended until it is not taken.</summary>
    public static async Task<string> UniqueAsync(string title, Func<string, Task<bool>> isTaken)
    {
        var baseSlug = From(title);
        var candidate = baseSlug;
        for (var n = 2; await isTaken(candidate); n++)
            candidate = $"{baseSlug[..Math.Min(baseSlug.Length, MaxLength - 6)]}-{n}";
        return candidate;
    }
}
