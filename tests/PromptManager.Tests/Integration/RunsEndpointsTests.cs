using System.Net;
using PromptManager.Tests.Infrastructure;

namespace PromptManager.Tests.Integration;

/// <summary>Uses the built-in offline "echo" model, so no provider is called.</summary>
public sealed class RunsEndpointsTests : IDisposable
{
    private readonly PromptManagerFactory _factory = new();
    private readonly HttpClient _client;

    public RunsEndpointsTests()
    {
        _client = _factory.CreateNoRedirectClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task<int> CreatePromptAsync()
    {
        var response = await _client.PostFormAsync("/Prompts/Create", "/Prompts/Create", new Dictionary<string, string>
        {
            ["Title"] = "Runnable prompt",
            ["PromptText"] = "Translate 'good morning' into Ukrainian.",
            ["Category"] = "Learning",
            ["AiTool"] = "Claude",
            ["Author"] = "alice"
        });
        return int.Parse(response.Headers.Location!.OriginalString.Split('/').Last());
    }

    [Fact]
    public async Task Details_page_offers_the_run_form_with_configured_models()
    {
        var id = await CreatePromptAsync();

        var html = await _client.GetStringAsync($"/Prompts/Details/{id}");

        Assert.Contains("action=\"/Prompts/Run\"", html);
        Assert.Contains("value=\"echo\"", html);
        Assert.Contains("value=\"claude-opus-5-5\"", html);
        Assert.Contains("has not been run yet", html);
    }

    [Fact]
    public async Task Running_on_echo_stores_a_run_and_shows_it()
    {
        var id = await CreatePromptAsync();

        var response = await _client.PostFormAsync($"/Prompts/Details/{id}", "/Prompts/Run", new Dictionary<string, string>
        {
            ["PromptId"] = id.ToString(), ["Model"] = "echo", ["RunBy"] = "bob"
        });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.OriginalString;
        Assert.StartsWith("/Runs/Details/", location);

        var run = await _client.GetStringAsync(location);
        Assert.Contains("Echo (no model was called)", run);
        Assert.Contains("Succeeded", run);

        var details = await _client.GetStringAsync($"/Prompts/Details/{id}");
        Assert.Contains("All 1 runs", details);

        var index = await _client.GetStringAsync("/Runs");
        Assert.Contains("Runnable prompt", index);
    }

    [Fact]
    public async Task Unknown_model_redirects_back_with_an_error()
    {
        var id = await CreatePromptAsync();

        var response = await _client.PostFormAsync($"/Prompts/Details/{id}", "/Prompts/Run", new Dictionary<string, string>
        {
            ["PromptId"] = id.ToString(), ["Model"] = "not-a-model"
        });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/Prompts/Details/{id}", response.Headers.Location!.OriginalString);
    }

    [Theory]
    [InlineData("/Runs")]
    [InlineData("/Runs?promptId=1&page=3")]
    public async Task Runs_index_renders_when_empty(string url)
    {
        var response = await _client.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_run_is_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/Runs/Details/999")).StatusCode);
    }

    [Fact]
    public async Task Variables_get_form_fields_and_can_be_saved_and_reloaded()
    {
        var create = await _client.PostFormAsync("/Prompts/Create", "/Prompts/Create", new Dictionary<string, string>
        {
            ["Title"] = "Templated",
            ["PromptText"] = "Translate {{text}} into {{language}}.",
            ["Category"] = "Learning",
            ["AiTool"] = "Claude",
            ["Author"] = "alice"
        });
        var id = int.Parse(create.Headers.Location!.OriginalString.Split('/').Last());

        var page = await _client.GetStringAsync($"/Prompts/Details/{id}");
        Assert.Contains("name=\"Variables[text]\"", page);
        Assert.Contains("name=\"Variables[language]\"", page);

        var run = await _client.PostFormAsync($"/Prompts/Details/{id}", "/Prompts/Run", new Dictionary<string, string>
        {
            ["PromptId"] = id.ToString(),
            ["Model"] = "echo",
            ["Variables[text]"] = "good night",
            ["Variables[language]"] = "Ukrainian",
            ["SaveInputSetAs"] = "evening"
        });
        Assert.Equal(HttpStatusCode.Redirect, run.StatusCode);
        Assert.Contains("Translate good night into Ukrainian.", await _client.GetStringAsync(run.Headers.Location!.OriginalString));

        var withSet = await _client.GetStringAsync($"/Prompts/Details/{id}");
        Assert.Contains(">evening<", withSet);
        var setId = System.Text.RegularExpressions.Regex.Match(withSet, @"inputSet=(\d+)").Groups[1].Value;
        var prefilled = await _client.GetStringAsync($"/Prompts/Details/{id}?inputSet={setId}");
        Assert.Contains(">good night</textarea>", prefilled);
    }

    [Fact]
    public async Task Missing_variable_redirects_back_with_an_error_and_stores_nothing()
    {
        var create = await _client.PostFormAsync("/Prompts/Create", "/Prompts/Create", new Dictionary<string, string>
        {
            ["Title"] = "Templated",
            ["PromptText"] = "Summarise {{text}} in one sentence.",
            ["Category"] = "Learning",
            ["AiTool"] = "Claude",
            ["Author"] = "alice"
        });
        var id = int.Parse(create.Headers.Location!.OriginalString.Split('/').Last());

        var run = await _client.PostFormAsync($"/Prompts/Details/{id}", "/Prompts/Run", new Dictionary<string, string>
        {
            ["PromptId"] = id.ToString(), ["Model"] = "echo"
        });

        Assert.Equal($"/Prompts/Details/{id}", run.Headers.Location!.OriginalString);
        Assert.Contains("Fill in a value for: text.", await _client.GetStringAsync(run.Headers.Location.OriginalString));
        await using var db = _factory.CreateDbContext();
        Assert.Equal(0, await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(db.PromptRuns));
    }
}
