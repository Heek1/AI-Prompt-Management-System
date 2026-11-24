using System.Globalization;
using Microsoft.Extensions.Localization;

namespace PromptManager.Infrastructure;

public static class LocalizerExtensions
{
    /// <summary>
    /// "1 prompt" / "5 prompts" in English; in Ukrainian the resource for the
    /// key "{0} prompts" holds the three forms "{0} промпт|{0} промпти|{0} промптів".
    /// </summary>
    public static string Count(this IStringLocalizer t, long n, string one, string many)
    {
        var key = "{0} " + many;
        var found = t[key];
        if (found.ResourceNotFound)
            return string.Format(CultureInfo.InvariantCulture, n == 1 ? "{0} " + one : key, n);

        var forms = found.Value.Split('|');
        return string.Format(CultureInfo.InvariantCulture, forms.Length == 3 ? forms[UkrainianForm(n)] : forms[0], n);
    }

    /// <summary>Ukrainian plural: 1, 21, 31 → one; 2–4, 22–24 → few; the rest (and 11–14) → many.</summary>
    public static int UkrainianForm(long n)
    {
        n = Math.Abs(n);
        var lastTwo = n % 100;
        var last = n % 10;
        if (last == 1 && lastTwo != 11) return 0;
        if (last is >= 2 and <= 4 && lastTwo is < 12 or > 14) return 1;
        return 2;
    }

    /// <summary>A date in the interface language: en-GB in English, Ukrainian month names otherwise.</summary>
    public static string Date(this DateTime value, string format) =>
        value.ToString(format, CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "uk"
            ? CultureInfo.GetCultureInfo("uk-UA")
            : CultureInfo.GetCultureInfo("en-GB"));

    public static string Date(this DateOnly value, string format) => value.ToDateTime(TimeOnly.MinValue).Date(format);

    /// <summary>
    /// The translation of <paramref name="key"/>, or <paramref name="english"/>
    /// when there is none. For keys that need a prefix because resource names
    /// ignore case ("Draft" the step and "draft" the status are one name).
    /// </summary>
    public static string Text(this IStringLocalizer t, string key, string english)
    {
        var found = t[key];
        return found.ResourceNotFound ? english : found.Value;
    }
}
