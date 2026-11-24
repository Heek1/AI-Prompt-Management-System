using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PromptManager.Evals;
using PromptManager.Llm;
using PromptManager.Models;
using PromptManager.Tests.Infrastructure;

namespace PromptManager.Tests.Evals;

/// <summary>B11: a model proposes a revision from a failed eval; accepting it saves a draft and reruns the eval.</summary>
public sealed class ImprovementAssistantTests : IDisposable
{
    /// <summary>A "model" that always proposes the same fix and records what it was asked.</summary>
    private sealed class WriterClient : ILlmClient
    {
        public static string? LastPrompt;
        public string Provider => "Writer";
        public bool IsConfigured => true;

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            LastPrompt = request.Prompt;
            return Task.FromResult(new LlmResponse(
                "<analysis>The answer never greets in French.</analysis>\n<revised_prompt>\nSay hello to {{name}} politely. Start with Bonjour.\n</revised_prompt>",
                900, 60, LlmOutcome.Completed, "end_turn"));
        }
    }

    private readonly PromptManagerFactory _base = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public ImprovementAssistantTests()
    {
        _factory = _base.WithWebHostBuilder(b =>
        {
            b.UseSetting("Llm:Models:5:Id", "writer-1");
            b.UseSetting("Llm:Models:5:DisplayName", "Writer");
            b.UseSetting("Llm:Models:5:Provider", "Writer");
            // Every model but echo counts as paid; let ordinary users use it here.
            b.UseSetting("Llm:PaidModelsAccess", "Everyone");
            b.ConfigureTestServices(s => s.AddSingleton<ILlmClient, WriterClient>());
        });
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        _base.Dispose();
    }

    private async Task<string> WaitForCompletionAsync(string url)
    {
        for (var i = 0; i < 50; i++)
        {
            var html = await _client.GetStringAsync(url);
            if (!html.Contains("http-equiv=\"refresh\"")) return html;
            await Task.Delay(200);
        }
        throw new TimeoutException("The eval did not finish in 10 seconds.");
    }

    /// <summary>A prompt whose only case fails on echo, and the finished eval's page.</summary>
    private async Task<(int PromptId, string EvalUrl)> FailedEvalAsync()
    {
        await _client.SignUpAsync("owner@test.local", "Owner");
        var create = await _client.PostFormAsync("/Prompts/Create", "/Prompts/Create", new Dictionary<string, string>
        {
            ["Title"] = "Greeter", ["PromptText"] = "Say hello to {{name}} politely.", ["Category"] = "Learning", ["AiTool"] = "Claude"
        });
        var id = int.Parse(create.Headers.Location!.OriginalString.Split('/').Last());
        await _client.PostFormAsync($"/Evals/Index/{id}", "/Evals/Case", new Dictionary<string, string>
        {
            ["Id"] = "0", ["PromptId"] = id.ToString(), ["Name"] = "speaks French", ["Variables[name]"] = "Olena", ["MustContain"] = "Bonjour"
        });
        var start = await _client.PostFormAsync($"/Evals/Index/{id}", "/Evals/Start", new Dictionary<string, string>
        {
            ["PromptId"] = id.ToString(), ["VersionNumber"] = "1", ["Model"] = "echo"
        });
        var url = start.Headers.Location!.OriginalString;
        Assert.Contains("0 / 1", await WaitForCompletionAsync(url));
        return (id, url);
    }

    [Fact]
    public async Task Suggestion_is_reviewed_as_a_diff_then_saved_as_a_draft_that_passes_the_same_eval()
    {
        var (promptId, evalUrl) = await FailedEvalAsync();
        var evalId = evalUrl.Split('/').Last();

        var runPage = await _client.GetStringAsync(evalUrl);
        Assert.Contains("Suggest a fix", runPage);
        Assert.DoesNotContain("value=\"echo\"", runPage[runPage.IndexOf("suggest-model", StringComparison.Ordinal)..]);

        var suggest = await _client.PostFormAsync(evalUrl, "/Evals/Suggest", new Dictionary<string, string>
        {
            ["evalRunId"] = evalId, ["model"] = "writer-1"
        });
        Assert.Equal(HttpStatusCode.Redirect, suggest.StatusCode);
        var reviewUrl = suggest.Headers.Location!.OriginalString;

        // The model saw the failing answer and why it failed, as data.
        Assert.Contains("<failed_checks>", WriterClient.LastPrompt);
        Assert.Contains("contains \"Bonjour\"", WriterClient.LastPrompt);
        Assert.Contains("Say hello to Olena politely.", WriterClient.LastPrompt);

        var review = await _client.GetStringAsync(reviewUrl);
        Assert.Contains("The answer never greets in French.", review);
        Assert.Contains("<mark>", review); // the diff marks the change
        Assert.Contains("Start with Bonjour.</textarea>", review);

        // Nothing was saved yet; the call itself is on record with its note.
        await using (var db = _base.CreateDbContext())
        {
            Assert.Equal(1, (await db.Prompts.SingleAsync(p => p.Id == promptId)).CurrentVersion);
            Assert.Equal(1, await db.PromptRuns.CountAsync(r => r.Note == $"Improvement suggestion from eval #{evalId}"));
        }

        var accept = await _client.PostFormAsync(reviewUrl, "/Evals/AcceptSuggestion", new Dictionary<string, string>
        {
            ["evalRunId"] = evalId, ["text"] = "Say hello to {{name}} politely. Start with Bonjour.", ["changeNote"] = "Greets in French"
        });
        Assert.Equal(HttpStatusCode.Redirect, accept.StatusCode);
        Assert.Contains("1 / 1", await WaitForCompletionAsync(accept.Headers.Location!.OriginalString));

        await using var after = _base.CreateDbContext();
        var v2 = await after.PromptVersions.SingleAsync(v => v.PromptId == promptId && v.VersionNumber == 2);
        Assert.Equal("Greets in French", v2.ChangeNote);
        // A passing eval marks the draft as tested; production stays on v1.
        Assert.Equal(VersionStatus.Tested, v2.Status);
        Assert.Equal(1, (await after.Prompts.SingleAsync(p => p.Id == promptId)).ProductionVersion);
    }

    [Fact]
    public async Task Echo_cannot_suggest_and_others_cannot_ask_for_someone_elses_prompt()
    {
        var (_, evalUrl) = await FailedEvalAsync();
        var evalId = evalUrl.Split('/').Last();

        var echo = await _client.PostFormAsync(evalUrl, "/Evals/Suggest", new Dictionary<string, string>
        {
            ["evalRunId"] = evalId, ["model"] = "echo"
        });
        Assert.Contains("cannot write a prompt", await _client.GetStringAsync(echo.Headers.Location!.OriginalString));

        var other = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await other.SignUpAsync("other@test.local", "Other");
        Assert.DoesNotContain("Suggest a fix", await other.GetStringAsync(evalUrl));
        var forbidden = await other.PostFormAsync(evalUrl, "/Evals/Suggest", new Dictionary<string, string>
        {
            ["evalRunId"] = evalId, ["model"] = "writer-1"
        });
        Assert.Contains("/Account/AccessDenied", forbidden.Headers.Location!.OriginalString);
    }

    [Theory]
    [InlineData("<analysis>a</analysis><revised_prompt>\nNew text here.\n</revised_prompt>", "New text here.")]
    [InlineData("no tags at all", null)]
    [InlineData("<revised_prompt>   </revised_prompt>", null)]
    public void Parse_takes_the_text_between_the_tags(string answer, string? expected)
    {
        Assert.Equal(expected, ImprovementAssistant.Parse(answer).Revised);
    }

    [Fact]
    public void Check_warns_about_changed_variables()
    {
        var warnings = ImprovementAssistant.Check("Hello {{name}}, about {{topic}}.", "Hello {{name}}, regarding {{subject}}.");

        Assert.Contains(warnings, w => w.Contains("drops {{topic}}"));
        Assert.Contains(warnings, w => w.Contains("adds {{subject}}"));
    }
}
