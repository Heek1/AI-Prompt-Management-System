using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PromptManager.DTOs;
using PromptManager.Evals;
using PromptManager.Llm;
using PromptManager.Models;
using PromptManager.Services;
using PromptManager.Tests.Infrastructure;

namespace PromptManager.Tests.Evals;

public sealed class EvalExecutorTests : IDisposable
{
    private readonly TestDatabase _database = new();

    public void Dispose() => _database.Dispose();

    /// <summary>
    /// "Paid" model: answers by upper-casing the prompt, or with a fixed
    /// verdict when it is asked to judge.
    /// </summary>
    private sealed class ScriptedClient : ILlmClient
    {
        public string JudgeReply { get; set; } = """{"pass": true, "reason": "fine"}""";
        public bool Fail { get; set; }
        public int Calls { get; private set; }
        public string Provider => "Scripted";
        public bool IsConfigured => true;

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            Calls++;
            if (Fail) throw new LlmException("provider down");
            var text = request.Prompt.Contains("<rubric>") ? JudgeReply : request.Prompt.ToUpperInvariant();
            return Task.FromResult(new LlmResponse(text, 1_000, 1_000, LlmOutcome.Completed, "end_turn"));
        }
    }

    private readonly ScriptedClient _client = new();
    private readonly EvalQueue _queue = new();

    private LlmCatalog Catalog() => new(Options.Create(new LlmOptions
    {
        Models =
        [
            new() { Id = "paid", Provider = "Scripted", InputPricePerMillion = 1m, OutputPricePerMillion = 1m },
            new() { Id = "echo", Provider = EchoLlmClient.ProviderName }
        ]
    }), [_client, new EchoLlmClient()]);

    private EvalService Service() => new(_database.CreateContext(), Catalog(), _queue);

    private async Task ExecuteAsync(int evalId)
    {
        var db = _database.CreateContext();
        var catalog = Catalog();
        var executor = new EvalExecutor(db,
            new PromptRunner(db, catalog, NullLogger<PromptRunner>.Instance),
            new EvalJudge(catalog), catalog,
            new VersionLifecycle(db, NullLogger<VersionLifecycle>.Instance),
            NullLogger<EvalExecutor>.Instance);
        await executor.ExecuteAsync(evalId, CancellationToken.None);
    }

    private async Task<int> SeedPromptAsync(string text = "Translate {{text}} into Ukrainian, please.")
    {
        using var db = _database.CreateContext();
        return await new PromptService(db, new ConfigurationBuilder().Build(), NullLogger<PromptService>.Instance)
            .CreateAsync(new CreatePromptDto { Title = "T", PromptText = text, Category = "Testing", AiTool = "Claude", Author = "a" });
    }

    private async Task AddCaseAsync(int promptId, string name, string text, string? mustContain = null, string? rubric = null, string? mustNotContain = null)
    {
        var (id, error) = await Service().SaveCaseAsync(new EvalCaseDto
        {
            PromptId = promptId, Name = name, Variables = new() { ["text"] = text },
            MustContain = mustContain, MustNotContain = mustNotContain, JudgeRubric = rubric
        });
        Assert.True(id is not null, error);
    }

    private async Task<int> StartAsync(int promptId, string model = "paid", string? judge = null, bool mayUsePaid = true, int? version = null)
    {
        var (id, error) = await Service().StartAsync(new StartEvalDto
        {
            PromptId = promptId, Model = model, JudgeModel = judge, MayUsePaidModels = mayUsePaid, RunBy = "tester", VersionNumber = version
        });
        Assert.True(id is not null, error);
        return id!.Value;
    }

    [Fact]
    public async Task Runs_every_case_checks_answers_and_counts_passes()
    {
        var id = await SeedPromptAsync();
        await AddCaseAsync(id, "hello", "hello", mustContain: "HELLO");
        await AddCaseAsync(id, "bye", "bye", mustContain: "nope");
        var evalId = await StartAsync(id);

        await ExecuteAsync(evalId);

        var run = (await Service().GetRunAsync(evalId))!;
        Assert.Equal(EvalRunStatus.Completed, run.Status);
        Assert.Equal((2, 2, 1), (run.TotalCases, run.CompletedCases, run.PassedCases));
        Assert.Equal(0.004m, run.CostUsd); // two answers at 2,000 tokens * $1/M
        Assert.True(run.Results[0].Passed);
        Assert.False(run.Results[1].Passed);
        Assert.Equal("not found", run.Results[1].Checks.Single().Detail);
        Assert.All(run.Results, r => Assert.NotNull(r.PromptRunId));
    }

    [Fact]
    public async Task Judge_verdict_and_cost_are_included()
    {
        var id = await SeedPromptAsync();
        await AddCaseAsync(id, "polite", "hello", rubric: "Polite.");
        _client.JudgeReply = """{"pass": false, "reason": "Rude."}""";
        var evalId = await StartAsync(id, judge: "paid");

        await ExecuteAsync(evalId);

        var run = (await Service().GetRunAsync(evalId))!;
        var check = run.Results.Single().Checks.Single();
        Assert.Equal((EvalJudge.CheckName, false, "Rude."), (check.Name, check.Passed, check.Detail));
        Assert.Equal(0.004m, run.CostUsd); // answer + judge call
    }

    [Fact]
    public async Task Provider_failure_fails_the_case_not_the_eval()
    {
        var id = await SeedPromptAsync();
        await AddCaseAsync(id, "one", "hello", mustContain: "HELLO");
        _client.Fail = true;
        var evalId = await StartAsync(id);

        await ExecuteAsync(evalId);

        var run = (await Service().GetRunAsync(evalId))!;
        Assert.Equal(EvalRunStatus.Completed, run.Status);
        Assert.Equal("provider down", run.Results.Single().Checks.Single().Detail);
        Assert.Equal(0, run.PassedCases);
    }

    [Fact]
    public async Task Executing_twice_does_nothing_the_second_time()
    {
        var id = await SeedPromptAsync();
        await AddCaseAsync(id, "one", "hello", mustContain: "HELLO");
        var evalId = await StartAsync(id);

        await ExecuteAsync(evalId);
        await ExecuteAsync(evalId);

        Assert.Equal(1, _client.Calls);
        using var db = _database.CreateContext();
        Assert.Equal(1, await db.EvalResults.CountAsync());
    }

    [Fact]
    public async Task Detects_a_regression_against_the_previous_version_on_the_same_model()
    {
        var id = await SeedPromptAsync();
        await AddCaseAsync(id, "hello", "hello", mustContain: "TRANSLATE");
        var v1 = await StartAsync(id);
        await ExecuteAsync(v1);

        using (var db = _database.CreateContext())
            await new PromptService(db, new ConfigurationBuilder().Build(), NullLogger<PromptService>.Instance)
                .CreateVersionAsync(new CreatePromptVersionDto { PromptId = id, PromptText = "Render {{text}} in Ukrainian, please.", CreatedBy = "a" });
        var v2 = await StartAsync(id);
        await ExecuteAsync(v2);

        var run = (await Service().GetRunAsync(v2))!;
        Assert.Equal(1, run.Previous!.VersionNumber);
        Assert.True(run.IsRegression);
        Assert.False((await Service().GetRunAsync(v1))!.IsRegression);
    }

    [Fact]
    public async Task Queues_the_run_for_the_worker()
    {
        var id = await SeedPromptAsync();
        await AddCaseAsync(id, "one", "hello", mustContain: "x");

        var evalId = await StartAsync(id);

        await using var e = _queue.ReadAllAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await e.MoveNextAsync());
        Assert.Equal(evalId, e.Current);
    }

    // ---------- Validation before anything runs ----------

    [Fact]
    public async Task Start_is_refused_for_invalid_requests()
    {
        var id = await SeedPromptAsync();

        Assert.Equal("Add at least one case before running an eval.", (await Service().StartAsync(new StartEvalDto { PromptId = id, Model = "paid", MayUsePaidModels = true })).Error);

        await AddCaseAsync(id, "judged", "hello", rubric: "Polite.");
        Assert.Equal("Some cases have a rubric: choose a judge model.",
            (await Service().StartAsync(new StartEvalDto { PromptId = id, Model = "echo" })).Error);
        Assert.Contains("only admins", (await Service().StartAsync(new StartEvalDto { PromptId = id, Model = "paid", JudgeModel = "echo" })).Error);
        Assert.Contains("Judge:", (await Service().StartAsync(new StartEvalDto { PromptId = id, Model = "echo", JudgeModel = "paid" })).Error);
        Assert.Contains("Unknown model", (await Service().StartAsync(new StartEvalDto { PromptId = id, Model = "nope", JudgeModel = "echo" })).Error);
        Assert.Equal("Version 9 does not exist.", (await Service().StartAsync(new StartEvalDto { PromptId = id, Model = "echo", JudgeModel = "echo", VersionNumber = 9 })).Error);

        using var db = _database.CreateContext();
        Assert.Equal(0, await db.EvalRuns.CountAsync());
    }

    [Fact]
    public async Task Case_needs_at_least_one_valid_check()
    {
        var id = await SeedPromptAsync();

        Assert.StartsWith("Add at least one check", (await Service().SaveCaseAsync(new EvalCaseDto { PromptId = id, Name = "empty" })).Error);
        Assert.StartsWith("Invalid regular expression", (await Service().SaveCaseAsync(new EvalCaseDto { PromptId = id, Name = "r", Regex = "(" })).Error);
        Assert.StartsWith("Invalid JSON Schema", (await Service().SaveCaseAsync(new EvalCaseDto { PromptId = id, Name = "s", JsonSchema = "{" })).Error);
    }

    [Fact]
    public async Task Case_keeps_only_values_for_known_variables_and_can_be_edited()
    {
        var id = await SeedPromptAsync();
        var (caseId, _) = await Service().SaveCaseAsync(new EvalCaseDto
        {
            PromptId = id, Name = "a", MustContain = "x", Variables = new() { ["text"] = "hi", ["unknown"] = "drop me" }
        });

        var saved = (await Service().GetCaseAsync(id, caseId!.Value))!;
        Assert.Equal(new[] { "text" }, saved.Variables.Keys);

        saved.Name = "renamed";
        await Service().SaveCaseAsync(saved);
        Assert.Equal("renamed", (await Service().ListCasesAsync(id)).Single().Name);
    }

    // ---------- Model comparison ----------

    [Fact]
    public async Task Comparison_queues_one_eval_per_model_and_reports_tokens_and_time()
    {
        var id = await SeedPromptAsync();
        // The echo model prefixes its answer with this sentence; the scripted one does not.
        await AddCaseAsync(id, "hello", "hello", mustContain: "HELLO", mustNotContain: "no model was called");

        var (ids, error) = await Service().StartComparisonAsync(new CompareModelsDto
        {
            PromptId = id, Models = ["paid", "echo", "paid"], MayUsePaidModels = true, RunBy = "t"
        });
        Assert.Null(error);
        Assert.Equal(2, ids!.Count); // duplicates are dropped
        foreach (var evalId in ids) await ExecuteAsync(evalId);

        var rows = await Service().CompareAsync(ids);

        Assert.Equal(new[] { "paid", "echo" }, rows.Select(r => r.Eval.Model));
        Assert.Equal((1, 0), (rows[0].Eval.PassedCases, rows[1].Eval.PassedCases)); // echo does not upper-case
        Assert.Equal((1_000, 1_000), (rows[0].InputTokens, rows[0].OutputTokens));
        Assert.NotNull(rows[0].AverageSeconds);
        Assert.Equal(0.002m, rows[0].Eval.CostUsd);
    }

    [Fact]
    public async Task Comparison_is_all_or_nothing()
    {
        var id = await SeedPromptAsync();
        await AddCaseAsync(id, "hello", "hello", mustContain: "HELLO");

        Assert.Equal("Choose at least two models to compare.",
            (await Service().StartComparisonAsync(new CompareModelsDto { PromptId = id, Models = ["echo"] })).Error);
        Assert.StartsWith("paid:", (await Service().StartComparisonAsync(new CompareModelsDto { PromptId = id, Models = ["echo", "paid"] })).Error);
        Assert.StartsWith("Compare at most",
            (await Service().StartComparisonAsync(new CompareModelsDto { PromptId = id, Models = ["a", "b", "c", "d", "e"] })).Error);

        using var db = _database.CreateContext();
        Assert.Equal(0, await db.EvalRuns.CountAsync());
    }
}
