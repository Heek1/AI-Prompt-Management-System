using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Localization;

namespace PromptManager.Infrastructure;

/// <summary>
/// Translates messages that services build in English (errors, notices) at
/// the point where they are shown. A fixed message is looked up as a key in
/// the shared resource; a message with values in it (versions, counts, model
/// names) is matched against the templates below. The services stay in
/// English, which keeps their tests and logs readable.
/// </summary>
public static partial class MessageTranslator
{
    private static readonly (Regex Pattern, string Ukrainian)[] Templates =
    [
        (new(@"^Promoted v(\d+) in production\.( Note: its evals were worse than the previous production version\.)?$"),
            "v$1 тепер у продакшні.$2"),
        (new(@"^Rolled back to v(\d+) in production\.( Note: its evals were worse than the previous production version\.)?$"),
            "Продакшн відкочено до v$1.$2"),
        (new(@"^v(\d+) is already in production\.$"), "v$1 уже в продакшні."),
        (new(@"^Run an eval of v(\d+) before promoting it: this prompt has test cases\.$"),
            "Спершу запустіть eval для v$1: у цього промпту є тест-кейси."),
        (new(@"^v(\d+) passes (\d+)/(\d+) cases on (.+), but production v(\d+) passes (\d+)/(\d+)\. Promoting it would be a regression\.$"),
            "v$1 проходить $2/$3 кейсів на $4, а продакшн-версія v$5 — $6/$7. Випуск означав би регресію."),
        (new(@"^Version (\d+) does not exist\.$"), "Версії $1 не існує."),
        (new(@"^A comment can have at most (\d+) characters\.$"), "Коментар може містити щонайбільше $1 символів."),
        (new(@"^A prompt can have at most (\d+) cases\.$"), "У промпту може бути щонайбільше $1 кейсів."),
        (new(@"^Compare at most (\d+) models at a time\.$"), "Порівнюйте щонайбільше $1 моделі за раз."),
        (new(@"^Fill in a value for: (.+)\.$"), "Заповніть значення для: $1."),
        (new(@"^Timed out after (\d+) seconds\.$"), "Час очікування вичерпано ($1 с)."),
        (new(@"^The model call failed: (.+)$"), "Виклик моделі не вдався: $1"),
        (new(@"^(.+) costs money to run; on this instance only admins can use it\. Try the echo model\.$"),
            "$1 — платна модель; тут нею можуть користуватися лише адміністратори. Спробуйте модель echo."),
        (new(@"^(.+) costs money to run; on this instance only admins can use it\.$"),
            "$1 — платна модель; тут нею можуть користуватися лише адміністратори."),
        (new(@"^A name can have at most (\d+) characters\.$"), "Назва може містити щонайбільше $1 символів."),
        (new("^\"(.+)\" is already in the list\\.$"), "«$1» уже є в списку."),
        (new(@"^Judge: (.+)$"), "Суддя: $1"),
        (new("^Each tag can have at most (\\d+) characters: \"(.+)\" is longer\\.$"), "Тег може містити щонайбільше $1 символів: «$2» довший."),
        (new(@"^A prompt can have at most (\d+) tags\.$"), "Промпт може мати щонайбільше $1 тегів."),
        (new("^\"(.+)\" needs at least one letter or digit to be a tag\\.$"), "«$1» має містити хоча б одну літеру чи цифру, щоб бути тегом."),
        (new("^Unknown category \"(.+)\"; an admin can add it on the Lists page\\.$"), "Невідома категорія «$1»; адміністратор може додати її на сторінці «Списки»."),
        (new("^Unknown tool \"(.+)\"; an admin can add it on the Lists page\\.$"), "Невідомий інструмент «$1»; адміністратор може додати його на сторінці «Списки»."),
        (new(@"^Added v(\d+) as a draft$"), "Додано v$1 як чернетку"),
        (new(@"^(.+): not a valid (YAML|YML|JSON) file \((?:line (\d+)|(.+))\)\.$"), "$1: файл $2 з помилкою (рядок $3$4)."),
        (new(@"^(.+): not a valid zip file\.$"), "$1: це не zip-архів."),
        (new(@"^(.+): use a \.yaml, \.yml, \.json or \.zip file\.$"), "$1: підходять файли .yaml, .yml, .json або .zip."),
        (new(@"^(.+): larger than (\d+) MB\.$"), "$1: більше за $2 МБ."),
        (new(@"^Text is the same as v(\d+)$"), "Текст такий самий, як у v$1"),
    ];

    private const string WorseNote = " Note: its evals were worse than the previous production version.";

    /// <summary>The message in the interface language; unknown messages stay as they are.</summary>
    public static string Message(this IStringLocalizer t, string? english)
    {
        if (string.IsNullOrEmpty(english)) return english ?? "";

        var exact = t[english];
        if (!exact.ResourceNotFound) return exact.Value;
        if (CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "uk") return english;

        foreach (var (pattern, ukrainian) in Templates)
        {
            var match = pattern.Match(english);
            if (!match.Success) continue;
            return pattern.Replace(english, ukrainian)
                .Replace(WorseNote, " Зауважте: її evals гірші за попередню продакшн-версію.");
        }
        return english;
    }
}
