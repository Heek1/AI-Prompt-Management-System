using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Html;

namespace PromptManager.Infrastructure;

/// <summary>
/// Renders prompt text for display: HTML-encoded, one element per line (the
/// stylesheet numbers them), and {{variables}} marked with the highlighter.
/// </summary>
public static partial class PromptMarkup
{
    // Matches the same placeholders as PromptTemplate, after HTML encoding
    // (braces and identifier characters are not changed by encoding).
    [GeneratedRegex(@"\{\{\s*([A-Za-z_][A-Za-z0-9_]*)\s*\}\}")]
    private static partial Regex Placeholder();

    /// <summary>The text with variables highlighted, on one line or as-is.</summary>
    public static IHtmlContent Inline(string? text) => new HtmlString(Highlight(text ?? string.Empty));

    /// <summary>Numbered lines for the prompt sheet.</summary>
    public static IHtmlContent Lines(string? text)
    {
        var sb = new StringBuilder();
        foreach (var line in (text ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
            sb.Append("<span class=\"pm-line\">").Append(line.Length == 0 ? "​" : Highlight(line)).Append("</span>");
        return new HtmlString(sb.ToString());
    }

    private static string Highlight(string text) =>
        Placeholder().Replace(WebUtility.HtmlEncode(text), m => $"<span class=\"pm-var\">{m.Value}</span>");
}
