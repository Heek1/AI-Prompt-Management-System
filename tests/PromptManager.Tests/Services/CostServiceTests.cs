using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PromptManager.DTOs;
using PromptManager.Llm;
using PromptManager.Models;
using PromptManager.Services;
using PromptManager.Tests.Infrastructure;

namespace PromptManager.Tests.Services;

public sealed class CostServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    private readonly TestDatabase _database = new();

    public void Dispose() => _database.Dispose();

    private async Task<int> SeedPromptAsync(string title = "T")
    {
        using var db = _database.CreateContext();
        return await new PromptService(db, NullLogger<PromptService>.Instance).CreateAsync(new CreatePromptDto
        {
            Title = title, PromptText = "Prompt text that is long enough.", Category = "Testing", AiTool = "Claude", Author = "a"
        });
    }

    private async Task AddRunAsync(int promptId, DateTime at, decimal cost, string model = "m", PromptRunStatus status = PromptRunStatus.Succeeded)
    {
        using var db = _database.CreateContext();
        db.PromptRuns.Add(new PromptRun
        {
            PromptId = promptId, VersionNumber = 1, Model = model, Provider = "x", CostUsd = cost, CreatedAt = at, Status = status
        });
        await db.SaveChangesAsync();
    }

    private async Task AddJudgeAsync(int promptId, DateTime at, decimal judgeCost)
    {
        using var db = _database.CreateContext();
        db.EvalRuns.Add(new EvalRun
        {
            PromptId = promptId, VersionNumber = 1, Model = "m", JudgeModel = "judge-m", Status = EvalRunStatus.Completed,
            CostUsd = judgeCost, JudgeCostUsd = judgeCost, CreatedAt = at, RunBy = "t"
        });
        await db.SaveChangesAsync();
    }

    private static DateTime At(int month, int day, int hour = 10) => new(2026, month, day, hour, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Month_to_date_counts_runs_and_judges_of_this_month_only()
    {
        var id = await SeedPromptAsync();
        await AddRunAsync(id, At(11, 1), 1.00m);
        await AddRunAsync(id, At(11, 20), 0.50m);
        await AddRunAsync(id, At(10, 31), 9.00m);   // last month
        await AddJudgeAsync(id, At(11, 5), 0.25m);

        Assert.Equal(1.75m, await TestCosts.For(_database, now: Now).MonthToDateAsync());
    }

    [Fact]
    public async Task Budget_problem_only_when_the_budget_is_used_up()
    {
        var id = await SeedPromptAsync();
        await AddRunAsync(id, At(11, 2), 4.99m);

        Assert.Null(await TestCosts.For(_database, monthlyBudget: 0, now: Now).BudgetProblemAsync());
        Assert.Null(await TestCosts.For(_database, monthlyBudget: 5, now: Now).BudgetProblemAsync());

        await AddRunAsync(id, At(11, 3), 0.01m);
        Assert.StartsWith("This month's budget of $5.00", await TestCosts.For(_database, monthlyBudget: 5, now: Now).BudgetProblemAsync());
    }

    [Fact]
    public async Task Summary_groups_by_day_prompt_and_model()
    {
        var a = await SeedPromptAsync("Alpha");
        var b = await SeedPromptAsync("Beta");
        await AddRunAsync(a, At(11, 20, 1), 1.00m, model: "opus");
        await AddRunAsync(a, At(11, 20, 2), 1.00m, model: "opus");
        await AddRunAsync(b, At(11, 19), 0.30m, model: "haiku");
        await AddRunAsync(b, At(11, 18), 0m, model: "haiku", status: PromptRunStatus.Failed);
        await AddJudgeAsync(a, At(11, 20), 0.20m);

        var s = await TestCosts.For(_database, monthlyBudget: 10, now: Now).SummaryAsync();

        Assert.Equal(2.20m, s.Today);
        Assert.Equal(2.50m, s.MonthToDate);
        Assert.Equal(0.20m, s.JudgeMonthToDate);
        Assert.Equal(1, s.FailedRunsMonthToDate);
        Assert.Equal(7.50m, s.Remaining);
        Assert.Equal(30, s.Last30Days.Count);
        Assert.Equal(new DailyCost(new DateOnly(2026, 11, 20), 2.20m, 2), s.Last30Days[^1]);
        Assert.Equal(new[] { "Alpha", "Beta" }, s.ByPrompt.Select(p => p.Name));
        Assert.Equal(new[] { ("opus", 2.00m), ("haiku", 0.30m), ("judge-m (as judge)", 0.20m) },
            s.ByModel.Select(m => (m.Name, m.CostUsd)));
    }

    [Fact]
    public async Task Runner_refuses_paid_models_once_the_budget_is_used_up_but_not_echo()
    {
        var id = await SeedPromptAsync();
        await AddRunAsync(id, DateTime.UtcNow, 5m);
        using var db = _database.CreateContext();
        var catalog = new LlmCatalog(Options.Create(new LlmOptions
        {
            Models = [new() { Id = "paid", Provider = "Anthropic" }, new() { Id = "echo", Provider = EchoLlmClient.ProviderName }]
        }), [new EchoLlmClient(), new AlwaysConfigured("Anthropic")]);
        var runner = new PromptRunner(db, catalog, TestCosts.For(_database, monthlyBudget: 5), NullLogger<PromptRunner>.Instance);

        var paid = await runner.RunAsync(new RunPromptDto { PromptId = id, Model = "paid" }, CancellationToken.None);
        var echo = await runner.RunAsync(new RunPromptDto { PromptId = id, Model = "echo" }, CancellationToken.None);

        Assert.Null(paid.RunId);
        Assert.Contains("budget", paid.Error);
        Assert.NotNull(echo.RunId);
    }

    private sealed class AlwaysConfigured(string provider) : ILlmClient
    {
        public string Provider => provider;
        public bool IsConfigured => true;
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct) =>
            Task.FromResult(new LlmResponse("paid answer", 1, 1, LlmOutcome.Completed, "end_turn"));
    }
}
