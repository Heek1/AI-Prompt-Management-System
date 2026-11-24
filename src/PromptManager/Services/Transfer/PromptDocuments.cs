using System.Text.Json;
using System.Text.Json.Serialization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.EventEmitters;
using YamlDotNet.Serialization.NamingConventions;

namespace PromptManager.Services.Transfer;

// The file format for import and export. Field names are camelCase in both
// JSON and YAML, so one format works for both. A file holds either several
// prompts under "prompts" or a single prompt at the top level (one file per
// prompt is what a folder in a git repository uses).

public sealed class PromptFile
{
    public const string CurrentFormat = "promptmanager/v1";

    public string Format { get; set; } = CurrentFormat;
    public DateTime? ExportedAt { get; set; }
    public List<PromptDocument> Prompts { get; set; } = [];
}

public sealed class PromptDocument
{
    public string? Format { get; set; }
    public string? Slug { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Tool { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = [];
    public bool Archived { get; set; }

    /// <summary>The version applications get; null means the newest one.</summary>
    public int? ProductionVersion { get; set; }

    /// <summary>Shorthand for a hand-written file: the text of a single version.</summary>
    public string? Text { get; set; }

    public List<VersionDocument> Versions { get; set; } = [];
    public List<EvalCaseDocument> Evals { get; set; } = [];

    /// <summary>The versions, with <see cref="Text"/> as v1 when no versions are listed.</summary>
    public List<VersionDocument> EffectiveVersions() =>
        Versions.Count > 0 || string.IsNullOrWhiteSpace(Text)
            ? Versions.OrderBy(v => v.Version).ToList()
            : [new VersionDocument { Version = 1, Text = Text! }];
}

public sealed class VersionDocument
{
    public int Version { get; set; }
    public string Text { get; set; } = string.Empty;
    public string? ChangeNote { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? CreatedAt { get; set; }
}

public sealed class EvalCaseDocument
{
    public string Name { get; set; } = string.Empty;
    public Dictionary<string, string> Variables { get; set; } = [];
    public List<string> MustContain { get; set; } = [];
    public List<string> MustNotContain { get; set; } = [];
    public string? Regex { get; set; }
    public string? JsonSchema { get; set; }
    public string? JudgeRubric { get; set; }
}

public enum TransferFormat
{
    Yaml,
    Json
}

public static class PromptSerializer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly ISerializer YamlWriter = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitEmptyCollections)
        .WithEventEmitter(next => new MultilineLiteralEmitter(next))
        .Build();

    private static readonly IDeserializer YamlReader = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static string Write(PromptFile file, TransferFormat format) =>
        format == TransferFormat.Json ? JsonSerializer.Serialize(file, Json) : YamlWriter.Serialize(file);

    public static string Write(PromptDocument prompt, TransferFormat format) =>
        format == TransferFormat.Json ? JsonSerializer.Serialize(prompt, Json) : YamlWriter.Serialize(prompt);

    /// <summary>
    /// The prompts in a file of either shape. JSON is a subset of YAML, but the
    /// JSON reader gives clearer errors for JSON files, so it reads those.
    /// </summary>
    public static List<PromptDocument> Read(string content, bool isJson)
    {
        if (isJson)
        {
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("the file must hold an object: one prompt, or \"prompts\" with a list");
            return doc.RootElement.TryGetProperty("prompts", out _)
                ? JsonSerializer.Deserialize<PromptFile>(content, Json)?.Prompts ?? []
                : [JsonSerializer.Deserialize<PromptDocument>(content, Json)!];
        }

        var multi = YamlReader.Deserialize<PromptFile?>(content);
        if (multi is { Prompts.Count: > 0 }) return multi.Prompts;
        var single = YamlReader.Deserialize<PromptDocument?>(content);
        return single is null || string.IsNullOrWhiteSpace(single.Title) ? [] : [single];
    }

    /// <summary>
    /// Writes multi-line text as a YAML literal block ("|"), so a prompt reads
    /// in the file the way it reads in the app and diffs line by line in git.
    /// Strings a literal block cannot hold exactly keep the default style.
    /// </summary>
    private sealed class MultilineLiteralEmitter(IEventEmitter next) : ChainedEventEmitter(next)
    {
        public override void Emit(ScalarEventInfo eventInfo, IEmitter emitter)
        {
            if (eventInfo.Source.Value is string s && s.Contains('\n') && FitsLiteral(s))
                eventInfo.Style = ScalarStyle.Literal;
            base.Emit(eventInfo, emitter);
        }

        private static bool FitsLiteral(string s) =>
            !s.Contains('\r') && !s.Contains('\t') && !char.IsWhiteSpace(s[0])
            && s.Split('\n').All(line => line.Length == 0 || !char.IsWhiteSpace(line[^1]));
    }
}
