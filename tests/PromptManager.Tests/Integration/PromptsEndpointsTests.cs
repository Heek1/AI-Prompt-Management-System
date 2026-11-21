using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PromptManager.Tests.Infrastructure;

namespace PromptManager.Tests.Integration;

public sealed class PromptsEndpointsTests : IDisposable
{
    private readonly PromptManagerFactory _factory = new();
    private readonly HttpClient _client;

    public PromptsEndpointsTests()
    {
        _client = _factory.CreateNoRedirectClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static Dictionary<string, string> ValidPromptForm(string title = "Integration prompt") => new()
    {
        ["Title"] = title,
        ["Description"] = "Created by an integration test",
        ["PromptText"] = "Summarise the following text in three bullet points.",
        ["Category"] = "Documentation",
        ["AiTool"] = "Claude",
        ["Author"] = "alice"
    };

    private async Task<int> CreatePromptViaFormAsync(string title = "Integration prompt")
    {
        var response = await _client.PostFormAsync("/Prompts/Create", "/Prompts/Create", ValidPromptForm(title));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return int.Parse(response.Headers.Location!.OriginalString.Split('/').Last());
    }

    // ---------- Pages ----------

    [Fact]
    public async Task Home_redirects_to_the_library()
    {
        var response = await _client.GetAsync("/Home");

        // Prompts/Index is the default route, so the generated URL is the site root.
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location!.OriginalString);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/Prompts")]
    [InlineData("/Prompts/Create")]
    [InlineData("/History")]
    public async Task Pages_render(string url)
    {
        var response = await _client.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/html", response.Content.Headers.ContentType!.ToString());
    }

    [Fact]
    public async Task Unknown_prompt_shows_the_404_page()
    {
        var response = await _client.GetAsync("/Prompts/Details/999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("does not exist", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Health_returns_json_with_prompt_count()
    {
        await CreatePromptViaFormAsync();

        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, json.RootElement.GetProperty("totalPrompts").GetInt32());
    }

    // ---------- Create ----------

    [Fact]
    public async Task Create_stores_the_prompt_and_redirects_to_details()
    {
        var id = await CreatePromptViaFormAsync("Created through the form");

        var details = await _client.GetStringAsync($"/Prompts/Details/{id}");
        Assert.Contains("Created through the form", details);
    }

    [Fact]
    public async Task Create_with_empty_description_succeeds()
    {
        // Browsers send an empty textarea as an empty value, not as a missing field.
        var form = ValidPromptForm();
        form["Description"] = "";

        var response = await _client.PostFormAsync("/Prompts/Create", "/Prompts/Create", form);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Theory]
    [InlineData("Category", "Poetry")]
    [InlineData("AiTool", "UnknownTool")]
    [InlineData("PromptText", "too short")]
    public async Task Create_with_invalid_field_redisplays_the_form_and_stores_nothing(string field, string value)
    {
        var form = ValidPromptForm();
        form[field] = value;

        var response = await _client.PostFormAsync("/Prompts/Create", "/Prompts/Create", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("field-validation-error", await response.Content.ReadAsStringAsync());
        await using var db = _factory.CreateDbContext();
        Assert.Equal(0, await db.Prompts.CountAsync());
    }

    [Fact]
    public async Task Post_without_antiforgery_token_is_rejected()
    {
        var response = await _client.PostAsync("/Prompts/Create", new FormUrlEncodedContent(ValidPromptForm()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------- Versions ----------

    [Fact]
    public async Task CreateVersion_with_empty_change_note_succeeds()
    {
        var id = await CreatePromptViaFormAsync();

        var response = await _client.PostFormAsync($"/Prompts/CreateVersion/{id}", "/Prompts/CreateVersion", new Dictionary<string, string>
        {
            ["PromptId"] = id.ToString(),
            ["PromptText"] = "A second version with no change note given.",
            ["ChangeNote"] = "",
            ["CreatedBy"] = "bob"
        });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task CreateVersion_adds_version_two()
    {
        var id = await CreatePromptViaFormAsync();

        var response = await _client.PostFormAsync($"/Prompts/CreateVersion/{id}", "/Prompts/CreateVersion", new Dictionary<string, string>
        {
            ["PromptId"] = id.ToString(),
            ["PromptText"] = "Summarise the text in exactly three short bullet points.",
            ["ChangeNote"] = "Tighter wording",
            ["CreatedBy"] = "bob"
        });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        await using var db = _factory.CreateDbContext();
        var prompt = await db.Prompts.Include(p => p.Versions).SingleAsync(p => p.Id == id);
        Assert.Equal(2, prompt.CurrentVersion);
        Assert.Equal(2, prompt.Versions.Count);
    }

    // ---------- Ratings ----------

    [Fact]
    public async Task Rating_twice_with_the_same_name_shows_an_error()
    {
        var id = await CreatePromptViaFormAsync();
        var rating = new Dictionary<string, string>
        {
            ["PromptId"] = id.ToString(), ["UserName"] = "bob", ["Rating"] = "5", ["Comment"] = ""
        };

        var first = await _client.PostFormAsync($"/Prompts/Rate/{id}", "/Prompts/Rate", rating);
        var second = await _client.PostFormAsync($"/Prompts/Rate/{id}", "/Prompts/Rate", rating);

        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Contains("You have already rated this prompt.", await second.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Rating_out_of_range_is_rejected()
    {
        var id = await CreatePromptViaFormAsync();

        var response = await _client.PostFormAsync($"/Prompts/Rate/{id}", "/Prompts/Rate", new Dictionary<string, string>
        {
            ["PromptId"] = id.ToString(), ["UserName"] = "bob", ["Rating"] = "9"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var db = _factory.CreateDbContext();
        Assert.Equal(0, await db.PromptRatings.CountAsync());
    }

    // ---------- Archive and use ----------

    [Fact]
    public async Task Archive_marks_the_prompt_and_hides_the_use_form()
    {
        var id = await CreatePromptViaFormAsync();

        var response = await _client.PostFormAsync($"/Prompts/Details/{id}", "/Prompts/Archive", new Dictionary<string, string>
        {
            ["id"] = id.ToString()
        });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var details = await _client.GetStringAsync($"/Prompts/Details/{id}");
        Assert.DoesNotContain("action=\"/Prompts/UsePrompt\"", details);
        await using var db = _factory.CreateDbContext();
        Assert.True((await db.Prompts.SingleAsync(p => p.Id == id)).IsArchived);
    }

    [Fact]
    public async Task UsePrompt_redirects_back_to_details()
    {
        var id = await CreatePromptViaFormAsync();

        var response = await _client.PostFormAsync($"/Prompts/Details/{id}", "/Prompts/UsePrompt", new Dictionary<string, string>
        {
            ["PromptId"] = id.ToString(), ["UserName"] = "bob"
        });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.EndsWith($"/Prompts/Details/{id}", response.Headers.Location!.OriginalString);
    }

    // ---------- Filters ----------

    [Fact]
    public async Task Library_filters_by_search_term()
    {
        await CreatePromptViaFormAsync("Findable prompt");
        await CreatePromptViaFormAsync("Something else");

        var html = await _client.GetStringAsync("/Prompts?search=Findable");

        Assert.Contains("Findable prompt", html);
        Assert.DoesNotContain("Something else", html);
    }
}
