using System.Net;
using System.Text.RegularExpressions;
using PromptManager.Tests.Infrastructure;

namespace PromptManager.Tests.Integration;

/// <summary>The whole loop through the real background worker, on the offline echo model.</summary>
public sealed class EvalsEndpointsTests : IDisposable, IAsyncLifetime
{
    private readonly PromptManagerFactory _factory = new();
    private readonly HttpClient _client;

    public EvalsEndpointsTests()
    {
        _client = _factory.CreateNoRedirectClient();
    }

    public Task InitializeAsync() => _client.SignUpAsync("owner@test.local", "Owner");

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task<int> CreatePromptAsync()
    {
        var response = await _client.PostFormAsync("/Prompts/Create", "/Prompts/Create", new Dictionary<string, string>
        {
            ["Title"] = "Greeter", ["PromptText"] = "Say hello to {{name}} politely.", ["Category"] = "Learning", ["AiTool"] = "Claude"
        });
        return int.Parse(response.Headers.Location!.OriginalString.Split('/').Last());
    }

    private Task<HttpResponseMessage> AddCaseAsync(HttpClient client, int promptId, string name, string mustContain) =>
        client.PostFormAsync($"/Evals/Index/{promptId}", "/Evals/Case", new Dictionary<string, string>
        {
            ["Id"] = "0", ["PromptId"] = promptId.ToString(), ["Name"] = name,
            ["Variables[name]"] = "Olena", ["MustContain"] = mustContain
        });

    private async Task<string> WaitForCompletionAsync(string url)
    {
        for (var i = 0; i < 50; i++)
        {
            var html = await _client.GetStringAsync(url);
            if (html.Contains("cases passed") || html.Contains(">Failed<")) return html;
            await Task.Delay(200);
        }
        throw new TimeoutException("The eval did not finish in 10 seconds.");
    }

    [Fact]
    public async Task Owner_adds_cases_runs_an_eval_and_sees_the_result()
    {
        var id = await CreatePromptAsync();
        Assert.Equal(HttpStatusCode.Redirect, (await AddCaseAsync(_client, id, "names the person", "Olena")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await AddCaseAsync(_client, id, "speaks French", "Bonjour")).StatusCode);

        var start = await _client.PostFormAsync($"/Evals/Index/{id}", "/Evals/Start", new Dictionary<string, string>
        {
            ["PromptId"] = id.ToString(), ["Model"] = "echo"
        });
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var runUrl = start.Headers.Location!.OriginalString;
        Assert.StartsWith("/Evals/Run/", runUrl);

        var html = await WaitForCompletionAsync(runUrl);

        Assert.Matches(new Regex(@"1\s*/\s*2"), html);
        Assert.Contains("names the person", html);
        Assert.Contains("contains &quot;Bonjour&quot;: <span class=\"text-body\">not found", html);
        Assert.Contains("1/2", await _client.GetStringAsync($"/Evals/Index/{id}"));
    }

    [Fact]
    public async Task Case_without_checks_is_rejected_with_a_message()
    {
        var id = await CreatePromptAsync();

        var response = await _client.PostFormAsync($"/Evals/Index/{id}", "/Evals/Case", new Dictionary<string, string>
        {
            ["Id"] = "0", ["PromptId"] = id.ToString(), ["Name"] = "no checks"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Add at least one check", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Another_user_can_run_but_not_edit_cases()
    {
        var id = await CreatePromptAsync();
        await AddCaseAsync(_client, id, "names the person", "Olena");
        var other = _factory.CreateNoRedirectClient();
        await other.SignUpAsync("other@test.local");

        Assert.Contains("/Account/AccessDenied", (await other.GetAsync($"/Evals/Case/{id}")).Headers.Location!.OriginalString);

        var start = await other.PostFormAsync($"/Evals/Index/{id}", "/Evals/Start", new Dictionary<string, string>
        {
            ["PromptId"] = id.ToString(), ["Model"] = "echo"
        });
        Assert.StartsWith("/Evals/Run/", start.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Evals_page_is_readable_without_an_account()
    {
        var id = await CreatePromptAsync();
        await AddCaseAsync(_client, id, "names the person", "Olena");

        var html = await _factory.CreateClient().GetStringAsync($"/Evals/Index/{id}");

        Assert.Contains("names the person", html);
        Assert.Contains("to run evals", html);
    }

    [Fact]
    public async Task Compare_models_shows_one_row_per_model()
    {
        var id = await CreatePromptAsync();
        await AddCaseAsync(_client, id, "names the person", "Olena");

        var response = await _client.PostAsync("/Evals/CompareModels", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("__RequestVerificationToken", await TokenAsync($"/Evals/Index/{id}")),
            new KeyValuePair<string, string>("PromptId", id.ToString()),
            new KeyValuePair<string, string>("Models", "echo"),
            new KeyValuePair<string, string>("Models", "echo-2")
        }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var url = response.Headers.Location!.OriginalString;
        Assert.StartsWith("/Evals/Compare?ids=", url);

        string html = "";
        for (var i = 0; i < 50 && !html.Contains("100%"); i++)
        {
            html = await _client.GetStringAsync(url);
            if (html.Split("100%").Length - 1 >= 2) break;
            await Task.Delay(200);
        }
        Assert.Contains("Echo (offline, no model call)", html);
        Assert.Contains("Echo 2", html);
        Assert.True(html.Split("100%").Length - 1 >= 2, "both models should pass the case");
    }

    [Fact]
    public async Task Compare_rejects_unknown_ids()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/Evals/Compare?ids=999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/Evals/Compare?ids=x")).StatusCode);
    }

    private async Task<string> TokenAsync(string page)
    {
        var html = await _client.GetStringAsync(page);
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }
}
