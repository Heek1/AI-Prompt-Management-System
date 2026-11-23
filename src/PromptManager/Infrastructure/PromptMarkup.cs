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

    /// <summary>Start and end of a search match in a ts_headline excerpt (see PromptService).</summary>
    public const char MatchStart = '\u0001', MatchEnd = '\u0002';

    /// <summary>
    /// The text with variables highlighted, on one line or as-is. Search
    /// matches marked with <see cref="MatchStart"/>/<see cref="MatchEnd"/>
    /// become &lt;mark&gt;; unpaired markers are dropped so the HTML stays balanced.
    /// </summary>
    public static IHtmlContent Inline(string? text)
    {
        var html = Highlight(text ?? string.Empty);
        if (html.IndexOf(MatchStart) < 0 && html.IndexOf(MatchEnd) < 0)
            return new HtmlString(html);

        var sb = new StringBuilder(html.Length + 32);
        var open = false;
        foreach (var c in html)
        {
            if (c == MatchStart && !open) { sb.Append("<mark>"); open = true; }
            else if (c == MatchEnd && open) { sb.Append("</mark>"); open = false; }
            else if (c != MatchStart && c != MatchEnd) sb.Append(c);
        }
        if (open) sb.Append("</mark>");
        return new HtmlString(sb.ToString());
    }

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
