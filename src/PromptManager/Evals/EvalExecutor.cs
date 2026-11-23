using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PromptManager.Data;
using PromptManager.DTOs;
using PromptManager.Llm;
using PromptManager.Models;
using PromptManager.Services;

namespace PromptManager.Evals;

/// <summary>
/// Executes one queued eval run: every case of the prompt is run on the chosen
/// version and model (as an ordinary, stored PromptRun), then checked.
/// Progress is saved after each case so the page can show it.
/// </summary>
public sealed class EvalExecutor(
    AppDbContext db,
    IPromptRunner runner,
    EvalJudge judge,
    LlmCatalog catalog,
    IVersionLifecycle lifecycle,
    ICostService costs,
    ILogger<EvalExecutor> logger)
{
    public async Task ExecuteAsync(int evalRunId, CancellationToken ct)
    {
        var eval = await db.EvalRuns.FirstOrDefaultAsync(r => r.Id == evalRunId, ct);
        if (eval is null || eval.Status != EvalRunStatus.Queued)
            return;

        var cases = await db.EvalCases.AsNoTracking()
            .Where(c => c.PromptId == eval.PromptId)
            .OrderBy(c => c.Id)
            .ToListAsync(ct);

        eval.Status = EvalRunStatus.Running;
        eval.StartedAt = DateTime.UtcNow;
        eval.TotalCases = cases.Count;
        await db.SaveChangesAsync(ct);

        try
        {
            var judgeProblem = JudgeProblem(eval);
            foreach (var evalCase in cases)
            {
                ct.ThrowIfCancellationRequested();
                var result = await RunCaseAsync(eval, evalCase, judgeProblem, ct);

                db.EvalResults.Add(result);
                eval.CompletedCases++;
                if (result.Passed) eval.PassedCases++;
                await db.SaveChangesAsync(ct);
            }

            eval.Status = EvalRunStatus.Completed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Shutdown: the worker marks interrupted runs on the next start.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Eval run {EvalRunId} failed.", evalRunId);
            eval.Status = EvalRunStatus.Failed;
            eval.Error = "The eval stopped because of an unexpected error. Partial results are kept.";
        }

        eval.FinishedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(CancellationToken.None);

        if (eval.Status == EvalRunStatus.Completed)
            await lifecycle.MarkTestedIfPassedAsync(eval.Id);
    }

    private async Task<EvalResult> RunCaseAsync(EvalRun eval, EvalCase evalCase, string? judgeProblem, CancellationToken ct)
    {
        var result = new EvalResult { EvalRunId = eval.Id, EvalCaseId = evalCase.Id, CaseName = evalCase.Name };
        var checks = new List<CheckResult>();

        var variables = JsonSerializer.Deserialize<Dictionary<string, string?>>(evalCase.VariablesJson) ?? [];
        var (runId, error) = await runner.RunAsync(new RunPromptDto
        {
            PromptId = eval.PromptId,
            VersionNumber = eval.VersionNumber,
            Model = eval.Model,
            Variables = variables,
            RunBy = eval.RunBy,
            RunByUserId = eval.RunByUserId,
            MayUsePaidModels = eval.MayUsePaidModels
        }, ct);

        if (runId is null)
        {
            checks.Add(new("answer", false, error ?? "the prompt could not be run"));
            return Finish(result, checks);
        }

        result.PromptRunId = runId;
        var run = await db.PromptRuns.AsNoTracking().SingleAsync(r => r.Id == runId, ct);
        eval.CostUsd += run.CostUsd;

        if (run.Status == PromptRunStatus.Failed)
        {
            checks.Add(new("answer", false, run.Error ?? "the call failed"));
            return Finish(result, checks);
        }

        checks.AddRange(EvalChecks.Run(evalCase, run.Output));

        if (!string.IsNullOrWhiteSpace(evalCase.JudgeRubric))
        {
            if (judgeProblem is not null)
            {
                checks.Add(new(EvalJudge.CheckName, false, judgeProblem));
            }
            else
            {
                if (await costs.BudgetProblemAsync() is { } budget && catalog.Find(eval.JudgeModel!)?.IsPaid == true)
                {
                    checks.Add(new(EvalJudge.CheckName, false, budget));
                }
                else
                {
                    var (judged, cost) = await judge.GradeAsync(eval.JudgeModel!, evalCase.JudgeRubric, run.PromptText, run.Output, ct);
                    eval.CostUsd += cost;
                    eval.JudgeCostUsd += cost;
                    checks.Add(judged);
                }
            }
        }

        // A case without checks still tells you whether the version answers at all.
        if (checks.Count == 0)
            checks.Add(new("answer", true, run.Status == PromptRunStatus.Succeeded ? "answered" : run.Status.ToString().ToLowerInvariant()));

        return Finish(result, checks);
    }

    private string? JudgeProblem(EvalRun eval)
    {
        if (string.IsNullOrWhiteSpace(eval.JudgeModel))
            return "no judge model was chosen for this eval";
        var (model, client, error) = catalog.Resolve(eval.JudgeModel);
        if (model is null || client is null)
            return $"judge unavailable: {error}";
        if (model.IsPaid && !eval.MayUsePaidModels)
            return "the judge model costs money; only admins can use it on this instance";
        return null;
    }

    private static EvalResult Finish(EvalResult result, List<CheckResult> checks)
    {
        result.Passed = checks.All(c => c.Passed);
        result.ChecksJson = JsonSerializer.Serialize(checks);
        return result;
    }
}
