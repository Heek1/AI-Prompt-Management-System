using Microsoft.EntityFrameworkCore;
using PromptManager.Data;
using PromptManager.Models;

namespace PromptManager.Services;

public sealed record PromotionResult(bool Promoted, string Message, bool BlockedByRegression = false);

public interface IVersionLifecycle
{
    /// <summary>
    /// Makes a version the production version (also used for rollbacks).
    /// When the prompt has eval cases the version needs a completed eval, and
    /// it must not pass fewer cases than production does on the same model,
    /// unless <paramref name="overrideRegression"/> is set (admins only).
    /// </summary>
    Task<PromotionResult> PromoteAsync(int promptId, int versionNumber, string promotedBy, bool overrideRegression);

    /// <summary>Marks a draft as Tested after an eval of it passed every case.</summary>
    Task MarkTestedIfPassedAsync(int evalRunId);
}

public sealed class VersionLifecycle(AppDbContext db, ILogger<VersionLifecycle> logger) : IVersionLifecycle
{
    public async Task<PromotionResult> PromoteAsync(int promptId, int versionNumber, string promotedBy, bool overrideRegression)
    {
        var prompt = await db.Prompts.Include(p => p.Versions).FirstOrDefaultAsync(p => p.Id == promptId);
        if (prompt is null) return new(false, "Prompt not found.");
        if (prompt.IsArchived) return new(false, "Archived prompts cannot be promoted.");

        var candidate = prompt.Versions.FirstOrDefault(v => v.VersionNumber == versionNumber);
        if (candidate is null) return new(false, $"Version {versionNumber} does not exist.");
        if (prompt.ProductionVersion == versionNumber) return new(false, $"v{versionNumber} is already in production.");

        var gate = await CheckGateAsync(prompt, versionNumber);
        if (gate is { Blocked: true } && !(gate.IsRegression && overrideRegression))
            return new(false, gate.Message, gate.IsRegression);

        var previous = prompt.Versions.FirstOrDefault(v => v.VersionNumber == prompt.ProductionVersion);
        if (previous is not null)
            previous.Status = VersionStatus.Tested; // it ran in production, so it was proven

        candidate.Status = VersionStatus.Production;
        candidate.PromotedAt = DateTime.UtcNow;
        candidate.PromotedBy = promotedBy;
        candidate.PromotedOverRegression = gate is { IsRegression: true };
        prompt.ProductionVersion = versionNumber;
        prompt.UpdatedAt = DateTime.UtcNow;
        prompt.ConcurrencyStamp = Guid.NewGuid().ToString();

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return new(false, "The prompt changed while you were promoting. Reload the page and try again.");
        }

        var verb = previous is not null && previous.VersionNumber > versionNumber ? "Rolled back to" : "Promoted";
        var note = gate is { IsRegression: true } ? " Note: its evals were worse than the previous production version." : "";
        logger.LogInformation("{Verb} v{Version} of prompt {PromptId} by {User}; override: {Override}.",
            verb, versionNumber, promptId, promotedBy, gate?.IsRegression == true);
        return new(true, $"{verb} v{versionNumber} in production.{note}");
    }

    public async Task MarkTestedIfPassedAsync(int evalRunId)
    {
        var eval = await db.EvalRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == evalRunId);
        if (eval is null || eval.Status != EvalRunStatus.Completed || eval.TotalCases == 0 || eval.PassedCases != eval.TotalCases)
            return;

        await db.PromptVersions
            .Where(v => v.PromptId == eval.PromptId && v.VersionNumber == eval.VersionNumber && v.Status == VersionStatus.Draft)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.Status, VersionStatus.Tested));
    }

    private sealed record Gate(bool Blocked, bool IsRegression, string Message);

    private async Task<Gate?> CheckGateAsync(Prompt prompt, int versionNumber)
    {
        // Without cases there is nothing to measure; promotion is a judgement call.
        if (!await db.EvalCases.AnyAsync(c => c.PromptId == prompt.Id))
            return null;

        var candidateEval = await LatestCompletedAsync(prompt.Id, versionNumber, model: null);
        if (candidateEval is null)
            return new(true, false, $"Run an eval of v{versionNumber} before promoting it: this prompt has test cases.");

        if (prompt.ProductionVersion is not { } production)
            return null;

        var productionEval = await LatestCompletedAsync(prompt.Id, production, candidateEval.Model);
        if (productionEval is null)
            return null; // nothing comparable on this model

        var candidateRate = Rate(candidateEval);
        var productionRate = Rate(productionEval);
        if (candidateRate >= productionRate)
            return null;

        return new(true, true,
            $"v{versionNumber} passes {candidateEval.PassedCases}/{candidateEval.TotalCases} cases on {candidateEval.Model}, " +
            $"but production v{production} passes {productionEval.PassedCases}/{productionEval.TotalCases}. Promoting it would be a regression.");
    }

    private Task<EvalRun?> LatestCompletedAsync(int promptId, int version, string? model) =>
        db.EvalRuns.AsNoTracking()
            .Where(r => r.PromptId == promptId && r.VersionNumber == version && r.Status == EvalRunStatus.Completed
                && (model == null || r.Model == model))
            .OrderByDescending(r => r.Id)
            .FirstOrDefaultAsync();

    private static double Rate(EvalRun r) => r.TotalCases == 0 ? 0 : (double)r.PassedCases / r.TotalCases;
}
