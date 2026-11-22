using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PromptManager.DTOs;
using PromptManager.Llm;
using PromptManager.Services;
using PromptManager.Tests.Infrastructure;

namespace PromptManager.Tests.Services;

public sealed class PromptRunnerVariablesTests : IDisposable
{
    private readonly TestDatabase _database = new();

    public void Dispose() => _database.Dispose();

    private sealed class RecordingClient : ILlmClient
    {
        public List<string> Prompts { get; } = [];
        public string Provider => "Rec";
        public bool IsConfigured => true;

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            Prompts.Add(request.Prompt);
            return Task.FromResult(new LlmResponse("ok", 1, 1, LlmOutcome.Completed, "end_turn"));
        }
    }

    private readonly RecordingClient _client = new();

    private PromptRunner Runner() =>
        new(_database.CreateContext(),
            new LlmCatalog(Options.Create(new LlmOptions { Models = [new() { Id = "m", Provider = "Rec" }] }), [_client]),
            NullLogger<PromptRunner>.Instance);

    private async Task<int> SeedAsync(string text)
    {
        using var db = _database.CreateContext();
        return await new PromptService(db, new ConfigurationBuilder().Build(), NullLogger<PromptService>.Instance)
            .CreateAsync(new CreatePromptDto { Title = "T", PromptText = text, Category = "Testing", AiTool = "Claude", Author = "a" });
    }

    private static RunPromptDto Run(int id, Dictionary<string, string?> vars, string? saveAs = null) =>
        new() { PromptId = id, Model = "m", Variables = vars, SaveInputSetAs = saveAs };

    [Fact]
    public async Task Fills_variables_and_stores_their_values()
    {
        var id = await SeedAsync("Translate {{text}} into {{language}}.");

        var (runId, error) = await Runner().RunAsync(Run(id, new() { ["text"] = "hello", ["language"] = "Ukrainian", ["unused"] = "x" }), CancellationToken.None);

        Assert.Null(error);
        Assert.Equal("Translate hello into Ukrainian.", Assert.Single(_client.Prompts));
        var run = await Runner().GetRunAsync(runId!.Value);
        Assert.Equal("Translate hello into Ukrainian.", run!.PromptText);
        // Only variables the version uses are stored.
        Assert.Equal(new Dictionary<string, string> { ["text"] = "hello", ["language"] = "Ukrainian" }, run.Variables);
    }

    [Fact]
    public async Task Refuses_to_run_with_missing_values()
    {
        var id = await SeedAsync("Translate {{text}} into {{language}}.");

        var (runId, error) = await Runner().RunAsync(Run(id, new() { ["text"] = "hello" }), CancellationToken.None);

        Assert.Null(runId);
        Assert.Equal("Fill in a value for: language.", error);
        Assert.Empty(_client.Prompts);
    }

    [Fact]
    public async Task Empty_form_field_counts_as_an_empty_value()
    {
        var id = await SeedAsync("Context: [{{context}}] Answer the question please.");

        var (runId, _) = await Runner().RunAsync(Run(id, new() { ["context"] = null }), CancellationToken.None);

        Assert.NotNull(runId);
        Assert.Equal("Context: [] Answer the question please.", _client.Prompts[0]);
    }

    [Fact]
    public async Task Prompt_without_variables_stores_no_variables()
    {
        var id = await SeedAsync("No variables in this prompt at all.");

        var (runId, _) = await Runner().RunAsync(Run(id, new()), CancellationToken.None);

        using var db = _database.CreateContext();
        Assert.Null((await db.PromptRuns.SingleAsync(r => r.Id == runId)).VariablesJson);
    }

    [Fact]
    public async Task Rejects_values_over_the_length_limit()
    {
        var id = await SeedAsync("Summarise {{text}} in one line.");

        var (runId, error) = await Runner().RunAsync(Run(id, new() { ["text"] = new string('x', 20_001) }), CancellationToken.None);

        Assert.Null(runId);
        Assert.Contains("longer than", error);
    }

    [Fact]
    public async Task Saves_and_replaces_an_input_set_by_name()
    {
        var id = await SeedAsync("Translate {{text}} into {{language}}.");

        await Runner().RunAsync(Run(id, new() { ["text"] = "hi", ["language"] = "German" }, saveAs: "short"), CancellationToken.None);
        await Runner().RunAsync(Run(id, new() { ["text"] = "hello", ["language"] = "French" }, saveAs: " short "), CancellationToken.None);

        using var db = _database.CreateContext();
        var set = await db.PromptInputSets.SingleAsync();
        Assert.Equal("short", set.Name);
        Assert.Equal("French", JsonSerializer.Deserialize<Dictionary<string, string>>(set.ValuesJson)!["language"]);

        var listed = await new InputSetService(db).ListAsync(id);
        Assert.Equal("hello", Assert.Single(listed).Values["text"]);
    }

    [Fact]
    public async Task Input_sets_are_deleted_only_for_their_own_prompt()
    {
        var id = await SeedAsync("Say {{x}} please, nothing else.");
        await Runner().RunAsync(Run(id, new() { ["x"] = "1" }, saveAs: "one"), CancellationToken.None);
        using var db = _database.CreateContext();
        var setId = (await db.PromptInputSets.SingleAsync()).Id;
        var service = new InputSetService(db);

        Assert.False(await service.DeleteAsync(id + 1, setId));
        Assert.True(await service.DeleteAsync(id, setId));
        Assert.Empty(await service.ListAsync(id));
    }
}
