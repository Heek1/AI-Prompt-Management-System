using System.Text.Json;
using System.Text.RegularExpressions;
using Json.Schema;
using PromptManager.Models;

namespace PromptManager.Evals;

public sealed record CheckResult(string Name, bool Passed, string Detail);

/// <summary>The deterministic checks of a case. The judge check lives in <see cref="EvalJudge"/>.</summary>
public static class EvalChecks
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    public static IEnumerable<string> Lines(string? text) =>
        (text ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);

    public static List<CheckResult> Run(EvalCase evalCase, string output)
    {
        var results = new List<CheckResult>();

        foreach (var expected in Lines(evalCase.MustContain))
        {
            var ok = output.Contains(expected, StringComparison.OrdinalIgnoreCase);
            results.Add(new($"contains \"{expected}\"", ok, ok ? "found" : "not found"));
        }

        foreach (var forbidden in Lines(evalCase.MustNotContain))
        {
            var ok = !output.Contains(forbidden, StringComparison.OrdinalIgnoreCase);
            results.Add(new($"does not contain \"{forbidden}\"", ok, ok ? "absent" : "found"));
        }

        if (!string.IsNullOrWhiteSpace(evalCase.Regex))
            results.Add(CheckRegex(evalCase.Regex, output));

        if (!string.IsNullOrWhiteSpace(evalCase.JsonSchema))
            results.Add(CheckJsonSchema(evalCase.JsonSchema, output));

        return results;
    }

    /// <summary>Returns an error message when the regex cannot be compiled, for form validation.</summary>
    public static string? ValidateRegex(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return null;
        try
        {
            _ = new Regex(pattern, RegexOptions.None, RegexTimeout);
            return null;
        }
        catch (ArgumentException ex)
        {
            return $"Invalid regular expression: {ex.Message}";
        }
    }

    /// <summary>Returns an error message when the schema cannot be parsed, for form validation.</summary>
    public static string? ValidateSchema(string? schema)
    {
        if (string.IsNullOrWhiteSpace(schema)) return null;
        try
        {
            _ = JsonSchema.FromText(schema);
            return null;
        }
        catch (Exception ex) when (ex is JsonException or JsonSchemaException or ArgumentException)
        {
            return $"Invalid JSON Schema: {ex.Message}";
        }
    }

    private static CheckResult CheckRegex(string pattern, string output)
    {
        const string name = "matches regex";
        try
        {
            // The timeout guards the worker against catastrophic backtracking.
            var ok = Regex.IsMatch(output, pattern, RegexOptions.None, RegexTimeout);
            return new(name, ok, ok ? "matched" : "no match");
        }
        catch (RegexMatchTimeoutException)
        {
            return new(name, false, "the regular expression timed out");
        }
        catch (ArgumentException ex)
        {
            return new(name, false, $"invalid regular expression: {ex.Message}");
        }
    }

    private static CheckResult CheckJsonSchema(string schemaText, string output)
    {
        const string name = "valid JSON for the schema";

        JsonSchema schema;
        try
        {
            schema = JsonSchema.FromText(schemaText);
        }
        catch (Exception ex) when (ex is JsonException or JsonSchemaException or ArgumentException)
        {
            return new(name, false, $"invalid schema: {ex.Message}");
        }

        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(StripCodeFence(output));
        }
        catch (JsonException)
        {
            return new(name, false, "the answer is not valid JSON");
        }

        using var _ = json;
        var evaluation = schema.Evaluate(json.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (evaluation.IsValid)
            return new(name, true, "valid");

        var errors = (evaluation.Details ?? [])
            .Where(d => d.Errors is { Count: > 0 })
            .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}"))
            .Take(3);
        return new(name, false, "schema errors: " + string.Join("; ", errors));
    }

    /// <summary>Models often wrap JSON in ```json fences; accept that.</summary>
    private static string StripCodeFence(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```")) return trimmed;
        var firstNewline = trimmed.IndexOf('\n');
        var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        return firstNewline > 0 && lastFence > firstNewline
            ? trimmed[(firstNewline + 1)..lastFence].Trim()
            : trimmed;
    }
}
