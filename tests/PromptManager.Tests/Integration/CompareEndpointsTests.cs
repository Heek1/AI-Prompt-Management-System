using System.Net;
using Microsoft.EntityFrameworkCore;
using PromptManager.Tests.Infrastructure;

namespace PromptManager.Tests.Integration;

public sealed class CompareEndpointsTests : IDisposable
{
    private readonly PromptManagerFactory _factory = new();
    private readonly HttpClient _client;

    public CompareEndpointsTests()
    {
        _client = _factory.CreateNoRedirectClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task<int> CreatePromptAsync(string text)
    {
        var response = await _client.PostFormAsync("/Prompts/Create", "/Prompts/Create", new Dictionary<string, string>
        {
            ["Title"] = "Versioned", ["PromptText"] = text, ["Category"] = "Learning", ["AiTool"] = "Claude", ["Author"] = "alice"
        });
        return int.Parse(response.Headers.Location!.OriginalString.Split('/').Last());
    }

    private async Task AddVersionAsync(int id, string text)
    {
        var response = await _client.PostFormAsync($"/Prompts/CreateVersion/{id}", "/Prompts/CreateVersion", new Dictionary<string, string>
        {
            ["PromptId"] = id.ToString(), ["PromptText"] = text, ["CreatedBy"] = "bob", ["ChangeNote"] = "tweak"
        });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task Compare_shows_a_word_level_diff_of_the_latest_two_versions()
    {
        var id = await CreatePromptAsync("Summarise the text in three bullet points.");
        await AddVersionAsync(id, "Summarise the text in five bullet points.");

        var html = await _client.GetStringAsync($"/Prompts/Compare/{id}");

        Assert.Contains("<mark>three</mark>", html);
        Assert.Contains("<mark>five</mark>", html);
        Assert.Contains("~1 changed", html);
        Assert.Contains("Compare versions", await _client.GetStringAsync($"/Prompts/Details/{id}"));
    }

    [Fact]
    public async Task Compare_with_one_version_redirects_to_details()
    {
        var id = await CreatePromptAsync("Only a single version of this prompt.");

        var response = await _client.GetAsync($"/Prompts/Compare/{id}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task Compare_with_unknown_version_is_404()
    {
        var id = await CreatePromptAsync("Summarise the text in three bullet points.");
        await AddVersionAsync(id, "Summarise the text in five bullet points.");

        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/Prompts/Compare/{id}?from=1&to=9")).StatusCode);
    }

    [Fact]
    public async Task Run_both_creates_two_runs_and_shows_them_side_by_side()
    {
        var id = await CreatePromptAsync("Translate {{text}} politely.");
        await AddVersionAsync(id, "Translate {{text}} casually.");

        var response = await _client.PostFormAsync($"/Prompts/Compare/{id}", "/Prompts/RunCompare", new Dictionary<string, string>
        {
            ["PromptId"] = id.ToString(), ["FromVersion"] = "1", ["ToVersion"] = "2", ["Model"] = "echo", ["Variables[text]"] = "hello"
        });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.OriginalString;
        Assert.StartsWith("/Runs/Compare", location);

        var html = await _client.GetStringAsync(location);
        Assert.Contains("Translate hello politely.", html);
        Assert.Contains("Translate hello casually.", html);
        Assert.Contains("<mark>politely", html);
        Assert.Contains("<mark>casually", html);
    }

    [Fact]
    public async Task Run_both_with_a_missing_value_runs_neither_version()
    {
        var id = await CreatePromptAsync("Translate the greeting into Ukrainian.");
        await AddVersionAsync(id, "Translate {{text}} into Ukrainian.");

        var response = await _client.PostFormAsync($"/Prompts/Compare/{id}", "/Prompts/RunCompare", new Dictionary<string, string>
        {
            ["PromptId"] = id.ToString(), ["FromVersion"] = "1", ["ToVersion"] = "2", ["Model"] = "echo"
        });

        Assert.StartsWith($"/Prompts/Compare/{id}", response.Headers.Location!.OriginalString);
        await using var db = _factory.CreateDbContext();
        Assert.Equal(0, await db.PromptRuns.CountAsync());
    }

    [Fact]
    public async Task Compare_runs_with_unknown_run_is_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/Runs/Compare?left=1&right=2")).StatusCode);
    }
}
