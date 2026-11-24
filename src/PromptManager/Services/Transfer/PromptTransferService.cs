using System.ComponentModel.DataAnnotations;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PromptManager.Data;
using PromptManager.DTOs;
using PromptManager.Evals;
using PromptManager.Infrastructure;
using PromptManager.Models;

namespace PromptManager.Services.Transfer;

public enum ImportOutcome
{
    Created,
    NewVersion,
    Unchanged,
    Skipped
}

public sealed record ImportLine(string Title, ImportOutcome Outcome, string Detail, int? PromptId = null);

public sealed record ImportUpload(string FileName, Stream Content);

/// <summary>
/// Moves prompts in and out as YAML or JSON (C5): one file with everything, a
/// file per prompt, or a zip of per-prompt files for a folder in a git
/// repository. Importing a prompt whose slug the user already manages adds
/// the file's newest text as a draft version, so a folder can be edited in
/// git and brought back without losing the history here.
/// </summary>
public sealed class PromptTransferService(AppDbContext db, ILookupService lookups, IEvalService evals, ICurrentUser user)
{
    public const long MaxUploadBytes = 5 * 1024 * 1024;
    public const int MaxPromptsPerImport = 500;

    // ---------- Export ----------

    public async Task<List<PromptDocument>> ExportAsync(int? promptId = null)
    {
        var prompts = await db.Prompts.AsNoTracking()
            .Where(p => promptId == null || p.Id == promptId)
            .Include(p => p.Versions)
            .Include(p => p.PromptTags).ThenInclude(pt => pt.Tag)
            .AsSplitQuery()
            .OrderBy(p => p.Slug)
            .ToListAsync();
        var ids = prompts.Select(p => p.Id).ToList();
        var cases = (await db.EvalCases.AsNoTracking().Where(c => ids.Contains(c.PromptId)).OrderBy(c => c.Id).ToListAsync())
            .ToLookup(c => c.PromptId);

        return prompts.Select(p => new PromptDocument
        {
            Format = PromptFile.CurrentFormat,
            Slug = p.Slug,
            Title = p.Title,
            Description = string.IsNullOrEmpty(p.Description) ? null : p.Description,
            Category = p.Category,
            Tool = p.AiTool,
            Tags = p.PromptTags.Select(pt => pt.Tag!.Name).OrderBy(n => n).ToList(),
            Archived = p.IsArchived,
            ProductionVersion = p.ProductionVersion,
            Versions = p.Versions.OrderBy(v => v.VersionNumber).Select(v => new VersionDocument
            {
                Version = v.VersionNumber,
                Text = v.PromptText,
                ChangeNote = string.IsNullOrEmpty(v.ChangeNote) ? null : v.ChangeNote,
                CreatedBy = v.CreatedBy,
                CreatedAt = DateTime.SpecifyKind(v.CreatedAt, DateTimeKind.Utc)
            }).ToList(),
            Evals = cases[p.Id].Select(c => new EvalCaseDocument
            {
                Name = c.Name,
                Variables = JsonSerializer.Deserialize<Dictionary<string, string>>(c.VariablesJson) ?? [],
                MustContain = Lines(c.MustContain),
                MustNotContain = Lines(c.MustNotContain),
                Regex = c.Regex,
                JsonSchema = c.JsonSchema,
                JudgeRubric = c.JudgeRubric
            }).ToList()
        }).ToList();
    }

    /// <summary>A zip with prompts/{slug}.yaml per prompt, ready to commit to a repository.</summary>
    public static byte[] Zip(IEnumerable<PromptDocument> prompts)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var p in prompts)
            {
                var entry = zip.CreateEntry($"prompts/{p.Slug}.yaml", CompressionLevel.Optimal);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(PromptSerializer.Write(p, TransferFormat.Yaml));
            }
        }
        return buffer.ToArray();
    }

    // ---------- Import ----------

    /// <summary>Reads the uploads (yaml, yml, json or a zip of those) into documents, or a reason it failed.</summary>
    public static (List<PromptDocument> Prompts, List<string> Errors) Read(IEnumerable<ImportUpload> uploads)
    {
        var prompts = new List<PromptDocument>();
        var errors = new List<string>();

        void ReadText(string name, string text)
        {
            var ext = Path.GetExtension(name).ToLowerInvariant();
            if (ext is not (".yaml" or ".yml" or ".json"))
                return; // other files in a zip (README, .gitkeep) are not prompts
            try
            {
                prompts.AddRange(PromptSerializer.Read(text, ext == ".json"));
            }
            catch (Exception e) when (e is YamlDotNet.Core.YamlException or JsonException)
            {
                errors.Add($"{name}: not a valid {ext.TrimStart('.').ToUpperInvariant()} file ({FirstLine(e.Message)}).");
            }
        }

        foreach (var upload in uploads)
        {
            if (Path.GetExtension(upload.FileName).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var zip = new ZipArchive(upload.Content, ZipArchiveMode.Read);
                    foreach (var entry in zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)))
                    {
                        // The upload limit bounds the zip, not what it unpacks to.
                        if (entry.Length > MaxUploadBytes)
                        {
                            errors.Add($"{entry.FullName}: larger than {MaxUploadBytes / 1024 / 1024} MB.");
                            continue;
                        }
                        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                        ReadText(entry.FullName, reader.ReadToEnd());
                    }
                }
                catch (InvalidDataException)
                {
                    errors.Add($"{upload.FileName}: not a valid zip file.");
                }
            }
            else
            {
                using var reader = new StreamReader(upload.Content, Encoding.UTF8);
                var ext = Path.GetExtension(upload.FileName).ToLowerInvariant();
                if (ext is not (".yaml" or ".yml" or ".json"))
                    errors.Add($"{upload.FileName}: use a .yaml, .yml, .json or .zip file.");
                else
                    ReadText(upload.FileName, reader.ReadToEnd());
            }
        }

        return (prompts, errors);
    }

    public async Task<List<ImportLine>> ImportAsync(IReadOnlyList<PromptDocument> prompts)
    {
        var lines = new List<ImportLine>();
        if (prompts.Count > MaxPromptsPerImport)
            return [new ImportLine("(all)", ImportOutcome.Skipped, $"An import can hold at most {MaxPromptsPerImport} prompts; this one has {prompts.Count}.")];

        var author = await user.GetDisplayNameAsync();
        foreach (var doc in prompts)
        {
            var title = string.IsNullOrWhiteSpace(doc.Title) ? doc.Slug ?? "(untitled)" : doc.Title;
            if (await ValidateAsync(doc) is { } problem)
            {
                lines.Add(new ImportLine(title, ImportOutcome.Skipped, problem));
                continue;
            }

            var existing = string.IsNullOrWhiteSpace(doc.Slug)
                ? null
                : await db.Prompts.AsNoTracking().FirstOrDefaultAsync(p => p.Slug == doc.Slug);
            lines.Add(existing is not null && user.CanManage(existing.OwnerId)
                ? await UpdateAsync(existing, doc, author)
                : await CreateAsync(doc, author));
        }
        return lines;
    }

    /// <summary>The reason a document cannot be imported, or null.</summary>
    private async Task<string?> ValidateAsync(PromptDocument doc)
    {
        var versions = doc.EffectiveVersions();
        if (versions.Count == 0) return "No text: give \"text\" or at least one entry under \"versions\".";
        if (versions.Select(v => v.Version).Distinct().Count() != versions.Count) return "Two versions have the same number.";

        // The same rules as the form, so an import cannot store what the form would refuse.
        var dto = new CreatePromptDto
        {
            Title = doc.Title?.Trim() ?? "", Description = doc.Description, Category = doc.Category, AiTool = doc.Tool,
            PromptText = versions[^1].Text, Tags = string.Join(", ", doc.Tags)
        };
        var results = new List<ValidationResult>();
        if (!Validator.TryValidateObject(dto, new ValidationContext(dto), results, validateAllProperties: true))
            return string.Join(" ", results.Select(r => r.ErrorMessage));
        foreach (var v in versions)
        {
            if (v.Text.Length is < FieldLimits.PromptTextMin or > FieldLimits.PromptText)
                return $"Text of v{v.Version} must be between {FieldLimits.PromptTextMin} and {FieldLimits.PromptText} characters.";
            if ((v.ChangeNote?.Length ?? 0) > FieldLimits.ChangeNote)
                return $"Change note of v{v.Version} is longer than {FieldLimits.ChangeNote} characters.";
        }
        if (TagNames.Parse(dto.Tags).Error is { } tagError) return tagError;
        if (!await lookups.IsAllowedAsync(LookupKind.Category, doc.Category)) return $"Unknown category \"{doc.Category}\"; an admin can add it on the Lists page.";
        if (!await lookups.IsAllowedAsync(LookupKind.Tool, doc.Tool)) return $"Unknown tool \"{doc.Tool}\"; an admin can add it on the Lists page.";
        return null;
    }

    private async Task<ImportLine> CreateAsync(PromptDocument doc, string author)
    {
        var versions = doc.EffectiveVersions();
        var production = doc.ProductionVersion is { } wanted && versions.Any(v => v.Version == wanted)
            ? wanted
            : versions[^1].Version;
        var slugSource = string.IsNullOrWhiteSpace(doc.Slug) ? doc.Title : doc.Slug;
        var now = DateTime.UtcNow;

        var prompt = new Prompt
        {
            Title = doc.Title.Trim(),
            Slug = await Slugs.UniqueAsync(slugSource, s => db.Prompts.AnyAsync(p => p.Slug == s)),
            Description = doc.Description ?? string.Empty,
            Category = doc.Category,
            AiTool = doc.Tool,
            Author = author,
            OwnerId = user.Id,
            IsArchived = doc.Archived,
            CreatedAt = now,
            UpdatedAt = now
        };

        // Versions are renumbered 1..n in file order: the numbers in a file may
        // have gaps (hand-written, or a pruned export) and must not here.
        var number = 0;
        foreach (var v in versions)
        {
            number++;
            var isProduction = v.Version == production;
            prompt.Versions.Add(new PromptVersion
            {
                VersionNumber = number,
                PromptText = v.Text,
                ChangeNote = v.ChangeNote ?? (number == 1 ? "Initial version" : string.Empty),
                CreatedBy = string.IsNullOrWhiteSpace(v.CreatedBy) ? author : Truncate(v.CreatedBy, FieldLimits.PersonName),
                CreatedById = user.Id,
                CreatedAt = v.CreatedAt?.ToUniversalTime() ?? now,
                // Eval results are not imported, so nothing is "tested" here yet.
                Status = isProduction ? VersionStatus.Production : VersionStatus.Draft,
                PromotedAt = isProduction ? now : null,
                PromotedBy = isProduction ? author : null
            });
            if (isProduction) prompt.ProductionVersion = number;
        }
        prompt.CurrentVersion = number;
        prompt.CurrentText = versions[^1].Text;

        foreach (var tag in await TagStore.ResolveAsync(db, string.Join(", ", doc.Tags)))
            prompt.PromptTags.Add(new PromptTag { Tag = tag });

        db.Prompts.Add(prompt);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var (added, caseErrors) = await AddCasesAsync(prompt.Id, doc.Evals);
        var detail = $"{number} {(number == 1 ? "version" : "versions")}, v{prompt.ProductionVersion} in production"
                     + (added > 0 ? $", {added} eval {(added == 1 ? "case" : "cases")}" : "")
                     + (prompt.Slug != doc.Slug && !string.IsNullOrWhiteSpace(doc.Slug) ? $", slug {prompt.Slug} because {doc.Slug} belongs to someone else" : "")
                     + caseErrors;
        return new ImportLine(prompt.Title, ImportOutcome.Created, detail, prompt.Id);
    }

    private async Task<ImportLine> UpdateAsync(Prompt existing, PromptDocument doc, string author)
    {
        var latest = doc.EffectiveVersions()[^1];
        var (added, caseErrors) = await AddCasesAsync(existing.Id, doc.Evals);
        var cases = added > 0 ? $"; {added} new eval {(added == 1 ? "case" : "cases")}" : "";

        if (Normalize(latest.Text) == Normalize(existing.CurrentText))
            return new ImportLine(existing.Title, ImportOutcome.Unchanged, "Text is the same as v" + existing.CurrentVersion + cases + caseErrors, existing.Id);

        var version = existing.CurrentVersion + 1;
        db.PromptVersions.Add(new PromptVersion
        {
            PromptId = existing.Id,
            VersionNumber = version,
            PromptText = latest.Text,
            ChangeNote = Truncate(latest.ChangeNote ?? "Imported from a file", FieldLimits.ChangeNote),
            CreatedBy = author,
            CreatedById = user.Id,
            Status = VersionStatus.Draft
        });
        await db.Prompts.Where(p => p.Id == existing.Id && p.CurrentVersion == existing.CurrentVersion)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.CurrentVersion, version)
                .SetProperty(p => p.CurrentText, latest.Text)
                .SetProperty(p => p.UpdatedAt, DateTime.UtcNow)
                .SetProperty(p => p.ConcurrencyStamp, Guid.NewGuid().ToString()));
        // The unique (prompt, version) index refuses the row if someone else
        // added a version in between; the import then fails for this file
        // rather than overwriting their version.
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new ImportLine(existing.Title, ImportOutcome.NewVersion, $"Added v{version} as a draft{cases}{caseErrors}", existing.Id);
    }

    /// <summary>Adds the cases whose names the prompt does not have yet, through the usual checks.</summary>
    private async Task<(int Added, string Errors)> AddCasesAsync(int promptId, List<EvalCaseDocument> cases)
    {
        if (cases.Count == 0) return (0, "");
        var names = (await evals.ListCasesAsync(promptId)).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        var errors = new List<string>();
        foreach (var c in cases.Where(c => !string.IsNullOrWhiteSpace(c.Name) && !names.Contains(c.Name.Trim())))
        {
            var (id, error) = await evals.SaveCaseAsync(new EvalCaseDto
            {
                PromptId = promptId,
                Name = Truncate(c.Name.Trim(), 100),
                Variables = c.Variables.ToDictionary(kv => kv.Key, kv => (string?)kv.Value),
                MustContain = string.Join('\n', c.MustContain),
                MustNotContain = string.Join('\n', c.MustNotContain),
                Regex = c.Regex,
                JsonSchema = c.JsonSchema,
                JudgeRubric = c.JudgeRubric
            });
            if (id is not null) added++;
            else errors.Add($"case \"{c.Name}\": {error}");
        }
        return (added, errors.Count == 0 ? "" : ". Not imported: " + string.Join("; ", errors));
    }

    private static List<string> Lines(string text) =>
        text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();

    // Git may check a file out with CRLF and editors add a final newline;
    // neither is a change to the prompt.
    private static string Normalize(string text) => text.Replace("\r\n", "\n").TrimEnd();

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private static string FirstLine(string s) => s.Split('\n')[0].Trim();
}
