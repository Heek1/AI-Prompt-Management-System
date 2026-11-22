using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PromptManager.Data;
using PromptManager.DTOs;
using PromptManager.Infrastructure;
using PromptManager.Llm;
using PromptManager.Models;

namespace PromptManager.Evals;

public interface IEvalService
{
    Task<List<EvalCaseDto>> ListCasesAsync(int promptId);
    Task<EvalCaseDto?> GetCaseAsync(int promptId, int caseId);

    /// <summary>Creates (Id 0) or updates a case. Returns the case id, or an error.</summary>
    Task<(int? Id, string? Error)> SaveCaseAsync(EvalCaseDto dto);

    Task<bool> DeleteCaseAsync(int promptId, int caseId);

    /// <summary>Validates and queues an eval run. Returns its id, or an error when nothing was queued.</summary>
    Task<(int? Id, string? Error)> StartAsync(StartEvalDto dto);

    Task<List<EvalRunListItemDto>> ListRunsAsync(int promptId, int take = 20);
    Task<EvalRunDetailsDto?> GetRunAsync(int id);
}

public sealed class EvalService(AppDbContext db, LlmCatalog catalog, EvalQueue queue) : IEvalService
{
    public const int MaxCasesPerPrompt = 50;

    public async Task<List<EvalCaseDto>> ListCasesAsync(int promptId) =>
        (await db.EvalCases.AsNoTracking().Where(c => c.PromptId == promptId).OrderBy(c => c.Id).ToListAsync())
        .Select(ToDto).ToList();

    public async Task<EvalCaseDto?> GetCaseAsync(int promptId, int caseId) =>
        await db.EvalCases.AsNoTracking().FirstOrDefaultAsync(c => c.PromptId == promptId && c.Id == caseId) is { } c ? ToDto(c) : null;

    public async Task<(int? Id, string? Error)> SaveCaseAsync(EvalCaseDto dto)
    {
        if (EvalChecks.ValidateRegex(dto.Regex) is { } regexError) return (null, regexError);
        if (EvalChecks.ValidateSchema(dto.JsonSchema) is { } schemaError) return (null, schemaError);
        if (dto.CheckCount == 0) return (null, "Add at least one check: text to find or avoid, a regex, a JSON schema or a judge rubric.");

        var prompt = await db.Prompts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == dto.PromptId);
        if (prompt is null) return (null, "Prompt not found.");

        EvalCase entity;
        if (dto.Id == 0)
        {
            if (await db.EvalCases.CountAsync(c => c.PromptId == dto.PromptId) >= MaxCasesPerPrompt)
                return (null, $"A prompt can have at most {MaxCasesPerPrompt} cases.");
            entity = new EvalCase { PromptId = dto.PromptId };
            db.EvalCases.Add(entity);
        }
        else
        {
            entity = await db.EvalCases.FirstOrDefaultAsync(c => c.PromptId == dto.PromptId && c.Id == dto.Id)
                ?? throw new KeyNotFoundException();
        }

        // Keep only values for variables some version actually uses.
        var known = await db.PromptVersions.AsNoTracking()
            .Where(v => v.PromptId == dto.PromptId)
            .Select(v => v.PromptText)
            .ToListAsync();
        var names = known.SelectMany(PromptTemplate.ExtractVariables).ToHashSet(StringComparer.Ordinal);
        var values = dto.Variables
            .Where(kv => names.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value ?? string.Empty);

        entity.Name = dto.Name.Trim();
        entity.VariablesJson = JsonSerializer.Serialize(values);
        entity.MustContain = dto.MustContain?.Trim() ?? "";
        entity.MustNotContain = dto.MustNotContain?.Trim() ?? "";
        entity.Regex = Blank(dto.Regex);
        entity.JsonSchema = Blank(dto.JsonSchema);
        entity.JudgeRubric = Blank(dto.JudgeRubric);

        await db.SaveChangesAsync();
        return (entity.Id, null);
    }

    public async Task<bool> DeleteCaseAsync(int promptId, int caseId) =>
        await db.EvalCases.Where(c => c.PromptId == promptId && c.Id == caseId).ExecuteDeleteAsync() > 0;

    public async Task<(int? Id, string? Error)> StartAsync(StartEvalDto dto)
    {
        var prompt = await db.Prompts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == dto.PromptId);
        if (prompt is null) return (null, "Prompt not found.");
        if (prompt.IsArchived) return (null, "Archived prompts cannot be evaluated.");

        var version = dto.VersionNumber ?? prompt.CurrentVersion;
        if (!await db.PromptVersions.AnyAsync(v => v.PromptId == prompt.Id && v.VersionNumber == version))
            return (null, $"Version {version} does not exist.");

        var cases = await db.EvalCases.AsNoTracking().Where(c => c.PromptId == prompt.Id).ToListAsync();
        if (cases.Count == 0) return (null, "Add at least one case before running an eval.");

        if (ModelError(dto.Model, dto.MayUsePaidModels) is { } modelError) return (null, modelError);

        var needsJudge = cases.Any(c => !string.IsNullOrWhiteSpace(c.JudgeRubric));
        var judge = string.IsNullOrWhiteSpace(dto.JudgeModel) ? null : dto.JudgeModel;
        if (needsJudge && judge is null) return (null, "Some cases have a rubric: choose a judge model.");
        if (judge is not null && ModelError(judge, dto.MayUsePaidModels) is { } judgeError) return (null, $"Judge: {judgeError}");

        var eval = new EvalRun
        {
            PromptId = prompt.Id,
            VersionNumber = version,
            Model = dto.Model,
            JudgeModel = needsJudge ? judge : null,
            TotalCases = cases.Count,
            RunBy = dto.RunBy,
            RunByUserId = dto.RunByUserId,
            MayUsePaidModels = dto.MayUsePaidModels
        };
        db.EvalRuns.Add(eval);
        await db.SaveChangesAsync();

        queue.Enqueue(eval.Id);
        return (eval.Id, null);
    }

    public async Task<List<EvalRunListItemDto>> ListRunsAsync(int promptId, int take = 20) =>
        await db.EvalRuns.AsNoTracking()
            .Where(r => r.PromptId == promptId)
            .OrderByDescending(r => r.Id)
            .Take(take)
            .Select(ListItem)
            .ToListAsync();

    public async Task<EvalRunDetailsDto?> GetRunAsync(int id)
    {
        var run = await db.EvalRuns.AsNoTracking()
            .Include(r => r.Prompt)
            .Include(r => r.Results.OrderBy(x => x.Id))
            .FirstOrDefaultAsync(r => r.Id == id);
        if (run is null) return null;

        var previous = await db.EvalRuns.AsNoTracking()
            .Where(r => r.PromptId == run.PromptId && r.Model == run.Model
                && r.VersionNumber < run.VersionNumber && r.Status == EvalRunStatus.Completed)
            .OrderByDescending(r => r.VersionNumber).ThenByDescending(r => r.Id)
            .Select(ListItem)
            .FirstOrDefaultAsync();

        return new EvalRunDetailsDto
        {
            Id = run.Id,
            PromptId = run.PromptId,
            PromptTitle = run.Prompt!.Title,
            VersionNumber = run.VersionNumber,
            Model = run.Model,
            JudgeModel = run.JudgeModel,
            Status = run.Status,
            TotalCases = run.TotalCases,
            CompletedCases = run.CompletedCases,
            PassedCases = run.PassedCases,
            CostUsd = run.CostUsd,
            RunBy = run.RunBy,
            CreatedAt = run.CreatedAt,
            StartedAt = run.StartedAt,
            FinishedAt = run.FinishedAt,
            Error = run.Error,
            Previous = previous,
            Results = run.Results.Select(r => new EvalCaseResultDto
            {
                CaseName = r.CaseName,
                PromptRunId = r.PromptRunId,
                Passed = r.Passed,
                Checks = JsonSerializer.Deserialize<List<CheckResult>>(r.ChecksJson) ?? []
            }).ToList()
        };
    }

    private string? ModelError(string modelId, bool mayUsePaid)
    {
        var (model, client, error) = catalog.Resolve(modelId);
        if (model is null || client is null) return error;
        if (model.IsPaid && !mayUsePaid) return $"{model.DisplayName} costs money to run; on this instance only admins can use it.";
        return null;
    }

    private static readonly System.Linq.Expressions.Expression<Func<EvalRun, EvalRunListItemDto>> ListItem = r => new EvalRunListItemDto
    {
        Id = r.Id,
        VersionNumber = r.VersionNumber,
        Model = r.Model,
        JudgeModel = r.JudgeModel,
        Status = r.Status,
        TotalCases = r.TotalCases,
        CompletedCases = r.CompletedCases,
        PassedCases = r.PassedCases,
        CostUsd = r.CostUsd,
        RunBy = r.RunBy,
        CreatedAt = r.CreatedAt
    };

    private static EvalCaseDto ToDto(EvalCase c) => new()
    {
        Id = c.Id,
        PromptId = c.PromptId,
        Name = c.Name,
        Variables = JsonSerializer.Deserialize<Dictionary<string, string?>>(c.VariablesJson) ?? [],
        MustContain = c.MustContain,
        MustNotContain = c.MustNotContain,
        Regex = c.Regex,
        JsonSchema = c.JsonSchema,
        JudgeRubric = c.JudgeRubric
    };

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
