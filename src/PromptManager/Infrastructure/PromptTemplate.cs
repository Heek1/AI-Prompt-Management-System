using System.Text.RegularExpressions;

namespace PromptManager.Infrastructure;

/// <summary>
/// Prompt templates with <c>{{name}}</c> placeholders. Names are letters,
/// digits and underscores, starting with a letter or underscore; spaces
/// inside the braces are allowed. Anything else in braces is left as text,
/// so prompts that contain JSON or code are not mangled.
/// </summary>
public static partial class PromptTemplate
{
    public const int MaxVariables = 50;
    public const int MaxValueLength = 20_000;

    [GeneratedRegex(@"\{\{\s*([A-Za-z_][A-Za-z0-9_]*)\s*\}\}")]
    private static partial Regex Placeholder();

    /// <summary>Distinct variable names in order of first appearance.</summary>
    public static IReadOnlyList<string> ExtractVariables(string text) =>
        Placeholder().Matches(text)
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Replaces every placeholder with its value. Values are inserted as-is
    /// (a value containing "{{x}}" is not expanded again). Returns the names
    /// that had no value; the text is only usable when that list is empty.
    /// </summary>
    public static (string Text, IReadOnlyList<string> Missing) Render(string text, IReadOnlyDictionary<string, string> values)
    {
        var missing = new List<string>();
        var rendered = Placeholder().Replace(text, m =>
        {
            var name = m.Groups[1].Value;
            if (values.TryGetValue(name, out var value))
                return value;
            if (!missing.Contains(name))
                missing.Add(name);
            return m.Value;
        });
        return (rendered, missing);
    }
}
