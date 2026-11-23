using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PromptManager.Data;
using PromptManager.Llm;

namespace PromptManager.Services;

public sealed record DailyCost(DateOnly Day, decimal CostUsd, int Runs);
public sealed record CostByName(string Name, int? PromptId, decimal CostUsd, int Runs);

public sealed class CostSummary
{
    public decimal Today { get; init; }
    public decimal Last7Days { get; init; }
    public decimal MonthToDate { get; init; }

    /// <summary>0 when no budget is set.</summary>
    public decimal MonthlyBudget { get; init; }

    public List<DailyCost> Last30Days { get; init; } = [];
    public List<CostByName> ByPrompt { get; init; } = [];
    public List<CostByName> ByModel { get; init; } = [];
    public decimal JudgeMonthToDate { get; init; }
    public int FailedRunsMonthToDate { get; init; }

    public bool HasBudget => MonthlyBudget > 0;
    public decimal Remaining => Math.Max(0, MonthlyBudget - MonthToDate);
    public double BudgetUsed => HasBudget ? (double)(MonthToDate / MonthlyBudget) : 0;
}

/// <summary>
/// Spend from stored runs plus judge calls (which are not runs). All dates
/// are UTC calendar days; "this month" is the UTC calendar month.
/// </summary>
public interface ICostService
{
    Task<decimal> MonthToDateAsync();

    /// <summary>Null when a paid call may go ahead; otherwise why not.</summary>
    Task<string?> BudgetProblemAsync();

    Task<CostSummary> SummaryAsync();
}

public sealed class CostService(AppDbContext db, IOptions<LlmOptions> options, TimeProvider clock) : ICostService
{
    private decimal Budget => options.Value.MonthlyBudgetUsd;

    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private DateTime MonthStart => new(Now.Year, Now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

    public async Task<decimal> MonthToDateAsync()
    {
        var from = MonthStart;
        var runs = await db.PromptRuns.Where(r => r.CreatedAt >= from).SumAsync(r => (decimal?)r.CostUsd) ?? 0m;
        var judges = await db.EvalRuns.Where(r => r.CreatedAt >= from).SumAsync(r => (decimal?)r.JudgeCostUsd) ?? 0m;
        return runs + judges;
    }

    public async Task<string?> BudgetProblemAsync()
    {
        if (Budget <= 0) return null;
        var spent = await MonthToDateAsync();
        return spent < Budget
            ? null
            // Invariant culture: the message may be built outside a request (eval worker).
            : string.Create(CultureInfo.InvariantCulture,
                $"This month's budget of ${Budget:0.00} for paid models is used up (${spent:0.00} spent). It resets on the 1st (UTC).");
    }

    public async Task<CostSummary> SummaryAsync()
    {
        var today = DateTime.SpecifyKind(Now.Date, DateTimeKind.Utc);
        var from30 = today.AddDays(-29);
        var month = MonthStart;
        var earliest = from30 < month ? from30 : month;

        var runs = await db.PromptRuns.AsNoTracking()
            .Where(r => r.CreatedAt >= earliest)
            .Select(r => new { r.CreatedAt, r.CostUsd, r.PromptId, Title = r.Prompt!.Title, Model = r.ServedByModel ?? r.Model, r.Status })
            .ToListAsync();
        var judges = await db.EvalRuns.AsNoTracking()
            .Where(r => r.CreatedAt >= earliest && r.JudgeCostUsd > 0)
            .Select(r => new { r.CreatedAt, r.JudgeCostUsd, r.JudgeModel })
            .ToListAsync();

        // Few rows per day for a tool like this, so grouping in memory is fine
        // and keeps decimal sums exact on every provider.
        decimal Since(DateTime from) =>
            runs.Where(r => r.CreatedAt >= from).Sum(r => r.CostUsd) + judges.Where(j => j.CreatedAt >= from).Sum(j => j.JudgeCostUsd);

        var monthRuns = runs.Where(r => r.CreatedAt >= month).ToList();
        var monthJudges = judges.Where(j => j.CreatedAt >= month).ToList();

        return new CostSummary
        {
            Today = Since(today),
            Last7Days = Since(today.AddDays(-6)),
            MonthToDate = Since(month),
            MonthlyBudget = Budget,
            JudgeMonthToDate = monthJudges.Sum(j => j.JudgeCostUsd),
            FailedRunsMonthToDate = monthRuns.Count(r => r.Status == Models.PromptRunStatus.Failed),
            Last30Days = Enumerable.Range(0, 30)
                .Select(i => from30.AddDays(i))
                .Select(day => new DailyCost(
                    DateOnly.FromDateTime(day),
                    runs.Where(r => r.CreatedAt >= day && r.CreatedAt < day.AddDays(1)).Sum(r => r.CostUsd)
                        + judges.Where(j => j.CreatedAt >= day && j.CreatedAt < day.AddDays(1)).Sum(j => j.JudgeCostUsd),
                    runs.Count(r => r.CreatedAt >= day && r.CreatedAt < day.AddDays(1))))
                .ToList(),
            ByPrompt = monthRuns
                .GroupBy(r => new { r.PromptId, r.Title })
                .Select(g => new CostByName(g.Key.Title, g.Key.PromptId, g.Sum(r => r.CostUsd), g.Count()))
                .OrderByDescending(x => x.CostUsd).ThenByDescending(x => x.Runs)
                .Take(10)
                .ToList(),
            ByModel = monthRuns
                .GroupBy(r => r.Model)
                .Select(g => new CostByName(g.Key, null, g.Sum(r => r.CostUsd), g.Count()))
                .Concat(monthJudges
                    .GroupBy(j => j.JudgeModel ?? "judge")
                    .Select(g => new CostByName($"{g.Key} (as judge)", null, g.Sum(j => j.JudgeCostUsd), g.Count())))
                .OrderByDescending(x => x.CostUsd)
                .ToList()
        };
    }
}
