using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PromptManager.Api;
using PromptManager.Infrastructure;
using PromptManager.Tests.Infrastructure;

namespace PromptManager.Tests.Integration;

public sealed class PromptApiTests : IDisposable, IAsyncLifetime
{
    private readonly PromptManagerFactory _factory = new();
    private readonly HttpClient _web;
    private string _key = "";
    private int _promptId;

    public PromptApiTests()
    {
        _web = _factory.CreateNoRedirectClient();
    }

    public async Task InitializeAsync()
    {
        await _web.SignUpAsync("owner@test.local");
        var create = await _web.PostFormAsync("/Prompts/Create", "/Prompts/Create", new Dictionary<string, string>
        {
            ["Title"] = "Support Reply (EN)", ["PromptText"] = "Reply politely to {{customer}} about {{topic}}.",
            ["Category"] = "Learning", ["AiTool"] = "Claude"
        });
        _promptId = int.Parse(create.Headers.Location!.OriginalString.Split('/').Last());
        await _web.PostFormAsync($"/Prompts/CreateVersion/{_promptId}", "/Prompts/CreateVersion", new Dictionary<string, string>
        {
            ["PromptId"] = _promptId.ToString(), ["PromptText"] = "Reply warmly to {{customer}} about {{topic}}, in two sentences."
        });
        _key = await CreateKeyAsync("test app");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _web.Dispose();
        _factory.Dispose();
    }

    private async Task<string> CreateKeyAsync(string name)
    {
        var response = await _web.PostFormAsync("/ApiKeys", "/ApiKeys/Create", new Dictionary<string, string> { ["name"] = name });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var html = await _web.GetStringAsync("/ApiKeys");
        return Regex.Match(html, "data-testid=\"new-key\">([^<]+)<").Groups[1].Value;
    }

    private HttpClient Api(string? key = null)
    {
        var client = _factory.CreateClient();
        if (key is not null) client.DefaultRequestHeaders.Add("X-Api-Key", key);
        return client;
    }

    // ---------- Keys ----------

    [Fact]
    public async Task New_key_is_shown_once_and_stored_only_as_a_hash()
    {
        Assert.StartsWith(ApiKeyService.KeyPrefix, _key);
        Assert.DoesNotContain(_key, await _web.GetStringAsync("/ApiKeys")); // second visit

        await using var db = _factory.CreateDbContext();
        var stored = await db.ApiKeys.SingleAsync();
        Assert.Equal(ApiKeyService.Hash(_key), stored.Hash);
        Assert.NotEqual(_key, stored.Hash);
        Assert.Equal(_key[..10], stored.Prefix);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("pm_not-a-real-key")]
    [InlineData("something-else")]
    public async Task Missing_or_wrong_key_is_401(string? key)
    {
        var response = await Api(key).GetAsync("/api/prompts/support-reply-en");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Bearer_header_works_too()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", _key);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/prompts/support-reply-en")).StatusCode);
    }

    [Fact]
    public async Task Revoked_key_stops_working()
    {
        await using (var db = _factory.CreateDbContext())
        {
            var id = (await db.ApiKeys.SingleAsync()).Id;
            await _web.PostFormAsync("/ApiKeys", "/ApiKeys/Revoke", new Dictionary<string, string> { ["id"] = id.ToString() });
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await Api(_key).GetAsync("/api/prompts/support-reply-en")).StatusCode);
    }

    [Fact]
    public async Task Another_user_cannot_revoke_my_key()
    {
        var other = _factory.CreateNoRedirectClient();
        await other.SignUpAsync("other@test.local");
        await using var db = _factory.CreateDbContext();
        var id = (await db.ApiKeys.SingleAsync()).Id;

        await other.PostFormAsync("/ApiKeys", "/ApiKeys/Revoke", new Dictionary<string, string> { ["id"] = id.ToString() });

        Assert.Equal(HttpStatusCode.OK, (await Api(_key).GetAsync("/api/prompts/support-reply-en")).StatusCode);
    }

    // ---------- Reading ----------

    [Fact]
    public async Task Returns_the_production_version_by_default()
    {
        var body = await Api(_key).GetFromJsonAsync<JsonElement>("/api/prompts/support-reply-en");

        Assert.Equal(1, body.GetProperty("version").GetInt32());
        Assert.Equal("production", body.GetProperty("status").GetString());
        Assert.Equal("Reply politely to {{customer}} about {{topic}}.", body.GetProperty("text").GetString());
        Assert.Equal(new[] { "customer", "topic" }, body.GetProperty("variables").EnumerateArray().Select(v => v.GetString()));
    }

    [Fact]
    public async Task Latest_label_and_explicit_version()
    {
        var latest = await Api(_key).GetFromJsonAsync<JsonElement>("/api/prompts/support-reply-en?label=latest");
        var v1 = await Api(_key).GetFromJsonAsync<JsonElement>("/api/prompts/support-reply-en?version=1");

        Assert.Equal((2, "draft"), (latest.GetProperty("version").GetInt32(), latest.GetProperty("status").GetString()));
        Assert.Equal(1, v1.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task Promotion_changes_what_the_api_returns()
    {
        await _web.PostFormAsync($"/Prompts/Details/{_promptId}", "/Prompts/Promote", new Dictionary<string, string>
        {
            ["id"] = _promptId.ToString(), ["versionNumber"] = "2"
        });

        var body = await Api(_key).GetFromJsonAsync<JsonElement>("/api/prompts/support-reply-en");

        Assert.Equal(2, body.GetProperty("version").GetInt32());
    }

    [Theory]
    [InlineData("/api/prompts/nope", HttpStatusCode.NotFound)]
    [InlineData("/api/prompts/support-reply-en?version=9", HttpStatusCode.NotFound)]
    [InlineData("/api/prompts/support-reply-en?label=beta", HttpStatusCode.BadRequest)]
    public async Task Bad_requests_get_problem_details(string url, HttpStatusCode expected)
    {
        var response = await Api(_key).GetAsync(url);

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Archived_prompts_are_not_served()
    {
        await _web.PostFormAsync($"/Prompts/Details/{_promptId}", "/Prompts/Archive", new Dictionary<string, string> { ["id"] = _promptId.ToString() });

        Assert.Equal(HttpStatusCode.NotFound, (await Api(_key).GetAsync("/api/prompts/support-reply-en")).StatusCode);
    }

    [Fact]
    public async Task Etag_allows_a_cheap_revalidation()
    {
        var client = Api(_key);
        var first = await client.GetAsync("/api/prompts/support-reply-en");
        var etag = first.Headers.ETag!;
        Assert.Contains("no-cache", first.Headers.CacheControl!.ToString());

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/prompts/support-reply-en");
        request.Headers.IfNoneMatch.Add(etag);
        var second = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
    }

    // ---------- Rendering ----------

    [Fact]
    public async Task Render_fills_in_variables()
    {
        var response = await Api(_key).PostAsJsonAsync("/api/prompts/support-reply-en/render",
            new { variables = new Dictionary<string, string> { ["customer"] = "Olena", ["topic"] = "a refund" } });

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Reply politely to Olena about a refund.", body.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Render_with_missing_values_is_400()
    {
        var response = await Api(_key).PostAsJsonAsync("/api/prompts/support-reply-en/render",
            new { variables = new Dictionary<string, string> { ["customer"] = "Olena" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("topic", await response.Content.ReadAsStringAsync());
    }

    // ---------- Limits and docs ----------

    [Fact]
    public async Task Requests_over_the_limit_get_429()
    {
        var client = Api(_key);
        HttpStatusCode last = HttpStatusCode.OK;
        for (var i = 0; i <= PromptApi.RequestsPerMinute && last != HttpStatusCode.TooManyRequests; i++)
            last = (await client.GetAsync("/api/prompts/support-reply-en")).StatusCode;

        Assert.Equal(HttpStatusCode.TooManyRequests, last);
    }

    [Fact]
    public async Task OpenApi_document_describes_the_endpoints()
    {
        var doc = await _factory.CreateClient().GetStringAsync("/openapi/v1.json");

        Assert.Contains("/api/prompts/{slug}", doc);
        Assert.Contains("/api/prompts/{slug}/render", doc);
    }

    [Fact]
    public async Task Slug_is_shown_and_duplicates_get_a_suffix()
    {
        var create = await _web.PostFormAsync("/Prompts/Create", "/Prompts/Create", new Dictionary<string, string>
        {
            ["Title"] = "Support reply (en)", ["PromptText"] = "Another prompt with the same title text.", ["Category"] = "Learning", ["AiTool"] = "Claude"
        });

        var html = await _web.GetStringAsync(create.Headers.Location!.OriginalString);
        Assert.Contains("data-testid=\"slug\">support-reply-en-2<", html);
    }
}

public class SlugsTests
{
    [Theory]
    [InlineData("Review a diff (v2)!", "review-a-diff-v2")]
    [InlineData("  Café crème  ", "cafe-creme")]
    [InlineData("Привіт світ", "prompt")]
    [InlineData("A--B__C", "a-b-c")]
    public void Builds_url_safe_slugs(string title, string expected)
    {
        Assert.Equal(expected, Slugs.From(title));
    }

    [Fact]
    public void Long_titles_are_cut()
    {
        Assert.True(Slugs.From(new string('a', 200)).Length <= Slugs.MaxLength);
    }

    [Fact]
    public async Task Unique_appends_a_number()
    {
        var taken = new HashSet<string> { "x", "x-2" };

        Assert.Equal("x-3", await Slugs.UniqueAsync("X", s => Task.FromResult(taken.Contains(s))));
    }
}
