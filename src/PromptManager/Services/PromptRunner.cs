using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using PromptManager.Data;
using PromptManager.DTOs;
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

public sealed class PromptRunner(AppDbContext db, LlmCatalog catalog, ILogger<PromptRunner> logger) : IPromptRunner
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

        var run = new PromptRun
        {
            PromptId = prompt.Id,
            VersionNumber = versionNumber,
            Model = model.Id,
            Provider = model.Provider,
            PromptText = version.PromptText,
            RunBy = string.IsNullOrWhiteSpace(dto.RunBy) ? "anonymous" : dto.RunBy.Trim()
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
        return (run.Id, null);
    }

    public async Task<RunDetailsDto?> GetRunAsync(int id) =>
        await db.PromptRuns.AsNoTracking()
            .Where(r => r.Id == id)
            .Select(r => new RunDetailsDto
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
            })
            .FirstOrDefaultAsync();

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
