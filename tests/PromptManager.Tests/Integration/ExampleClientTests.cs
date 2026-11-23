using System.Net;
using System.Text.RegularExpressions;
using PromptClient;
using PromptManager.Tests.Infrastructure;

namespace PromptManager.Tests.Integration;

/// <summary>Keeps the example in examples/PromptClient honest: it runs against the real app.</summary>
public sealed class ExampleClientTests : IDisposable, IAsyncLifetime
{
    private readonly PromptManagerFactory _factory = new();
    private readonly HttpClient _web;
    private PromptManagerClient _client = null!;
    private int _promptId;

    public ExampleClientTests()
    {
        _web = _factory.CreateNoRedirectClient();
    }

    public async Task InitializeAsync()
    {
        await _web.SignUpAsync("owner@test.local");
        var create = await _web.PostFormAsync("/Prompts/Create", "/Prompts/Create", new Dictionary<string, string>
        {
            ["Title"] = "Support reply", ["PromptText"] = "Reply politely to {{customer}} about {{topic}}.",
            ["Category"] = "Learning", ["AiTool"] = "Claude"
        });
        _promptId = int.Parse(create.Headers.Location!.OriginalString.Split('/').Last());

        await _web.PostFormAsync("/ApiKeys", "/ApiKeys/Create", new Dictionary<string, string> { ["name"] = "example" });
        var key = Regex.Match(await _web.GetStringAsync("/ApiKeys"), "data-testid=\"new-key\">([^<]+)<").Groups[1].Value;

        _client = new PromptManagerClient(_factory.CreateClient(), key);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _web.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Gets_the_prompt_then_revalidates_with_304()
    {
        var first = await _client.GetAsync("support-reply");
        var second = await _client.GetAsync("support-reply");

        Assert.Equal((1, "production"), (first.Version, first.Status));
        Assert.Equal(new[] { "customer", "topic" }, first.Variables);
        Assert.Same(first, second);
        Assert.Equal(1, _client.NotModifiedCount);
    }

    [Fact]
    public async Task Sees_a_promotion_on_the_next_request()
    {
        await _client.GetAsync("support-reply");
        await _web.PostFormAsync($"/Prompts/CreateVersion/{_promptId}", "/Prompts/CreateVersion", new Dictionary<string, string>
        {
            ["PromptId"] = _promptId.ToString(), ["PromptText"] = "Reply warmly to {{customer}} about {{topic}}."
        });
        await _web.PostFormAsync($"/Prompts/Details/{_promptId}", "/Prompts/Promote", new Dictionary<string, string>
        {
            ["id"] = _promptId.ToString(), ["versionNumber"] = "2"
        });

        var after = await _client.GetAsync("support-reply");

        Assert.Equal(2, after.Version);
        Assert.Equal(0, _client.NotModifiedCount);
    }

    [Fact]
    public async Task Renders_variables()
    {
        var rendered = await _client.RenderAsync("support-reply", new Dictionary<string, string>
        {
            ["customer"] = "Olena", ["topic"] = "a refund"
        });

        Assert.Equal("Reply politely to Olena about a refund.", rendered.Text);
    }

    [Fact]
    public async Task Reports_api_errors_with_the_status_code()
    {
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => _client.GetAsync("does-not-exist"));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
    }
}
