using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PromptManager.Data;
using PromptManager.DTOs;
using PromptManager.Infrastructure;
using PromptManager.Llm;
using PromptManager.Models;

namespace PromptManager.Services;

public interface IPromptRunner
{
    /// <summary>
    /// Runs a prompt version on a model and stores the run. Returns the run id,
    /// or an error when nothing was run (unknown prompt, version or model).
    /// Provider failures are not errors here: they are stored as Failed runs.
    /// </summary>
    Task<(int? RunId, string? Error)> RunAsync(RunPromptDto dto, CancellationToken ct);

    Task<RunDetailsDto?> GetRunAsync(int id);

    Task<RunPageDto> ListRunsAsync(int? promptId, int page, int pageSize = 25);
}

public sealed class PromptRunner(AppDbContext db, LlmCatalog catalog, ICostService costs, ILogger<PromptRunner> logger) : IPromptRunner
{
    public async Task<(int? RunId, string? Error)> RunAsync(RunPromptDto dto, CancellationToken ct)
    {
        var prompt = await db.Prompts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == dto.PromptId, ct);
        if (prompt is null) return (null, "Prompt not found.");
        if (prompt.IsArchived) return (null, "Archived prompts cannot be run.");

        var versionNumber = dto.VersionNumber ?? prompt.CurrentVersion;
        var version = await db.PromptVersions.AsNoTracking()
            .FirstOrDefaultAsync(v => v.PromptId == dto.PromptId && v.VersionNumber == versionNumber, ct);
        if (version is null) return (null, $"Version {versionNumber} does not exist.");

        var (model, client, modelError) = catalog.Resolve(dto.Model);
        if (model is null || client is null) return (null, modelError);
        if (model.IsPaid && !dto.MayUsePaidModels)
            return (null, $"{model.DisplayName} costs money to run; on this instance only admins can use it. Try the echo model.");
        if (model.IsPaid && await costs.BudgetProblemAsync() is { } budget)
            return (null, budget);

        var (values, variablesError) = ValuesFor(version.PromptText, dto.Variables);
        if (variablesError is not null) return (null, variablesError);

        var (text, missing) = PromptTemplate.Render(version.PromptText, values);
        if (missing.Count > 0)
            return (null, $"Fill in a value for: {string.Join(", ", missing)}.");

        var run = new PromptRun
        {
            PromptId = prompt.Id,
            VersionNumber = versionNumber,
            Model = model.Id,
            Provider = model.Provider,
            VariablesJson = values.Count > 0 ? JsonSerializer.Serialize(values) : null,
            PromptText = text,
            RunBy = string.IsNullOrWhiteSpace(dto.RunBy) ? "anonymous" : dto.RunBy.Trim(),
            RunByUserId = dto.RunByUserId
        };

        // The run gets its own deadline, but a closed browser tab (ct) also stops it.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(catalog.Timeout);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var response = await client.CompleteAsync(new LlmRequest(model.Id, run.PromptText), timeout.Token);

            run.Output = response.Text;
            run.InputTokens = response.InputTokens;
            run.OutputTokens = response.OutputTokens;
            run.StopReason = response.StopReason;
            run.Note = response.Note;
            run.ServedByModel = response.ServedByModel;
            run.Status = response.Outcome switch
            {
                LlmOutcome.Refused => PromptRunStatus.Refused,
                LlmOutcome.Truncated => PromptRunStatus.Truncated,
                _ => PromptRunStatus.Succeeded
            };

            // A fallback model may be priced differently; use its price when we know it.
            var pricedAs = response.ServedByModel is { } served ? catalog.Find(served) ?? model : model;
            run.CostUsd = pricedAs.CostOf(run.InputTokens, run.OutputTokens);
        }
        catch (LlmException ex)
        {
            logger.LogWarning(ex, "Run of prompt {PromptId} on {Model} failed.", prompt.Id, model.Id);
            run.Status = PromptRunStatus.Failed;
            run.Error = ex.Message;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            run.Status = PromptRunStatus.Failed;
            run.Error = $"Timed out after {catalog.Timeout.TotalSeconds:0} seconds.";
        }
        finally
        {
            run.DurationMs = stopwatch.ElapsedMilliseconds;
        }

        db.PromptRuns.Add(run);
        // Saved even if the user left: the call was made and may have cost money.
        await db.SaveChangesAsync(CancellationToken.None);

        if (!string.IsNullOrWhiteSpace(dto.SaveInputSetAs) && values.Count > 0)
            await SaveInputSetAsync(prompt.Id, dto.SaveInputSetAs.Trim(), values);

        return (run.Id, null);
    }

    /// <summary>
    /// Keeps only the values the version actually uses; a form may carry
    /// fields for variables that exist only in other versions.
    /// </summary>
    private static (Dictionary<string, string> Values, string? Error) ValuesFor(string template, Dictionary<string, string?> submitted)
    {
        var used = PromptTemplate.ExtractVariables(template);
        if (used.Count > PromptTemplate.MaxVariables)
            return ([], $"A prompt can have at most {PromptTemplate.MaxVariables} variables.");

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in used)
        {
            // A submitted empty field binds as null: it was provided, just empty.
            if (!submitted.TryGetValue(name, out var value)) continue;
            value ??= string.Empty;
            if (value.Length > PromptTemplate.MaxValueLength)
                return ([], $"The value for {name} is longer than {PromptTemplate.MaxValueLength} characters.");
            values[name] = value;
        }
        return (values, null);
    }

    private async Task SaveInputSetAsync(int promptId, string name, Dictionary<string, string> values)
    {
        var json = JsonSerializer.Serialize(values);
        var existing = await db.PromptInputSets.FirstOrDefaultAsync(s => s.PromptId == promptId && s.Name == name);
        if (existing is null)
        {
            db.PromptInputSets.Add(new PromptInputSet { PromptId = promptId, Name = name, ValuesJson = json });
        }
        else
        {
            existing.ValuesJson = json;
            existing.UpdatedAt = DateTime.UtcNow;
        }

        try
        {
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (DbUpdateException ex)
        {
            // Two saves under the same new name at once: the run is already
            // stored, so losing this convenience copy is acceptable.
            logger.LogWarning(ex, "Could not save input set {Name} for prompt {PromptId}.", name, promptId);
            db.ChangeTracker.Clear();
        }
    }

    public async Task<RunDetailsDto?> GetRunAsync(int id)
    {
        var row = await db.PromptRuns.AsNoTracking()
            .Where(r => r.Id == id)
            .Select(r => new { r.VariablesJson, Dto = new RunDetailsDto
            {
                Id = r.Id,
                PromptId = r.PromptId,
                PromptTitle = r.Prompt!.Title,
                VersionNumber = r.VersionNumber,
                Model = r.Model,
                ServedByModel = r.ServedByModel,
                Provider = r.Provider,
                Status = r.Status,
                InputTokens = r.InputTokens,
                OutputTokens = r.OutputTokens,
                CostUsd = r.CostUsd,
                DurationMs = r.DurationMs,
                RunBy = r.RunBy,
                CreatedAt = r.CreatedAt,
                PromptText = r.PromptText,
                Output = r.Output,
                StopReason = r.StopReason,
                Note = r.Note,
                Error = r.Error
            } })
            .FirstOrDefaultAsync();

        if (row is null) return null;
        if (row.VariablesJson is not null)
            row.Dto.Variables = JsonSerializer.Deserialize<Dictionary<string, string>>(row.VariablesJson) ?? [];
        return row.Dto;
    }

    public async Task<RunPageDto> ListRunsAsync(int? promptId, int page, int pageSize = 25)
    {
        page = Math.Max(1, page);
        var query = db.PromptRuns.AsNoTracking();
        if (promptId is { } pid)
            query = query.Where(r => r.PromptId == pid);

        var total = await query.CountAsync();
        var runs = await query
            .OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(r => new RunListItemDto
            {
                Id = r.Id,
                PromptId = r.PromptId,
                PromptTitle = r.Prompt!.Title,
                VersionNumber = r.VersionNumber,
                Model = r.ServedByModel ?? r.Model,
                Status = r.Status,
                InputTokens = r.InputTokens,
                OutputTokens = r.OutputTokens,
                CostUsd = r.CostUsd,
                DurationMs = r.DurationMs,
                RunBy = r.RunBy,
                CreatedAt = r.CreatedAt
            })
            .ToListAsync();

        string? title = null;
        if (promptId is { } id)
            title = await db.Prompts.Where(p => p.Id == id).Select(p => p.Title).FirstOrDefaultAsync();

        return new RunPageDto
        {
            Runs = runs,
            Page = page,
            PageSize = pageSize,
            TotalCount = total,
            PromptId = promptId,
            PromptTitle = title
        };
    }
}
