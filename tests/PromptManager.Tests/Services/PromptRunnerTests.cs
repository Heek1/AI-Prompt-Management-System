using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PromptManager.Data;
using PromptManager.DTOs;
using PromptManager.Llm;
using PromptManager.Models;
using PromptManager.Services;
using PromptManager.Tests.Infrastructure;

namespace PromptManager.Tests.Services;

public sealed class PromptRunnerTests : IDisposable
{
    private readonly TestDatabase _database = new();

    public void Dispose() => _database.Dispose();

    /// <summary>A client whose behaviour each test scripts, recording what it was asked.</summary>
    private sealed class ScriptedClient(Func<LlmRequest, CancellationToken, Task<LlmResponse>> behaviour) : ILlmClient
    {
        public List<LlmRequest> Requests { get; } = [];
        public string Provider => "Scripted";
        public bool IsConfigured => true;

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return behaviour(request, ct);
        }
    }

    private static ScriptedClient Answering(string text, LlmOutcome outcome = LlmOutcome.Completed, string? servedBy = null, string? note = null) =>
        new((_, _) => Task.FromResult(new LlmResponse(text, 1_000, 500, outcome, "end_turn", note, servedBy)));

    private PromptRunner Runner(ILlmClient client, int timeoutSeconds = 30) =>
        new(_database.CreateContext(),
            new LlmCatalog(Options.Create(new LlmOptions
            {
                TimeoutSeconds = timeoutSeconds,
                Models =
                [
                    new() { Id = "model-a", Provider = "Scripted", InputPricePerMillion = 4m, OutputPricePerMillion = 20m },
                    new() { Id = "model-b", Provider = "Scripted", InputPricePerMillion = 1m, OutputPricePerMillion = 2m },
                    new() { Id = "offline", Provider = "Nobody" }
                ]
            }), [client]),
            NullLogger<PromptRunner>.Instance);

    private async Task<int> SeedPromptAsync(bool withSecondVersion = false)
    {
        using var db = _database.CreateContext();
        var service = new PromptService(db, NullLogger<PromptService>.Instance);
        var id = await service.CreateAsync(new CreatePromptDto
        {
            Title = "Runnable", PromptText = "Version one text of the prompt.", Category = "Testing", AiTool = "Claude", Author = "alice"
        });
        if (withSecondVersion)
            await service.CreateVersionAsync(new CreatePromptVersionDto { PromptId = id, PromptText = "Version two text of the prompt.", CreatedBy = "bob" });
        return id;
    }

    private async Task<PromptRun> StoredRunAsync(int runId)
    {
        using var db = _database.CreateContext();
        return await db.PromptRuns.SingleAsync(r => r.Id == runId);
    }

    [Fact]
    public async Task Successful_run_stores_output_tokens_cost_and_timing()
    {
        var id = await SeedPromptAsync();
        var client = Answering("The answer.");

        var (runId, error) = await Runner(client).RunAsync(new RunPromptDto { PromptId = id, Model = "model-a", RunBy = " carol " }, CancellationToken.None);

        Assert.Null(error);
        var run = await StoredRunAsync(runId!.Value);
        Assert.Equal(PromptRunStatus.Succeeded, run.Status);
        Assert.Equal("The answer.", run.Output);
        Assert.Equal("Version one text of the prompt.", run.PromptText);
        Assert.Equal(1, run.VersionNumber);
        Assert.Equal((1_000, 500), (run.InputTokens, run.OutputTokens));
        Assert.Equal(0.014m, run.CostUsd); // 1000 * $4/M + 500 * $20/M
        Assert.Equal("carol", run.RunBy);
        Assert.True(run.DurationMs >= 0);
        Assert.Equal("model-a", Assert.Single(client.Requests).Model);
    }

    [Fact]
    public async Task Runs_the_requested_version_not_just_the_current_one()
    {
        var id = await SeedPromptAsync(withSecondVersion: true);
        var client = Answering("ok");

        var (runId, _) = await Runner(client).RunAsync(new RunPromptDto { PromptId = id, VersionNumber = 1, Model = "model-a" }, CancellationToken.None);

        Assert.Equal("Version one text of the prompt.", client.Requests[0].Prompt);
        Assert.Equal(1, (await StoredRunAsync(runId!.Value)).VersionNumber);
    }

    [Fact]
    public async Task Defaults_to_the_current_version_and_anonymous_runner()
    {
        var id = await SeedPromptAsync(withSecondVersion: true);

        var (runId, _) = await Runner(Answering("ok")).RunAsync(new RunPromptDto { PromptId = id, Model = "model-a" }, CancellationToken.None);

        var run = await StoredRunAsync(runId!.Value);
        Assert.Equal(2, run.VersionNumber);
        Assert.Equal("anonymous", run.RunBy);
    }

    [Theory]
    [InlineData(999, null, "model-a", "Prompt not found.")]
    [InlineData(0, 7, "model-a", "Version 7 does not exist.")]
    [InlineData(0, null, "unknown", "Unknown model")]
    [InlineData(0, null, "offline", "No client")]
    public async Task Nothing_is_run_or_stored_for_invalid_input(int promptId, int? version, string model, string expected)
    {
        var id = await SeedPromptAsync();
        var client = Answering("never");

        var (runId, error) = await Runner(client).RunAsync(
            new RunPromptDto { PromptId = promptId == 0 ? id : promptId, VersionNumber = version, Model = model }, CancellationToken.None);

        Assert.Null(runId);
        Assert.Contains(expected, error);
        Assert.Empty(client.Requests);
        using var db = _database.CreateContext();
        Assert.Equal(0, await db.PromptRuns.CountAsync());
    }

    [Fact]
    public async Task Archived_prompts_are_not_run()
    {
        var id = await SeedPromptAsync();
        using (var db = _database.CreateContext())
            await new PromptService(db, NullLogger<PromptService>.Instance).ArchiveAsync(id);

        var (runId, error) = await Runner(Answering("x")).RunAsync(new RunPromptDto { PromptId = id, Model = "model-a" }, CancellationToken.None);

        Assert.Null(runId);
        Assert.Equal("Archived prompts cannot be run.", error);
    }

    [Fact]
    public async Task Provider_failure_is_stored_as_a_failed_run()
    {
        var id = await SeedPromptAsync();
        var client = new ScriptedClient((_, _) => throw new LlmException("Anthropic rate limit reached."));

        var (runId, error) = await Runner(client).RunAsync(new RunPromptDto { PromptId = id, Model = "model-a" }, CancellationToken.None);

        Assert.Null(error);
        var run = await StoredRunAsync(runId!.Value);
        Assert.Equal(PromptRunStatus.Failed, run.Status);
        Assert.Equal("Anthropic rate limit reached.", run.Error);
        Assert.Equal(0m, run.CostUsd);
    }

    [Fact]
    public async Task Slow_provider_times_out_and_is_stored_as_failed()
    {
        var id = await SeedPromptAsync();
        var client = new ScriptedClient(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            throw new InvalidOperationException("unreachable");
        });

        var (runId, _) = await Runner(client, timeoutSeconds: 1).RunAsync(new RunPromptDto { PromptId = id, Model = "model-a" }, CancellationToken.None);

        var run = await StoredRunAsync(runId!.Value);
        Assert.Equal(PromptRunStatus.Failed, run.Status);
        Assert.Equal("Timed out after 1 seconds.", run.Error);
    }

    [Fact]
    public async Task Cancellation_by_the_caller_is_not_swallowed()
    {
        var id = await SeedPromptAsync();
        using var cts = new CancellationTokenSource();
        var client = new ScriptedClient(async (_, ct) =>
        {
            await cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
            return null!;
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Runner(client).RunAsync(new RunPromptDto { PromptId = id, Model = "model-a" }, cts.Token));
    }

    [Theory]
    [InlineData(LlmOutcome.Refused, PromptRunStatus.Refused)]
    [InlineData(LlmOutcome.Truncated, PromptRunStatus.Truncated)]
    public async Task Maps_outcomes_to_statuses(LlmOutcome outcome, PromptRunStatus expected)
    {
        var id = await SeedPromptAsync();

        var (runId, _) = await Runner(Answering("", outcome, note: "why")).RunAsync(new RunPromptDto { PromptId = id, Model = "model-a" }, CancellationToken.None);

        var run = await StoredRunAsync(runId!.Value);
        Assert.Equal(expected, run.Status);
        Assert.Equal("why", run.Note);
    }

    [Fact]
    public async Task Fallback_answer_is_priced_with_the_fallback_model()
    {
        var id = await SeedPromptAsync();

        var (runId, _) = await Runner(Answering("ok", servedBy: "model-b")).RunAsync(new RunPromptDto { PromptId = id, Model = "model-a" }, CancellationToken.None);

        var run = await StoredRunAsync(runId!.Value);
        Assert.Equal("model-b", run.ServedByModel);
        Assert.Equal(0.002m, run.CostUsd); // 1000 * $1/M + 500 * $2/M
    }

    [Fact]
    public async Task Lists_runs_newest_first_with_paging_and_prompt_filter()
    {
        var a = await SeedPromptAsync();
        var b = await SeedPromptAsync();
        for (var i = 0; i < 3; i++)
            await Runner(Answering($"a{i}")).RunAsync(new RunPromptDto { PromptId = a, Model = "model-a" }, CancellationToken.None);
        await Runner(Answering("b")).RunAsync(new RunPromptDto { PromptId = b, Model = "model-a" }, CancellationToken.None);

        var all = await Runner(Answering("")).ListRunsAsync(null, page: 1, pageSize: 2);
        var onlyA = await Runner(Answering("")).ListRunsAsync(a, page: 2, pageSize: 2);

        Assert.Equal(4, all.TotalCount);
        Assert.Equal(2, all.PageCount);
        Assert.Equal(b, all.Runs[0].PromptId);
        Assert.Equal(3, onlyA.TotalCount);
        Assert.Single(onlyA.Runs);
        Assert.Equal("Runnable", onlyA.PromptTitle);
    }

    [Fact]
    public async Task Paid_models_are_refused_without_permission_and_echo_is_not_paid()
    {
        var id = await SeedPromptAsync();
        var client = Answering("never");

        var (runId, error) = await Runner(client).RunAsync(
            new RunPromptDto { PromptId = id, Model = "model-a", MayUsePaidModels = false }, CancellationToken.None);

        Assert.Null(runId);
        Assert.Contains("only admins", error);
        Assert.Empty(client.Requests);
        Assert.False(new LlmModelOptions { Provider = EchoLlmClient.ProviderName }.IsPaid);
        Assert.True(new LlmModelOptions { Provider = "Anthropic" }.IsPaid);
    }

    [Fact]
    public async Task Run_records_the_account_that_ran_it()
    {
        var id = await SeedPromptAsync();

        var (runId, _) = await Runner(Answering("ok")).RunAsync(
            new RunPromptDto { PromptId = id, Model = "model-a", RunBy = "Olena", RunByUserId = "user-7" }, CancellationToken.None);

        var run = await StoredRunAsync(runId!.Value);
        Assert.Equal(("Olena", "user-7"), (run.RunBy, run.RunByUserId));
    }
}
